using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Overlord
{
    /// <summary>
    /// WebSocket client that connects to the Railway relay server as "host".
    /// Background thread receives messages, queues them for main-thread processing.
    /// Handles message fragmentation (multi-frame messages).
    /// </summary>
    public class RelayClient
    {
        private ClientWebSocket webSocket;
        private CancellationTokenSource cts;
        private Thread receiveThread;
        private Thread sendThread;

        private string relayUrl;
        private string hostSecret;
        private volatile bool isConnected;
        private volatile bool shouldReconnect;
        private volatile bool senderRunning;
        private const float ReconnectIntervalSec = 5f;
        private const int MaxGeneralQueuedMessages = 250;
        private const int MaxQueuedFrames = 6;

        // Send lock to prevent concurrent SendAsync calls
        private readonly object sendLock = new object();

        private readonly ThreadSafeQueue<Action> mainThreadQueue = new ThreadSafeQueue<Action>();
        private readonly ThreadSafeQueue<OutgoingMessage> outgoingQueue = new ThreadSafeQueue<OutgoingMessage>();

        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<string> OnMessageReceived;

        public RelayClient(string serverUrl, string secret = "")
        {
            relayUrl = NormalizeUrl(serverUrl);
            hostSecret = secret ?? "";
        }

        public void Connect()
        {
            if (isConnected)
                return;

            shouldReconnect = true;
            cts = new CancellationTokenSource();

            receiveThread = new Thread(ConnectionLoop)
            {
                IsBackground = true,
                Name = "Overlord_RelayClient"
            };
            receiveThread.Start();
            StartSender();

            LogUtil.Log($"Relay client starting connection to {relayUrl}");
        }

        public void Disconnect()
        {
            shouldReconnect = false;
            senderRunning = false;
            cts?.Cancel();

            try
            {
                if (webSocket != null && webSocket.State == WebSocketState.Open)
                {
                    webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None).Wait(2000);
                }
            }
            catch { }

            webSocket?.Dispose();
            webSocket = null;
            isConnected = false;
            outgoingQueue.Clear();
            sendThread?.Join(1500);
            sendThread = null;
        }

        public void ProcessQueue()
        {
            mainThreadQueue.DrainTo(action => action());
        }

        public void Send(string json)
        {
            if (!isConnected || webSocket == null || webSocket.State != WebSocketState.Open)
                return;

            int queued = outgoingQueue.Count;
            bool isMapFrame = json.IndexOf("\"type\":\"map_frame\"", StringComparison.Ordinal) >= 0;
            if (ShouldDropQueuedMessage(queued, isMapFrame))
                return;

            outgoingQueue.Enqueue(new OutgoingMessage
            {
                Text = json,
                MessageType = WebSocketMessageType.Text
            });
        }

        public void SendBinaryMapFrame(Dictionary<string, object> metadata, byte[] jpeg)
        {
            if (!isConnected || webSocket == null || webSocket.State != WebSocketState.Open)
                return;

            int queued = outgoingQueue.Count;
            if (ShouldDropQueuedMessage(queued, true))
                return;

            try
            {
                outgoingQueue.Enqueue(new OutgoingMessage
                {
                    Bytes = BinaryFrameProtocol.EncodeMapFrame(metadata, jpeg),
                    MessageType = WebSocketMessageType.Binary
                });
            }
            catch (Exception ex)
            {
                LogUtil.Warn($"Failed to encode binary map frame: {ex.Message}");
            }
        }

        private void StartSender()
        {
            if (senderRunning)
                return;

            senderRunning = true;
            sendThread = new Thread(SendLoop)
            {
                IsBackground = true,
                Name = "Overlord_RelaySend"
            };
            sendThread.Start();
        }

        private void SendLoop()
        {
            while (senderRunning)
            {
                if (outgoingQueue.Count > 0)
                    outgoingQueue.DrainTo(SendNow);
                else
                    Thread.Sleep(5);
            }
        }

        private static bool ShouldDropQueuedMessage(int queued, bool isMapFrame)
        {
            return queued > MaxGeneralQueuedMessages || (isMapFrame && queued > MaxQueuedFrames);
        }

        private void SendNow(OutgoingMessage message)
        {
            var socket = webSocket;
            if (!isConnected || socket == null || socket.State != WebSocketState.Open)
                return;

            lock (sendLock)
            {
                try
                {
                    byte[] bytes = message.Bytes ?? Encoding.UTF8.GetBytes(message.Text ?? "");
                    var task = socket.SendAsync(new ArraySegment<byte>(bytes), message.MessageType, true, CancellationToken.None);
                    task.Wait(5000);
                }
                catch (Exception ex)
                {
                    LogUtil.Warn($"Failed to send: {ex.Message}");
                }
            }
        }

        public void SendToViewer(string username, Dictionary<string, object> message)
        {
            var copy = new Dictionary<string, object>(message);
            copy["target"] = username;
            Send(JsonHelper.ToJson(copy));
        }

        public void Broadcast(Dictionary<string, object> message)
        {
            Send(JsonHelper.ToJson(message));
        }

        public bool IsConnected => isConnected;

        private class OutgoingMessage
        {
            public string Text;
            public byte[] Bytes;
            public WebSocketMessageType MessageType;
        }

        private async void ConnectionLoop()
        {
            while (shouldReconnect && !cts.Token.IsCancellationRequested)
            {
                try
                {
                    webSocket = new ClientWebSocket();
                    string connectUrl = string.IsNullOrEmpty(hostSecret)
                        ? $"{relayUrl}?role=host"
                        : $"{relayUrl}?role=host&secret={Uri.EscapeDataString(hostSecret)}";

                    LogUtil.Log($"Connecting to relay: {connectUrl}");
                    await webSocket.ConnectAsync(new Uri(connectUrl), cts.Token);

                    if (webSocket.State == WebSocketState.Open)
                    {
                        isConnected = true;
                        mainThreadQueue.Enqueue(() => OnConnected?.Invoke());

                        var buffer = new byte[16384];
                        var messageBuffer = new StringBuilder();

                        while (webSocket.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
                        {
                            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

                            if (result.MessageType == WebSocketMessageType.Close)
                                break;

                            if (result.MessageType == WebSocketMessageType.Text)
                            {
                                messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                                // Only process when we have the complete message
                                if (result.EndOfMessage)
                                {
                                    string message = messageBuffer.ToString();
                                    messageBuffer.Clear();
                                    mainThreadQueue.Enqueue(() => OnMessageReceived?.Invoke(message));
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogUtil.Warn($"Relay connection error: {ex.Message}");
                }

                isConnected = false;
                mainThreadQueue.Enqueue(() => OnDisconnected?.Invoke());

                webSocket?.Dispose();
                webSocket = null;

                if (shouldReconnect && !cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay((int)(ReconnectIntervalSec * 1000), cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private static string NormalizeUrl(string url)
        {
            url = url.TrimEnd('/');
            if (url.StartsWith("https://"))
                url = "wss://" + url.Substring(8);
            else if (url.StartsWith("http://"))
                url = "ws://" + url.Substring(7);
            else if (!url.StartsWith("ws://") && !url.StartsWith("wss://"))
                url = "wss://" + url;

            // Ensure /ws path is present (relay server listens on /ws)
            if (!url.EndsWith("/ws"))
                url += "/ws";

            return url;
        }
    }
}
