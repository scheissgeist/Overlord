using System;
using System.IO;
using System.Net;
using System.Threading;

namespace Overlord
{
    /// <summary>
    /// One-shot "Test connection" probe for the mod settings screen.
    ///
    /// Why this exists: relay setup is a five-step loop across two developer accounts,
    /// and until a save is loaded the settings screen can only say "not started yet".
    /// A streamer who typo'd the URL or mismatched the secret had no way to find out
    /// except loading a game and watching nothing happen — which reads as "the mod is
    /// broken", not "you have a typo". This makes the two failures nameable from the
    /// main menu, before a save exists.
    ///
    /// Two requests, because they answer two different questions:
    ///   GET /health        — unauthenticated. Is the URL right and the relay running?
    ///                        Also reports whether TWITCH_CLIENT_ID is set, which is
    ///                        the difference between "viewers can log in" and "viewers
    ///                        see 'Twitch auth is not configured'".
    ///   GET /admin/status  — Bearer HOST_SECRET. The relay's admin auth fails closed
    ///                        (server.js adminAuth), so 200 means the secret matches
    ///                        and 401 means it does not. This is the only check that
    ///                        can tell a wrong secret from a right one without loading
    ///                        a save and trying to claim the host slot.
    ///
    /// Threading: the request runs on a ThreadPool thread and writes plain fields the
    /// GUI reads. The settings window only reads them — no per-frame work, no main
    /// thread I/O. Nothing here runs unless the button is clicked.
    /// </summary>
    public static class RelayProbe
    {
        public enum Status
        {
            Idle,
            Running,
            Ok,      // relay reachable AND secret accepted
            Warn,    // relay reachable, but something is off (secret rejected, no Twitch id)
            Fail,    // could not reach the relay at all
        }

        // Written by the probe thread, read by the settings GUI. Reference and enum
        // writes are atomic on all runtimes RimWorld ships on; volatile keeps the GUI
        // thread from caching a stale copy across frames.
        private static volatile Status state = Status.Idle;
        private static volatile string headline = "";
        private static volatile string detail = "";
        private static int inFlight; // 0/1 via Interlocked — one probe at a time

        public static Status State => state;
        public static string Headline => headline;
        public static string Detail => detail;
        public static bool IsRunning => state == Status.Running;

        /// <summary>Clears the last result. Called when the URL or secret is edited.</summary>
        public static void Reset()
        {
            if (state == Status.Running) return;
            state = Status.Idle;
            headline = "";
            detail = "";
        }

