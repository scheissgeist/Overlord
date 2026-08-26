using System;
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

        public static void Start(string relayUrl, string hostSecret, string hostKey = "")
        {
            if (Interlocked.CompareExchange(ref inFlight, 1, 0) != 0) return;

            string baseUrl = RelayHttp.ToHttpBase(relayUrl);
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
                try { Run(baseUrl, hostSecret, hostKey); }
                catch (Exception e)
                {
                    LogUtil.Warn("Relay probe threw: " + e.Message);
                    Finish(Status.Fail, "Test failed: " + e.Message, "");
                }
            });
        }

        private static void Run(string baseUrl, string hostSecret, string hostKey)
        {
            // Mono's default protocol set is older than what most hosts now accept.
            RelayHttp.EnsureTls();

            string body;
            HttpStatusCode code;
            string error = RelayHttp.Get(baseUrl + "/health", null, out code, out body);

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

            bool twitchConfigured = RelayHttp.FlagTrue(body, "twitch");
            bool guestLogin = RelayHttp.FlagTrue(body, "guest");
            bool hostConnected = RelayHttp.FlagTrue(body, "host");

            string note = hostConnected
                ? "A host is already connected to this relay — if that is not this game, you are sharing a relay and will kick each other off."
                : "No host connected yet; this game claims the slot when you load a save.";

            // A game that JOINED this relay holds a key the relay issued and has no
            // secret at all. Testing it against the admin API used to report "no host
            // secret is set here", which is both false and unactionable on that path -
            // there is no secret to set. Ask the question that actually applies: does
            // this relay still know my key?
            if (!string.IsNullOrEmpty(hostKey))
            {
                string keyBody = "{\"hostKey\":" + RelayHttp.JsonString(hostKey) + "}";
                HttpStatusCode keyCode;
                string keyReply;
                string keyError = RelayHttp.Post(baseUrl + "/api/host/reclaim", keyBody, out keyCode, out keyReply);

                if (keyError != null)
                {
                    Finish(Status.Warn, "Relay is up. Could not check your room.", keyError);
                    return;
                }
                if (keyCode == HttpStatusCode.NotFound)
                {
                    Finish(Status.Warn, "This relay no longer knows your game.",
                           "It was probably restarted or reset. Press Leave this relay, then Join again — you will get a new link to share.");
                    return;
                }
                if (keyCode != HttpStatusCode.OK)
                {
                    Finish(Status.Warn, "Relay is up. Room check was inconclusive.",
                           "The relay answered " + (int)keyCode + " when asked about your room.");
                    return;
                }

                if (guestLogin)
                {
                    Finish(Status.Ok, "Ready. Your room is registered and viewers join by typing a name.",
                           "Anyone with your link can join as any name on this relay. " + note);
                    return;
                }
                if (!twitchConfigured)
                {
                    Finish(Status.Warn, "Your room is fine, but viewers cannot log in.",
                           "This relay has neither Twitch login nor name-only login switched on. That is for whoever runs it to fix — you do not need to do anything.  " + note);
                    return;
                }
                Finish(Status.Ok, "Ready. Your room is registered and viewers can sign in with Twitch.", note);
                return;
            }

            // Secret check. Only meaningful if one is set locally.
            if (string.IsNullOrEmpty(hostSecret))
            {
                Finish(Status.Warn, "Relay is up, but no host secret is set here.",
                       "Click Generate, then put the same value in the relay's HOST_SECRET variable. Without it the relay will reject this game.");
                return;
            }

            string adminError = RelayHttp.Get(baseUrl + "/admin/status", hostSecret, out code, out body);
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

            if (guestLogin)
            {
                Finish(Status.Ok, "Relay reachable, host secret accepted. Viewers join by typing a name.",
                       "Guest login is on and Twitch login is off, so anyone with the URL can join as any name. Good for a first run or a private group; set TWITCH_CLIENT_ID on the relay before a public stream.  " + note);
                return;
            }

            if (!twitchConfigured)
            {
                Finish(Status.Warn, "Relay reachable, secret accepted — but viewers cannot log in.",
                       "The relay has neither TWITCH_CLIENT_ID nor ALLOW_GUEST_LOGIN set, so /auth/twitch returns 503 and there is no name-only path either. Set one of them on the relay. (To play with no server at all, clear the relay URL and use friends mode.)  " + note);
                return;
            }

            Finish(Status.Ok, "Relay reachable, host secret accepted, Twitch login configured.", note);
        }

        private static void Finish(Status s, string head, string det)
        {
            headline = head ?? "";
            detail = det ?? "";
            state = s;
            Interlocked.Exchange(ref inFlight, 0);
        }

    }
}
