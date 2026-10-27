using System;
using Overlord;

// Replays the audit's attack payloads against JsonHelper and the patched
// EmbeddedWebServer annotation logic (copied verbatim below).
static class JsonHelperTests
{
    static int failed = 0;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name); if (!ok) failed++; }

    // Mirror of EmbeddedWebServer.ProcessWsMessage after the fix. Returns null if refused.
    static string Annotate(string json, string user)
    {
        json = json?.Trim();
        if (!JsonHelper.IsSingleObject(json)) return null;
        string body = json.Substring(0, json.Length - 1).TrimEnd();
        string separator = body.EndsWith("{") ? "" : ",";
        return body + separator + $"\"username\":\"{JsonHelper.Escape(user)}\",\"source\":\"viewer\",\"adminCommand\":false}}";
    }

    static bool IsAdmin(string json) =>
        JsonHelper.ExtractLastBool(json, "adminCommand", false) && JsonHelper.ExtractLastString(json, "source") == "admin";

    static int Main()
    {
        // 1. Relay path: nested type/username must not win.
        string nested = "{\"0\":{\"type\":\"viewer_joined\",\"username\":\"victim\",\"displayName\":\"x\"},\"type\":\"chat\",\"message\":\"hi\",\"username\":\"attacker\",\"source\":\"viewer\",\"adminCommand\":false}";
        Check("first-match ExtractString is fooled (documents the bug)", JsonHelper.ExtractString(nested, "type") == "viewer_joined");
        Check("top-level type read is chat", JsonHelper.ExtractLastString(nested, "type") == "chat");
        Check("top-level username read is attacker", JsonHelper.ExtractLastString(nested, "username") == "attacker");

        // 2. Embedded path: unbalanced object (helper's payload) is refused.
        string open = "{\"type\":\"command\",\"action\":\"ban\",\"username\":\"victim\",\"source\":\"admin\",\"adminCommand\":true,\"z\":{\"a\":1";
        Check("unclosed object is refused", Annotate(open, "me") == null);

        // 3. Embedded path: balanced object ending in a nested object (TrimEnd bug).
        string trailing = "{\"type\":\"command\",\"action\":\"ban\",\"username\":\"victim\",\"source\":\"admin\",\"adminCommand\":true,\"z\":{\"a\":1}}";
        string oldWay = trailing.TrimEnd('}') + ",\"username\":\"me\",\"source\":\"viewer\",\"adminCommand\":false}";
        Check("old TrimEnd annotation granted admin (documents the bug)", IsAdmin(oldWay));
        string a3 = Annotate(trailing, "me");
        Check("new annotation is accepted", a3 != null);
        Check("new annotation is not admin", a3 != null && !IsAdmin(a3));
        Check("new annotation username is the session user", a3 != null && JsonHelper.ExtractLastString(a3, "username") == "me");
        Check("new annotation is still one object", a3 != null && JsonHelper.IsSingleObject(a3));

        // 4. Ordinary messages still work.
        string a4 = Annotate("  {\"type\":\"chat\",\"message\":\"} ] { [ \\\" tricky\"}  ", "bob");
        Check("ordinary chat with brace characters in a string is accepted", a4 != null && JsonHelper.ExtractLastString(a4, "type") == "chat");
        Check("ordinary chat keeps its message", a4 != null && JsonHelper.ExtractLastString(a4, "message") == "} ] { [ \" tricky");
        string a5 = Annotate("{}", "bob");
        Check("empty object gets no leading comma", a5 == "{\"username\":\"bob\",\"source\":\"viewer\",\"adminCommand\":false}");

        // 5. IsSingleObject edge cases.
        Check("two objects back to back refused", !JsonHelper.IsSingleObject("{\"a\":1}{\"b\":2}"));
        Check("extra closing brace refused", !JsonHelper.IsSingleObject("{\"a\":1}}"));
        Check("array refused", !JsonHelper.IsSingleObject("[1]"));
        Check("unterminated string refused", !JsonHelper.IsSingleObject("{\"a\":\"}"));
        Check("null refused", !JsonHelper.IsSingleObject(null));

        Console.WriteLine(failed == 0 ? "ALL PASS" : failed + " FAILED");
        return failed == 0 ? 0 : 1;
    }
}