        public static void Start(string relayUrl, string hostSecret)
        {
            if (Interlocked.CompareExchange(ref inFlight, 1, 0) != 0) return;

            string baseUrl = ToHttpBase(relayUrl);
            if (string.IsNullOrEmpty(baseUrl))
            {
                Finish(Status.Fail, "No relay URL to test.",
                       "Enter your relay's address above, or leave it blank to play in friends mode.");
                return;
            }

            state = Status.Running;
            headline = "Testing " + baseUrl + " ...";
            detail = "";

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Run(baseUrl, hostSecret); }
                catch (Exception e)
                {
                    LogUtil.Warn("Relay probe threw: " + e.Message);
                    Finish(Status.Fail, "Test failed: " + e.Message, "");
                }
            });
        }

        private static void Run(string baseUrl, string hostSecret)
        {
            // Mono's default protocol set is older than what most hosts now accept.
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (NotSupportedException)
            {
                // Older runtime without Tls12 in the enum — leave the default alone.
            }

            string body;
            HttpStatusCode code;
            string error = Get(baseUrl + "/health", null, out code, out body);

            if (error != null)
            {
                Finish(Status.Fail, "Cannot reach the relay.",
                       error + "  —  check the URL is exactly what your host gave you (including https://), and that the service is running.");
                return;
            }

            if (code != HttpStatusCode.OK)
            {
                Finish(Status.Fail, "Reached that address, but it is not an Overlord relay.",
                       "GET /health returned " + (int)code + ". A relay answers with {\"ok\":true,...}.");
                return;
            }

            if (body == null || body.IndexOf("\"ok\":true", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Finish(Status.Fail, "Reached that address, but it is not an Overlord relay.",
                       "GET /health did not return an Overlord health payload.");
                return;
            }

            bool twitchConfigured = body.IndexOf("\"twitch\":true", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hostConnected = body.IndexOf("\"host\":true", StringComparison.OrdinalIgnoreCase) >= 0;

            // Secret check. Only meaningful if one is set locally.
            if (string.IsNullOrEmpty(hostSecret))
            {
                Finish(Status.Warn, "Relay is up, but no host secret is set here.",
                       "Click Generate, then put the same value in the relay's HOST_SECRET variable. Without it the relay will reject this game.");
                return;
            }

            string adminError = Get(baseUrl + "/admin/status", hostSecret, out code, out body);
            if (adminError != null)
            {
                Finish(Status.Warn, "Relay is up. Could not verify the host secret.",
                       adminError);
                return;
            }

            if (code == HttpStatusCode.Unauthorized)
            {
                Finish(Status.Warn, "Relay is up, but it rejected your host secret.",
                       "The Host secret here and HOST_SECRET on the relay are not the same value. Copy one into the other — no quotes, no trailing space.");
                return;
            }

            if (code != HttpStatusCode.OK)
            {
                Finish(Status.Warn, "Relay is up. Host secret check was inconclusive.",
                       "GET /admin/status returned " + (int)code + ".");
                return;
            }

            string note = hostConnected
                ? "A host is already connected to this relay — if that is not this game, you are sharing a relay and will kick each other off."
                : "No host connected yet; this game claims the slot when you load a save.";

            if (!twitchConfigured)
            {
                Finish(Status.Warn, "Relay reachable, secret accepted — but viewers cannot log in.",
                       "TWITCH_CLIENT_ID is not set on the relay, so /auth/twitch returns 503. Set it from dev.twitch.tv/console/apps. (To play with no Twitch at all, clear the relay URL instead and use friends mode.)  " + note);
                return;
            }

            Finish(Status.Ok, "Relay reachable, host secret accepted, Twitch login configured.", note);
        }

        /// <summary>
        /// Returns null on a completed request (check <paramref name="code"/>), or a
        /// human-readable reason the request never completed.
        /// </summary>
        private static string Get(string url, string bearer, out HttpStatusCode code, out string body)
        {
            code = 0;
            body = null;
            HttpWebResponse response = null;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = 8000;
                request.ReadWriteTimeout = 8000;
                request.UserAgent = "Overlord-mod-setup-probe";
                if (!string.IsNullOrEmpty(bearer))
                    request.Headers["Authorization"] = "Bearer " + bearer;

                response = (HttpWebResponse)request.GetResponse();
            }
            catch (WebException we)
            {
                // A 401/404/500 arrives here too, with the response attached. That is a
                // completed request and the caller wants the status code, not an error.
                response = we.Response as HttpWebResponse;
                if (response == null)
                    return Describe(we);
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
                            body = read > 0 ? new string(buffer, 0, read) : "";
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
                    return "That host name does not resolve — check for a typo in the address.";
                case WebExceptionStatus.ConnectFailure:
                    return "Nothing accepted a connection at that address. The relay may be stopped or asleep.";
                case WebExceptionStatus.Timeout:
                    return "Timed out after 8 seconds. A free-tier relay that has gone to sleep can take longer to wake — try once more.";
                case WebExceptionStatus.TrustFailure:
                case WebExceptionStatus.SecureChannelFailure:
                    return "The HTTPS certificate could not be validated.";
                default:
                    return we.Message;
            }
        }

        private static void Finish(Status s, string head, string det)
        {
            headline = head ?? "";
            detail = det ?? "";
            state = s;
            Interlocked.Exchange(ref inFlight, 0);
        }

        /// <summary>
        /// Mirrors RelayClient.NormalizeUrl in reverse: the settings field may hold
        /// wss://, ws://, https://, http:// or a bare host, and the REST endpoints live
        /// on http(s) at the root. Bare hosts default to https, matching the client's
        /// default of wss.
        /// </summary>
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
