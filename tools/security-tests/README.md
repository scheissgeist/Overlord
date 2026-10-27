# Security regression tests (C#)

`JsonHelperTests.cs` replays the 2026-10-27 audit's attack payloads against
`Source/Core/JsonHelper.cs` and a verbatim copy of the annotation logic in
`EmbeddedWebServer.ProcessWsMessage`. It needs only the C# compiler, not RimWorld:

```
mcs -out:jsonhelper-tests.exe Source/Core/JsonHelper.cs tools/security-tests/JsonHelperTests.cs
mono jsonhelper-tests.exe
```

If `ProcessWsMessage` changes, update the copy in `Annotate()` to match.
