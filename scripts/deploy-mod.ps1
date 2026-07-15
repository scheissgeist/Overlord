# Installs the built mod into BOTH RimWorld mod paths: DLL *and* content.
#
# Content matters as much as the assembly. EmbeddedWebServer serves the viewer UI
# out of the installed mod's WebUI/ folder, and RimWorld's "Upload on Steam"
# publishes the local Mods/Overlord/ folder verbatim — so a DLL-only deploy
# silently ships a stale viewer UI to subscribers. WebUI drifted 7 commits behind
# that way (2026-07-14) and was caught only by diffing before an upload.
#
# Local Mods/ wins load order over the Workshop copy, so a stale Workshop DLL is
# invisible until Steam re-syncs and silently reverts the mod. Both must match.
#
# A RUNNING RimWorld memory-maps the loaded DLL: overwrite fails, rename succeeds.
# So a failed copy falls back to renaming the live DLL aside and copying in place.
# The new DLL only loads on RimWorld's next restart.

param(
    [switch] $SkipBuild,
    [switch] $Prune      # delete .loaded-old-* leftovers from earlier swaps
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $SkipBuild) {
    Write-Host 'Building Overlord ...'
    dotnet build Overlord.csproj -c Release -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
}

# repo folder -> installed mod folder. Mirrors build.bat's xcopy set.
$content = @(
    @{ Src = 'About';               Dst = 'About' },
    @{ Src = 'Defs';                Dst = 'Defs' },
    @{ Src = 'Textures';            Dst = 'Textures' },
    @{ Src = 'relay-server\public'; Dst = 'WebUI' }
)

$dll = Join-Path $root 'Assemblies\Overlord.dll'

# Verify every source BEFORE touching any install. Without this a bad path can
# rename the live DLL aside and then fail to replace it, leaving RimWorld no mod.
if (-not (Test-Path $dll)) { throw "Missing $dll - build did not produce output" }
foreach ($c in $content) {
    if (-not (Test-Path $c.Src)) { throw "Missing source content: $($c.Src)" }
}

$modRoots = @(
    'C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\Overlord',
    'C:\Program Files (x86)\Steam\steamapps\workshop\content\294100\3760983440'
)

$srcHash = (Get-FileHash $dll -Algorithm SHA256).Hash
Write-Host "Source $($srcHash.Substring(0,16))  $((Get-Item $dll).Length) bytes"
Write-Host ''

$failed = @()
foreach ($modRoot in $modRoots) {
    if (-not (Test-Path $modRoot)) {
        Write-Host "SKIP     $modRoot (not installed)"
        continue
    }
    Write-Host $modRoot

    foreach ($c in $content) {
        $dst = Join-Path $modRoot $c.Dst
        if (-not (Test-Path $dst)) { New-Item -ItemType Directory -Path $dst -Force | Out-Null }
        Copy-Item (Join-Path $c.Src '*') $dst -Recurse -Force
        Write-Host "  content  $($c.Dst)"
    }

    $dest = Join-Path $modRoot 'Assemblies\Overlord.dll'
    if (-not (Test-Path (Split-Path $dest))) {
        New-Item -ItemType Directory -Path (Split-Path $dest) -Force | Out-Null
    }
    try {
        Copy-Item $dll $dest -Force -ErrorAction Stop
        Write-Host '  dll      OK'
    }
    catch {
        # Locked by a running game -> rename aside, then copy.
        $aside = Join-Path (Split-Path $dest) "Overlord.dll.loaded-old-$(Get-Date -Format 'HHmmss')"
        Rename-Item $dest $aside -ErrorAction Stop
        try {
            Copy-Item $dll $dest -Force -ErrorAction Stop
            Write-Host '  dll      SWAPPED (game running - restart RimWorld to load)'
        }
        catch {
            # Put the live DLL back rather than leaving the install empty.
            Rename-Item $aside $dest
            $failed += $modRoot
            Write-Host "  dll      FAIL (restored previous DLL): $_"
        }
    }

    if ($Prune) {
        Get-ChildItem (Join-Path $modRoot 'Assemblies') -Filter 'Overlord.dll.*old*' -ErrorAction SilentlyContinue |
            ForEach-Object { Remove-Item $_.FullName -Force; Write-Host "  pruned   $($_.Name)" }
    }
}

# Verify. A copy that "succeeded" but does not match means the install is running
# other code / serving other assets than HEAD.
Write-Host ''
foreach ($modRoot in $modRoots) {
    if (-not (Test-Path $modRoot)) { continue }

    $dest = Join-Path $modRoot 'Assemblies\Overlord.dll'
    if (Test-Path $dest) {
        $h = (Get-FileHash $dest -Algorithm SHA256).Hash
        if ($h -eq $srcHash) { Write-Host "match   dll      $($h.Substring(0,16))" }
        else { Write-Host "STALE   dll      $($h.Substring(0,16))"; $failed += $modRoot }
    }

    foreach ($c in $content) {
        $dst = Join-Path $modRoot $c.Dst
        # Compare-Object over relative path + hash: catches edits AND missing files.
        $hashTree = {
            param($base)
            Get-ChildItem $base -Recurse -File | ForEach-Object {
                '{0}|{1}' -f $_.FullName.Substring($base.Length).TrimStart('\'),
                           (Get-FileHash $_.FullName -Algorithm SHA256).Hash
            }
        }
        $a = & $hashTree (Resolve-Path $c.Src).Path
        $b = & $hashTree (Resolve-Path $dst).Path
        $drift = @(Compare-Object $a $b | Where-Object SideIndicator -eq '<=')
        if ($drift.Count -eq 0) { Write-Host "match   $($c.Dst)" }
        else {
            Write-Host "STALE   $($c.Dst) - $($drift.Count) file(s) differ or missing"
            $drift | Select-Object -First 5 | ForEach-Object { Write-Host "          $($_.InputObject.Split('|')[0])" }
            $failed += $modRoot
        }
    }
    Write-Host ''
}

if ($failed.Count -gt 0) { throw "Deploy incomplete: $(($failed | Select-Object -Unique) -join '; ')" }
Write-Host 'Both installs match HEAD (dll + content). Restart RimWorld to load.'
Write-Host 'Workshop upload ships Mods\Overlord verbatim: RimWorld -> Mods (dev mode) -> Upload on Steam.'
