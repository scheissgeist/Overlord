# Installs the freshly-built Overlord.dll into BOTH RimWorld mod paths.
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

$dll = Join-Path $root 'Assemblies\Overlord.dll'
# Verify the source BEFORE touching any install. Without this a bad path renames
# the live DLL aside and then fails to replace it, leaving RimWorld with no mod.
if (-not (Test-Path $dll)) { throw "Missing $dll - build did not produce output" }

$targets = @(
    'C:\Program Files (x86)\Steam\steamapps\common\RimWorld\Mods\Overlord\Assemblies',
    'C:\Program Files (x86)\Steam\steamapps\workshop\content\294100\3760983440\Assemblies'
)

$srcHash = (Get-FileHash $dll -Algorithm SHA256).Hash
Write-Host "Source $($srcHash.Substring(0,16))  $((Get-Item $dll).Length) bytes"

$failed = @()
foreach ($dir in $targets) {
    if (-not (Test-Path $dir)) {
        Write-Host "SKIP     $dir (not installed)"
        continue
    }
    $dest = Join-Path $dir 'Overlord.dll'
    try {
        Copy-Item $dll $dest -Force -ErrorAction Stop
        Write-Host "OK       $dir"
    }
    catch {
        # Locked by a running game -> rename aside, then copy.
        $aside = Join-Path $dir "Overlord.dll.loaded-old-$(Get-Date -Format 'HHmmss')"
        Rename-Item $dest $aside -ErrorAction Stop
        try {
            Copy-Item $dll $dest -Force -ErrorAction Stop
            Write-Host "SWAPPED  $dir (game running - restart RimWorld to load)"
        }
        catch {
            # Put the live DLL back rather than leaving the install empty.
            Rename-Item $aside $dest
            $failed += $dir
            Write-Host "FAIL     $dir (restored previous DLL): $_"
        }
    }
    if ($Prune) {
        Get-ChildItem $dir -Filter 'Overlord.dll.*old*' -ErrorAction SilentlyContinue |
            ForEach-Object { Remove-Item $_.FullName -Force; Write-Host "  pruned $($_.Name)" }
    }
}

# A copy that "succeeded" but does not match means the install is running other code.
Write-Host ''
foreach ($dir in $targets) {
    $dest = Join-Path $dir 'Overlord.dll'
    if (-not (Test-Path $dest)) { continue }
    $h = (Get-FileHash $dest -Algorithm SHA256).Hash
    $mark = if ($h -eq $srcHash) { 'match  ' } else { 'STALE  ' }
    Write-Host "$mark $($h.Substring(0,16))  $dest"
    if ($h -ne $srcHash) { $failed += $dir }
}

if ($failed.Count -gt 0) { throw "Deploy incomplete: $($failed -join '; ')" }
Write-Host ''
Write-Host 'Both installs match HEAD. Restart RimWorld to load.'
