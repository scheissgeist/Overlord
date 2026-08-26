using System;
using System.Net;
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

        /// <summary>
        /// Set once by the mod to GUIUtility.systemCopyBuffer. Left null outside
        /// RimWorld so this class stays Unity-free and testable.
        /// </summary>
        public static Action<string> CopyLink;

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

            string baseUrl = RelayHttp.ToHttpBase(relayUrl);
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
            RelayHttp.EnsureTls();

            string body = "{\"label\":" + RelayHttp.JsonString(label ?? "") +
                          ",\"invite\":" + RelayHttp.JsonString(inviteCode ?? "") + "}";

            HttpStatusCode code;
            string reply;
            string error = RelayHttp.Post(baseUrl + "/api/host/register", body, out code, out reply);

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
                       RelayHttp.Field(reply, "error") + " " + RelayHttp.Field(reply, "detail"));
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
                       RelayHttp.Field(reply, "detail"));
                return;
            }

            if (code != HttpStatusCode.OK)
            {
                Finish(Status.Fail, "The relay refused the request (" + (int)code + ").",
                       RelayHttp.Field(reply, "error"));
                return;
            }

            string roomId = RelayHttp.Field(reply, "roomId");
            string hostKey = RelayHttp.Field(reply, "hostKey");
            string viewerPath = RelayHttp.Field(reply, "viewerPath");

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
            // Put it on the clipboard immediately. The link is the entire product of
            // pressing Join, and the next thing anyone does is paste it into a chat -
            // so the Copy button is a step that exists only to be forgotten.
            //
            // Through a delegate rather than calling UnityEngine directly: this file is
            // compiled into a headless harness to verify the friend's join path, and a
            // hard Unity reference here breaks the only test that covers it.
            try { CopyLink?.Invoke(settings.viewerUrl); }
            catch (Exception e) { LogUtil.Warn("Could not copy the link: " + e.Message); }

            Finish(Status.Ok, "You're set up, and your link is already copied:",
                   settings.viewerUrl + "   (paste it anywhere — it opens YOUR colony)");
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
