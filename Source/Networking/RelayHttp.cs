using System;
using System.IO;
using System.Net;
using System.Text;

namespace Overlord
{
    /// <summary>
    /// The small HTTP surface the mod uses to talk to a relay outside the WebSocket:
    /// health, room registration, room reclaim. Shared by RelayProbe and RelayJoin,
    /// which had grown their own byte-identical copies of Get/Post/Describe/JsonString.
    ///
    /// Everything here runs on a ThreadPool thread. Nothing here touches Unity or
    /// RimWorld state, so it is safe off the main thread and safe to unit-test outside
    /// the game — which is how both callers are actually verified.
    /// </summary>
    internal static class RelayHttp
    {
        private const int TimeoutMs = 12000;

        /// <summary>Enable TLS 1.2 where the runtime knows about it.</summary>
        internal static void EnsureTls()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (NotSupportedException) { /* older runtime; leave the default */ }
        }

        /// <summary>
        /// Returns null when the request COMPLETED (check <paramref name="code"/>), or a
        /// human-readable reason it never completed. A 4xx/5xx is a completed request —
        /// the caller wants the status, not an error string.
        /// </summary>
        internal static string Get(string url, string bearer, out HttpStatusCode code, out string body)
        {
            return Send("GET", url, bearer, null, out code, out body);
        }

        internal static string Post(string url, string json, out HttpStatusCode code, out string body)
        {
            return Send("POST", url, null, json, out code, out body);
        }

        private static string Send(string method, string url, string bearer, string json,
                                   out HttpStatusCode code, out string body)
        {
            code = 0;
            body = null;
            HttpWebResponse response = null;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = method;
                request.Timeout = TimeoutMs;
                request.ReadWriteTimeout = TimeoutMs;
                request.UserAgent = "Overlord-mod";
                if (!string.IsNullOrEmpty(bearer))
                    request.Headers["Authorization"] = "Bearer " + bearer;

                if (json != null)
                {
                    request.ContentType = "application/json";
                    byte[] payload = Encoding.UTF8.GetBytes(json);
                    request.ContentLength = payload.Length;
                    using (var stream = request.GetRequestStream())
                        stream.Write(payload, 0, payload.Length);
                }

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

        /// <summary>Network failures in words a streamer can act on.</summary>
        internal static string Describe(WebException we)
        {
            switch (we.Status)
            {
                case WebExceptionStatus.NameResolutionFailure:
                    return "That host name does not resolve — check for a typo in the address.";
                case WebExceptionStatus.ConnectFailure:
                    return "Nothing accepted a connection at that address. The relay may be stopped or asleep.";
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
        /// Minimal extractor for the flat string fields a relay returns. A full JSON
        /// parser is not worth pulling into a RimWorld mod for four keys, and this is
        /// deliberately strict: only "key":"value" in a small, known reply.
        /// </summary>
        internal static string Field(string json, string key)
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

        /// <summary>True when a flat boolean field is present and true.</summary>
        internal static bool FlagTrue(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return false;
            return json.IndexOf("\"" + key + "\":true", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Whatever a streamer pasted -> the http(s) base the REST endpoints live on.
        /// Accepts wss://, ws://, http(s):// or a bare host, with or without a trailing
        /// /ws or slash. Bare hosts default to https, matching RelayClient's default of
        /// wss.
        ///
        /// The .Trim() is load-bearing and was once missing here: RelayProbe trimmed and
        /// RelayClient did not, so a URL pasted with a trailing space made
        /// "Test connection" go green while the game never connected - the settings
        /// screen contradicting itself. One copy now, so the two cannot drift again.
        /// </summary>
        internal static string ToHttpBase(string url)
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

        internal static string JsonString(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in value ?? string.Empty)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c < 32) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
