using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Verse;

namespace Overlord
{
    /// <summary>
    /// Fallback local HTTP + WebSocket server when no relay URL is configured.
    /// Viewers connect directly to the streamer's machine and use the canonical web UI.
    /// Static assets are served from the installed mod's WebUI folder, with a dev fallback
    /// to relay-server/public when running from the repo.
    /// </summary>
    public class EmbeddedWebServer
    {
        private const string CanonicalUiFolder = "WebUI";

        private static readonly Dictionary<string, string> ContentTypes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".html"] = "text/html; charset=utf-8",
                [".js"] = "application/javascript; charset=utf-8",
                [".css"] = "text/css; charset=utf-8",
                [".json"] = "application/json; charset=utf-8",
                [".svg"] = "image/svg+xml",
                [".png"] = "image/png",
                [".jpg"] = "image/jpeg",
                [".jpeg"] = "image/jpeg",
                [".ico"] = "image/x-icon",
                [".txt"] = "text/plain; charset=utf-8",
            };

        public static EmbeddedWebServer Instance { get; private set; }

        private HttpListener listener;
        private CancellationTokenSource cts;
        private Thread serverThread;
        private readonly int port;
        private volatile bool running;
        private string cachedAssetRoot;
        private bool assetRootLogged;

        // sessionId -> (ws, username)
        private readonly Dictionary<string, (WebSocket ws, string username)> wsSessions =
            new Dictionary<string, (WebSocket ws, string username)>();
        private readonly object sessionsLock = new object();
        private static readonly Dictionary<WebSocket, int> pendingSendCounts = new Dictionary<WebSocket, int>();
        private static readonly object pendingSendLock = new object();
        private const int MaxPendingMapFrameSends = 1;
        private const int MaxPendingGeneralSends = 16;

        // Callbacks into game logic (set by GameComponent)
        public event Action<string, string> OnViewerConnected;   // username, displayName
        public event Action<string> OnViewerDisconnected;        // username
        public event Action<string, string> OnCommandReceived;   // username, rawJson

        public EmbeddedWebServer(int serverPort)
        {
            Instance = this;
            port = serverPort;
        }

        public void Start()
        {
            if (running)
                return;

            cts = new CancellationTokenSource();
            listener = new HttpListener();

            try
            {
                listener.Prefixes.Add($"http://+:{port}/");
                listener.Start();
                LogUtil.Log($"Embedded web server started on port {port} (all interfaces)");
            }
            catch
            {
                try
                {
                    listener = new HttpListener();
                    listener.Prefixes.Add($"http://localhost:{port}/");
                    listener.Start();
                    LogUtil.Log($"Embedded web server started on localhost:{port} (local only - run as admin for LAN access)");
                }
                catch (Exception ex)
                {
                    LogUtil.Error($"Failed to start embedded web server: {ex.Message}");
                    return;
                }
            }

            ResolveAssetRoot();

            running = true;
            serverThread = new Thread(ServerLoop)
            {
                IsBackground = true,
                Name = "Overlord_WebServer"
            };
            serverThread.Start();
        }

        public void Stop()
        {
            running = false;
            cts?.Cancel();
            try { listener?.Stop(); } catch { }

            lock (sessionsLock)
            {
                foreach (var kvp in wsSessions)
                {
                    try
                    {
                        kvp.Value.ws.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Server stopping",
                            CancellationToken.None
                        );
                    }
                    catch { }
                }

                wsSessions.Clear();
            }
        }

        public void SendToViewer(string username, string json)
        {
            lock (sessionsLock)
            {
                foreach (var kvp in wsSessions)
                {
                    if (kvp.Value.username == username)
                    {
                        SendToSocket(kvp.Value.ws, json);
                        return;
                    }
                }
            }
        }

        public void Broadcast(string json)
        {
            lock (sessionsLock)
            {
                foreach (var kvp in wsSessions)
                    SendToSocket(kvp.Value.ws, json);
            }
        }

        public int ConnectedCount
        {
            get { lock (sessionsLock) { return wsSessions.Count; } }
        }

        public bool IsRunning => running;
        public int Port => port;

        private void ServerLoop()
        {
            while (running)
            {
                try
                {
                    var ctx = listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (running)
                        LogUtil.Warn($"Web server request error: {ex.Message}");
                }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            try
            {
                if (req.IsWebSocketRequest)
                {
                    HandleWebSocket(ctx);
                    return;
                }

                string path = NormalizeRequestPath(req.Url.AbsolutePath);
                if (path == "/health")
                {
                    ServeString(res, "application/json; charset=utf-8", $"{{\"ok\":true,\"viewers\":{ConnectedCount}}}");
                    return;
                }

                if (TryServeStaticAsset(res, path))
                    return;

                res.StatusCode = path == "/index.html" ? 500 : 404;
                string body = path == "/index.html"
                    ? BuildMissingUiPage()
                    : "Not Found";
                string contentType = path == "/index.html" ? "text/html; charset=utf-8" : "text/plain; charset=utf-8";
                ServeString(res, contentType, body);
            }
            catch (Exception ex)
            {
                LogUtil.Warn($"HTTP request error: {ex.Message}");
                try
                {
                    res.StatusCode = 500;
                    ServeString(res, "text/plain; charset=utf-8", "Server error");
                }
                catch { }
            }
        }

        private bool TryServeStaticAsset(HttpListenerResponse res, string requestPath)
        {
            string assetRoot = ResolveAssetRoot();
            if (string.IsNullOrEmpty(assetRoot))
                return false;

            string relativePath = requestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(relativePath))
                relativePath = "index.html";

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(assetRoot, relativePath));
            }
            catch
            {
                return false;
            }

            if (!fullPath.StartsWith(assetRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!File.Exists(fullPath))
                return false;

            string extension = Path.GetExtension(fullPath);
            string contentType = ContentTypes.TryGetValue(extension, out var foundType)
                ? foundType
                : "application/octet-stream";

            if (string.Equals(extension, ".html", StringComparison.OrdinalIgnoreCase))
            {
                string html = File.ReadAllText(fullPath, Encoding.UTF8);
                if (string.Equals(relativePath, "index.html", StringComparison.OrdinalIgnoreCase))
                    html = InjectEmbeddedMode(html);

                ServeString(res, contentType, html);
                return true;
            }

            byte[] bytes = File.ReadAllBytes(fullPath);
            ServeBytes(res, contentType, bytes);
            return true;
        }

        private string ResolveAssetRoot()
        {
            if (!string.IsNullOrEmpty(cachedAssetRoot) && Directory.Exists(cachedAssetRoot))
                return cachedAssetRoot;

            foreach (var candidate in GetAssetRootCandidates())
            {
                if (string.IsNullOrEmpty(candidate))
                    continue;

                try
                {
                    string fullPath = Path.GetFullPath(candidate);
                    if (!Directory.Exists(fullPath))
                        continue;

                    cachedAssetRoot = fullPath;
                    if (!assetRootLogged)
                    {
                        LogUtil.Log($"Embedded web UI root: {cachedAssetRoot}");
                        assetRootLogged = true;
                    }
                    return cachedAssetRoot;
                }
                catch { }
            }

            if (!assetRootLogged)
            {
                LogUtil.Warn("Embedded web UI assets not found. Expected WebUI/ or relay-server/public/ in the mod root.");
                assetRootLogged = true;
            }

            return null;
        }

        private IEnumerable<string> GetAssetRootCandidates()
        {
            string modRoot = OverlordMod.Instance?.Content?.RootDir;
            if (!string.IsNullOrEmpty(modRoot))
            {
                yield return Path.Combine(modRoot, CanonicalUiFolder);
                yield return Path.Combine(modRoot, "relay-server", "public");
            }

            string currentDir = Environment.CurrentDirectory;
            if (!string.IsNullOrEmpty(currentDir))
            {
                yield return Path.Combine(currentDir, CanonicalUiFolder);
                yield return Path.Combine(currentDir, "relay-server", "public");
            }
        }

        private static string NormalizeRequestPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
                return "/index.html";

            if (path == "/admin")
                return "/admin.html";

            if (path == "/obs")
                return "/obs.html";

            return path;
        }

        private static string InjectEmbeddedMode(string html)
        {
            if (string.IsNullOrEmpty(html))
                return html;

            if (html.IndexOf("data-embedded-mode=", StringComparison.OrdinalIgnoreCase) >= 0)
                return html;

            const string twitchMarker = "data-twitch-client-id=\"\"";
            if (html.Contains(twitchMarker))
            {
                return html.Replace(
                    twitchMarker,
                    "data-twitch-client-id=\"\" data-embedded-mode=\"true\""
                );
            }

            int bodyIndex = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            if (bodyIndex < 0)
                return html;

            int bodyEnd = html.IndexOf('>', bodyIndex);
            if (bodyEnd < 0)
                return html;

            return html.Insert(bodyEnd, " data-embedded-mode=\"true\"");
        }

        private static string BuildMissingUiPage()
        {
            return @"<!DOCTYPE html>
<html lang=""en"">
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <title>Overlord Embedded UI Missing</title>
  <style>
    body { margin: 0; padding: 32px; background: #121214; color: #e0e0e4; font: 14px/1.5 system-ui, sans-serif; }
    .box { max-width: 720px; margin: 0 auto; padding: 24px; border: 1px solid #303036; background: #1c1c20; }
    h1 { margin: 0 0 12px; font-size: 22px; }
    p { margin: 0 0 12px; color: #a0a0a8; }
    code { color: #fbbf24; }
  </style>
</head>
<body>
  <div class=""box"">
    <h1>Embedded UI assets missing</h1>
    <p>The embedded web server is running, but it could not find the canonical Overlord frontend.</p>
    <p>Expected a <code>WebUI</code> folder in the installed mod, or a development fallback at <code>relay-server/public</code>.</p>
    <p>Reinstall the mod assets or run the project install script so the frontend files are copied into the mod directory.</p>
  </div>
</body>
</html>";
        }

        private static void ServeString(HttpListenerResponse res, string contentType, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body ?? "");
            ServeBytes(res, contentType, bytes);
        }

        private static void ServeBytes(HttpListenerResponse res, string contentType, byte[] body)
        {
            byte[] bytes = body ?? Array.Empty<byte>();
            res.ContentType = contentType;
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.Close();
        }

        private async void HandleWebSocket(HttpListenerContext ctx)
        {
            WebSocket ws = null;
            string sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
            string username = null;

            try
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                ws = wsCtx.WebSocket;

                lock (sessionsLock)
                    wsSessions[sessionId] = (ws, null);

                var msgBuf = new byte[65536];
                var msgBuilder = new StringBuilder();

                while (ws.State == WebSocketState.Open && running)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(msgBuf), cts.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        msgBuilder.Append(Encoding.UTF8.GetString(msgBuf, 0, result.Count));
                        if (result.EndOfMessage)
                        {
                            string msg = msgBuilder.ToString();
                            msgBuilder.Clear();
                            username = ProcessWsMessage(sessionId, ws, username, msg);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogUtil.Warn($"WebSocket error: {ex.Message}");
            }
            finally
            {
                bool stillConnectedAsSameUser = false;
                lock (sessionsLock)
                {
                    wsSessions.Remove(sessionId);
                    if (username != null)
                    {
                        foreach (var existing in wsSessions.Values)
                        {
                            if (existing.username == username && existing.ws?.State == WebSocketState.Open)
                            {
                                stillConnectedAsSameUser = true;
                                break;
                            }
                        }
                    }
                }

                if (username != null && !stillConnectedAsSameUser)
                    OnViewerDisconnected?.Invoke(username);

                try { ws?.Dispose(); } catch { }
            }
        }

        /// <summary>Returns the possibly updated username after processing.</summary>
        private string ProcessWsMessage(string sessionId, WebSocket ws, string currentUsername, string json)
        {
            string type = JsonHelper.ExtractString(json, "type");
            if (type == null)
                return currentUsername;

            if (type == "auth")
            {
                string user = JsonHelper.ExtractString(json, "username");
                string display = JsonHelper.ExtractString(json, "displayName") ?? user;
                if (user == null)
                {
                    SendToSocket(ws, "{\"type\":\"error\",\"message\":\"Missing username\"}");
                    return currentUsername;
                }

                // Block username hijack: if another open socket already owns this name,
                // reject so a second client cannot issue commands as that viewer.
                lock (sessionsLock)
                {
                    foreach (var existing in wsSessions)
                    {
                        if (existing.Key == sessionId) continue;
                        if (!string.Equals(existing.Value.username, user, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (existing.Value.ws != null && existing.Value.ws.State == WebSocketState.Open)
                        {
                            SendToSocket(ws, "{\"type\":\"error\",\"message\":\"That username is already connected\"}");
                            return currentUsername;
                        }
                    }
                    wsSessions[sessionId] = (ws, user);
                }

                SendToSocket(ws, $"{{\"type\":\"auth_ok\",\"username\":\"{JsonHelper.Escape(user)}\"}}");
                OnViewerConnected?.Invoke(user, display);
                return user;
            }

            if (currentUsername == null)
            {
                SendToSocket(ws, "{\"type\":\"error\",\"message\":\"Not authenticated\"}");
                return currentUsername;
            }

            string annotated = json.TrimEnd('}') +
                $",\"username\":\"{JsonHelper.Escape(currentUsername)}\",\"source\":\"viewer\",\"adminCommand\":false}}";
            OnCommandReceived?.Invoke(currentUsername, annotated);
            return currentUsername;
        }

        private static async void SendToSocket(WebSocket ws, string json)
        {
            if (ws == null || ws.State != WebSocketState.Open)
                return;

            bool isMapFrame = json != null &&
                json.IndexOf("\"type\":\"map_frame\"", StringComparison.Ordinal) >= 0;
            if (!TryBeginSocketSend(ws, isMapFrame))
                return;

            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None
                );
            }
            catch { }
            finally
            {
                EndSocketSend(ws);
            }
        }

        private static bool TryBeginSocketSend(WebSocket ws, bool isMapFrame)
        {
            lock (pendingSendLock)
            {
                pendingSendCounts.TryGetValue(ws, out int pending);
                int limit = isMapFrame ? MaxPendingMapFrameSends : MaxPendingGeneralSends;
                if (pending >= limit)
                    return false;

                pendingSendCounts[ws] = pending + 1;
                return true;
            }
        }

        private static void EndSocketSend(WebSocket ws)
        {
            lock (pendingSendLock)
            {
                if (!pendingSendCounts.TryGetValue(ws, out int pending))
                    return;

                pending--;
                if (pending <= 0)
                    pendingSendCounts.Remove(ws);
                else
                    pendingSendCounts[ws] = pending;
            }
        }
    }
}
