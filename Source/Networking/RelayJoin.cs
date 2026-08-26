using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Overlord
{
    /// <summary>
    /// "Join this relay" — the one button a streamer who is not the relay operator
    /// ever needs to press.
    ///
    /// Why this exists: hosting used to require registering a Twitch application,
    /// deploying a Node server, inventing a HOST_SECRET and matching it in two
    /// places. That is a developer workflow, and most people asked to do it simply
    /// do not. This collapses it to: paste the address a friend sent you, press
    /// Join. The relay issues a room and a key, the mod stores them, and the mod
    /// hands back a link to paste into chat. No secret is ever shown or typed.
    ///
    /// Threading matches RelayProbe: the request runs on a ThreadPool thread, the
    /// GUI only reads fields. Nothing here runs unless the button is pressed.
    /// </summary>
    public static class RelayJoin
    {
        public enum Status
        {
            Idle,
            Running,
            Ok,
            Fail,
        }

        private static volatile Status state = Status.Idle;
        private static volatile string headline = "";
        private static volatile string detail = "";
        private static int inFlight;

        public static Status State => state;
        public static string Headline => headline;
        public static string Detail => detail;
        public static bool IsRunning => state == Status.Running;

        public static void Reset()
        {
            if (state == Status.Running) return;
            state = Status.Idle;
            headline = "";
            detail = "";
        }

        /// <summary>
        /// Registers this game with the relay at <paramref name="relayUrl"/>. On
        /// success the settings are written directly — the streamer never copies a
        /// value from one place to another, which is where setup used to break.
        /// </summary>
        public static void Start(string relayUrl, string label, string inviteCode)
        {
            if (Interlocked.CompareExchange(ref inFlight, 1, 0) != 0) return;

            string baseUrl = ToHttpBase(relayUrl);
            if (string.IsNullOrEmpty(baseUrl))
            {
                Finish(Status.Fail, "No relay address to join.",
                       "Paste the address the person running the relay gave you, then press Join.");
                return;
            }

            state = Status.Running;
            headline = "Joining " + baseUrl + " ...";
            detail = "";

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Run(baseUrl, label, inviteCode); }
                catch (Exception e)
                {
                    LogUtil.Warn("Relay join threw: " + e.Message);
                    Finish(Status.Fail, "Could not join: " + e.Message, "");
                }
            });
        }

        private static void Run(string baseUrl, string label, string inviteCode)
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (NotSupportedException) { }

            string body = "{\"label\":" + JsonString(label ?? "") +
                          ",\"invite\":" + JsonString(inviteCode ?? "") + "}";

            HttpStatusCode code;
            string reply;
            string error = Post(baseUrl + "/api/host/register", body, out code, out reply);

            if (error != null)
            {
                Finish(Status.Fail, "Cannot reach that relay.",
                       error + "  —  check the address is exactly what you were given, including https://");
                return;
            }

            if (code == HttpStatusCode.Forbidden)
            {
                // 403 covers both "closed relay" and "wrong invite code"; the relay's
                // own message distinguishes them, so pass it through rather than guess.
                Finish(Status.Fail, "That relay is not accepting other hosts.",
                       ExtractField(reply, "error") + " " + ExtractField(reply, "detail"));
                return;
            }

            if ((int)code == 429)
            {
                Finish(Status.Fail, "Too many attempts.", "Wait a few minutes and press Join again.");
                return;
            }

            if (code == HttpStatusCode.ServiceUnavailable)
            {
                Finish(Status.Fail, "That relay is full right now.",
                       ExtractField(reply, "detail"));
                return;
            }

            if (code != HttpStatusCode.OK)
            {
                Finish(Status.Fail, "The relay refused the request (" + (int)code + ").",
                       ExtractField(reply, "error"));
                return;
            }

            string roomId = ExtractField(reply, "roomId");
            string hostKey = ExtractField(reply, "hostKey");
            string viewerPath = ExtractField(reply, "viewerPath");

            if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(hostKey))
            {
                Finish(Status.Fail, "That address answered, but not like an Overlord relay.",
                       "It did not return a room to host in.");
                return;
            }

            var settings = OverlordMod.Settings;
            settings.relayUrl = baseUrl;
            settings.roomId = roomId;
            settings.hostKey = hostKey;
            settings.viewerUrl = baseUrl + (string.IsNullOrEmpty(viewerPath) ? "/g/" + roomId : viewerPath);
            settings.Write();

            LogUtil.Log("Joined relay " + baseUrl + " as room " + roomId);
            Finish(Status.Ok, "You're set up. Send people this link:",
                   settings.viewerUrl);
        }

        private static string Post(string url, string body, out HttpStatusCode code, out string reply)
        {
            code = 0;
            reply = null;
            HttpWebResponse response = null;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 12000;
                request.ReadWriteTimeout = 12000;
                request.UserAgent = "Overlord-mod-join";
                byte[] payload = Encoding.UTF8.GetBytes(body);
                request.ContentLength = payload.Length;
                using (var stream = request.GetRequestStream())
                    stream.Write(payload, 0, payload.Length);

                response = (HttpWebResponse)request.GetResponse();
            }
            catch (WebException we)
            {
                response = we.Response as HttpWebResponse;
                if (response == null) return Describe(we);
            }
            catch (UriFormatException)
            {
                return "That is not a valid web address.";
            }
            catch (NotSupportedException e)
            {
                return e.Message;
            }

            try
            {
                code = response.StatusCode;
                using (var stream = response.GetResponseStream())
                {
                    if (stream != null)
                    {
                        using (var reader = new StreamReader(stream))
                        {
                            char[] buffer = new char[8192];
                            int read = reader.Read(buffer, 0, buffer.Length);
                            reply = read > 0 ? new string(buffer, 0, read) : "";
                        }
                    }
                }
            }
            catch (Exception e)
            {
                return "Could not read the reply: " + e.Message;
            }
            finally
            {
                response.Close();
            }

            return null;
        }

        private static string Describe(WebException we)
        {
            switch (we.Status)
            {
                case WebExceptionStatus.NameResolutionFailure:
                    return "That host name does not resolve — check for a typo.";
                case WebExceptionStatus.ConnectFailure:
                    return "Nothing accepted a connection at that address.";
                case WebExceptionStatus.Timeout:
                    return "Timed out. A relay on a free tier can be asleep — try once more.";
                case WebExceptionStatus.TrustFailure:
                case WebExceptionStatus.SecureChannelFailure:
                    return "The HTTPS certificate could not be validated.";
                default:
                    return we.Message;
            }
        }

        /// <summary>
        /// Minimal extractor for the few flat string fields the relay returns. A full
        /// JSON parser is not worth pulling in for four keys, but this is deliberately
        /// strict: it only matches "key":"value" at the top level of a small reply.
        /// </summary>
        private static string ExtractField(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";
            string needle = "\"" + key + "\"";
            int at = json.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) return "";
            int colon = json.IndexOf(':', at + needle.Length);
            if (colon < 0) return "";
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return "";
            i++;
            var sb = new StringBuilder();
            while (i < json.Length && json[i] != '"')
            {
                if (json[i] == '\\' && i + 1 < json.Length)
                {
                    i++;
                    sb.Append(json[i] == 'n' ? '\n' : json[i]);
                }
                else sb.Append(json[i]);
                i++;
            }
            return sb.ToString();
        }

        private static string JsonString(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in value)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c < 32) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        private static void Finish(Status s, string head, string det)
        {
            headline = head ?? "";
            detail = det ?? "";
            state = s;
            Interlocked.Exchange(ref inFlight, 0);
        }

        /// <summary>Same normalisation as RelayProbe — see the trim incident there.</summary>
        private static string ToHttpBase(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            url = url.Trim().TrimEnd('/');
            if (url.Length == 0) return null;
            if (url.EndsWith("/ws")) url = url.Substring(0, url.Length - 3).TrimEnd('/');
            if (url.StartsWith("wss://")) url = "https://" + url.Substring(6);
            else if (url.StartsWith("ws://")) url = "http://" + url.Substring(5);
            else if (!url.StartsWith("http://") && !url.StartsWith("https://")) url = "https://" + url;
            return url.TrimEnd('/');
        }
    }
}
