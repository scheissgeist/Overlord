using System;
using Overlord;

// Origin/Host cases for the embedded server's websocket gate.
static class WebSocketOriginPolicyTests
{
    static int failed = 0;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name); if (!ok) failed++; }

    static int Main()
    {
        Check("LAN viewer page on an IP", WebSocketOriginPolicy.IsAllowed("http://192.168.1.20:8421", "192.168.1.20:8421"));
        Check("streamer on localhost", WebSocketOriginPolicy.IsAllowed("http://localhost:8421", "localhost:8421"));
        Check("IPv6 loopback", WebSocketOriginPolicy.IsAllowed("http://[::1]:8421", "[::1]:8421"));
        Check("machine name", WebSocketOriginPolicy.IsAllowed("http://desktop-abc:8421", "DESKTOP-ABC:8421"));
        Check("mDNS name", WebSocketOriginPolicy.IsAllowed("http://gamepc.local:8421", "gamepc.local:8421"));
        Check("non-browser client (no Origin)", WebSocketOriginPolicy.IsAllowed(null, "localhost:8421"));
        Check("foreign site refused", !WebSocketOriginPolicy.IsAllowed("https://evil.example", "localhost:8421"));
        Check("DNS rebinding refused", !WebSocketOriginPolicy.IsAllowed("http://evil.example:8421", "evil.example:8421"));
        Check("other port on same host refused", !WebSocketOriginPolicy.IsAllowed("http://localhost:9999", "localhost:8421"));
        Check("file:// origin refused", !WebSocketOriginPolicy.IsAllowed("null", "localhost:8421"));
        Check("missing Host refused", !WebSocketOriginPolicy.IsAllowed("http://localhost:8421", null));
        Console.WriteLine(failed == 0 ? "ALL PASS" : failed + " FAILED");
        return failed == 0 ? 0 : 1;
    }
}
