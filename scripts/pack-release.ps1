param(
    [Parameter(Mandatory = $true)]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "Building Overlord $Version ..."
dotnet build Overlord.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$dll = Join-Path $root 'Assemblies\Overlord.dll'
if (-not (Test-Path $dll)) { throw "Missing $dll" }

$stage = Join-Path $root "dist\Overlord-$Version"
$zip = Join-Path $root "dist\Overlord-$Version.zip"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
if (Test-Path $zip) { Remove-Item -Force $zip }
New-Item -ItemType Directory -Path $stage | Out-Null

Copy-Item -Recurse (Join-Path $root 'About') (Join-Path $stage 'About')
Copy-Item -Recurse (Join-Path $root 'Assemblies') (Join-Path $stage 'Assemblies')
if (Test-Path (Join-Path $root 'Defs')) {
    Copy-Item -Recurse (Join-Path $root 'Defs') (Join-Path $stage 'Defs')
}
if (Test-Path (Join-Path $root 'Textures')) {
    Copy-Item -Recurse (Join-Path $root 'Textures') (Join-Path $stage 'Textures')
}
$webuiSrc = Join-Path $root 'relay-server\public'
if (-not (Test-Path $webuiSrc)) { throw "Missing viewer UI at $webuiSrc" }
Copy-Item -Recurse $webuiSrc (Join-Path $stage 'WebUI')

# Do not ship private docs or AGENTS inside the mod zip
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Write-Host "Release package: $zip"
Write-Host "Upload this file on GitHub Releases (tag v$Version)."
Write-Host "Streamers still need their own relay - see docs/SELF_HOST.md"
