# Reusable Patterns from Fuck The Base

Source: `e:\Fuck The Base`

## 1. RelayClient Pattern
**File**: `e:/Fuck The Base/Source/Server/RelayClient.cs`

- WebSocket client to Railway relay (wss://...)
- Background thread with connection loop + auto-reconnect (5s interval)
- Thread-safe queue: messages received on background thread, queued with lock, dequeued on main thread in Update()
- Manual JSON parsing with ExtractString/ExtractFloat helpers (no Newtonsoft)
- Message types: viewer_joined, viewer_left, viewer_click, viewer_command
- Outgoing: SendToViewer, Broadcast, BroadcastGameStatus, NotifyViewerSpawned, NotifyViewerDied

## 2. EmbeddedWebServer Pattern
**File**: `e:/Fuck The Base/Source/Server/EmbeddedWebServer.cs`

- HttpListener on port 8420 (configurable)
- Dual interface: HTTP endpoints + WebSocket upgrade
- HTTP routes: GET /, GET /api/status, GET /style.css, GET /game.js
- WebSocket commands: move, attack, pickup, drop, equip, draft, undraft
- ViewerSession dictionary (sessionId → WebSocket + viewer name)
- Callbacks: OnViewerConnected, OnViewerDisconnected, OnCommandReceived
- All HTML/CSS/JS embedded as C# strings
- Windows HttpListener permission fallback (localhost-only if no admin)

## 3. MapRenderer Camera Clone
**File**: `e:/Fuck The Base/Source/Server/MapRenderer.cs`

- Clone game camera settings → RenderTexture
- Output: 400x400px, JPEG quality 65%
- Grid radius: 12 cells around pawn
- Update interval: 0.15s (~6.6 FPS)
- Background compression thread for JPEG encoding
- Lazy initialization (defers until game camera exists)
- Texture2D → EncodeTo/JPEG → base64 string for browser

## 4. ViewerPawnController Command Routing
**File**: `e:/Fuck The Base/Source/Server/ViewerPawnController.cs`

- Thread-safe command queue
- Update() processes queue on main thread
- ExecuteMove() — relative coords → map pos → GoToJob
- ExecuteAttack() — AttackJob or MeleeAttackJob
- ExecutePickup/Drop/Equip — inventory management
- SetDrafted() — draft/undraft
- Coordinate system: relative (0-100) canvas → map coordinates

## 5. Relay Server (Node.js)
**File**: `e:/Fuck The Base/relay-server/server.js`

- Express + ws (WebSocket) library
- Two roles: host (RimWorld game) and viewer (browsers)
- Twitch OAuth flow: /auth/twitch → id.twitch.tv → /auth/callback
- Session tokens for authenticated viewers
- Message forwarding: host↔viewer bidirectional
- Health check: /health (returns hostConnected, viewerCount)
- Periodic stale connection cleanup (30s)

## 6. Build System
**File**: `e:/Fuck The Base/FuckTheBase.csproj`

- Target: net472, C# 7.3, AllowUnsafeBlocks
- Assembly-CSharp: `$(RimWorldPath)\RimWorldWin64_Data\Managed\Assembly-CSharp.dll`
- Unity modules: CoreModule, IMGUIModule, ImageConversionModule, InputLegacyModule, TextRenderingModule
- Harmony: `$(RimWorldPath)\Mods\HarmonyRimWorld\Current\Assemblies\0Harmony.dll`
- All refs Private=false (not bundled)
- Output: Assemblies/ folder

## 7. Harmony Patch Pattern
**File**: `e:/Fuck The Base/Source/Components/HarmonyPatches.cs`

- `[StaticConstructorOnStartup]` class applies patches
- `new Harmony("com.mod.id").PatchAll()` in static constructor
- Postfix pattern: `[HarmonyPatch(typeof(Target), "Method")]`
- Static bool flags for AI override (AIChangingPriorities, AIDesignating, etc.)
- Cheat codes via Input.GetKeyDown in Update patch

## 8. Browser UI
**File**: `e:/Fuck The Base/relay-server/public/index.html`

- Login → Waiting → Game → Dead state machine
- Canvas map with left-click (move) and right-click (attack)
- Normalized coordinates (0-1) sent to server
- WebSocket auto-reconnect with exponential backoff
- Health bar, chat log, action buttons, phase display
- Twitch OAuth button for login
