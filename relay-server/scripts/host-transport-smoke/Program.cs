using System;
using System.Collections.Generic;
using System.Threading;

namespace Overlord
{
    internal static class Program
    {
        private static volatile bool commandReceived;
        private static string failure;

        private static int Main(string[] args)
        {
            if (args.Length != 3)
            {
                Console.Error.WriteLine("usage: HostTransportSmoke RELAY_URL HOST_KEY VIEWER_LOGIN");
                return 2;
            }

            string relayUrl = args[0];
            string hostKey = args[1];
            string viewerLogin = args[2];
            var relay = new RelayClient(relayUrl, "", hostKey);

            relay.OnConnected += () => Console.WriteLine("HOST_CONNECTED");
            relay.OnMessageReceived += json =>
            {
                string type = JsonHelper.ExtractString(json, "type");
                if (type == StateProtocol.ViewerJoined
                    && JsonHelper.ExtractLastString(json, "username") == viewerLogin)
                {
                    bool capabilitiesQueued = relay.SendToViewer(viewerLogin, new Dictionary<string, object>
                    {
                        ["type"] = StateProtocol.HostCapabilities,
                        ["rimworldVersion"] = "smoke-real-host-transport",
                        ["work"] = true,
                        ["schedule"] = true,
                        ["contextMenu"] = true
                    });

                    var session = new ViewerSession { preferredWeaponDef = "Gun_SmokeRifle" };
                    bool pawnStateQueued = relay.SendToViewer(viewerLogin, StateProtocol.BuildPawnStateMessage(
                        session,
                        "{\"id\":4242,\"name\":\"Clean Profile\",\"drafted\":false}"
                    ));
                    Console.WriteLine("HOST_STATE_SENT capabilities=" + capabilitiesQueued + " pawn=" + pawnStateQueued);
                }
                else if (type == StateProtocol.Command)
                {
                    string commandId = JsonHelper.ExtractLastString(json, "commandId");
                    string username = JsonHelper.ExtractLastString(json, "username");
                    string source = JsonHelper.ExtractLastString(json, "source");
                    bool admin = JsonHelper.ExtractLastBool(json, "adminCommand", true);
                    string action = JsonHelper.ExtractString(json, "action");
                    if (username != viewerLogin || source != "viewer" || admin || action != StateProtocol.CmdDraft
                        || string.IsNullOrEmpty(commandId))
                    {
                        failure = "relay did not pin viewer command identity";
                        return;
                    }
                    relay.SendToViewer(viewerLogin, new Dictionary<string, object>
                    {
                        ["type"] = StateProtocol.ActionResult,
                        ["commandId"] = commandId,
                        ["action"] = action,
                        ["phase"] = "applied",
                        ["ok"] = true,
                        ["message"] = "Drafted"
                    });
                    commandReceived = true;
                    Console.WriteLine("HOST_COMMAND_RECEIVED");
                }
            };

            relay.Connect();
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && !commandReceived && failure == null)
            {
                relay.ProcessQueue();
                Thread.Sleep(10);
            }
            if (commandReceived) Thread.Sleep(250);
            relay.ProcessQueue();
            relay.Disconnect();

            if (failure != null)
            {
                Console.Error.WriteLine(failure);
                return 1;
            }
            if (!commandReceived)
            {
                Console.Error.WriteLine("timed out waiting for viewer command");
                return 1;
            }

            Console.WriteLine("HOST_SMOKE_OK");
            return 0;
        }
    }
}
