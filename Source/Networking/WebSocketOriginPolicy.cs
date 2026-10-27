using System;
using System.Net;

namespace Overlord
{
    /// <summary>
    /// Decides whether a websocket handshake to the embedded (LAN) server may proceed.
    ///
    /// Browsers send Origin on every websocket handshake and do NOT apply the
    /// same-origin policy to websockets, so without a check any web page the streamer
    /// has open could connect to ws://localhost:&lt;port&gt; and act as a viewer.
    /// The viewer page is served by this same server, so a legitimate Origin names
    /// the same host:port the request was sent to.
    ///
    /// Origin == Host alone is beaten by DNS rebinding (evil.example re-pointed at
    /// 127.0.0.1 sends Origin and Host both "evil.example:port"), so the Host must also
    /// be something only the local network resolves: an IP literal, localhost, a
    /// single-label machine name, or a .local mDNS name.
    /// Clients that send no Origin (not a browser) are allowed, as before.
    /// </summary>
    public static class WebSocketOriginPolicy
    {
        public static bool IsAllowed(string origin, string hostHeader)
        {
            if (string.IsNullOrEmpty(origin))
                return true;
            if (string.IsNullOrEmpty(hostHeader))
                return false;

            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri originUri))
                return false;
            if (originUri.Scheme != Uri.UriSchemeHttp && originUri.Scheme != Uri.UriSchemeHttps)
                return false;

            string originHostPort = originUri.IsDefaultPort
                ? originUri.Host
                : originUri.Host + ":" + originUri.Port;
            if (!string.Equals(originHostPort, hostHeader.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            return IsLocalName(originUri.Host);
        }

        private static bool IsLocalName(string host)
        {
            string bare = host.Trim('[', ']');
            if (IPAddress.TryParse(bare, out _))
                return true;
            if (string.Equals(bare, "localhost", StringComparison.OrdinalIgnoreCase))
                return true;
            if (bare.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
                return true;
            return bare.IndexOf('.') < 0;
        }
    }
}
