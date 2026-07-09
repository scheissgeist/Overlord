param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $FlyArgs
)

$ErrorActionPreference = 'Stop'

if (-not $FlyArgs -or $FlyArgs.Count -eq 0) {
    throw 'Usage: scripts/fly.ps1 <flyctl args>, e.g. scripts/fly.ps1 status --app overlord-relay'
}

$flyctl = Get-Command flyctl -ErrorAction Stop

if ([string]::IsNullOrWhiteSpace($env:FLY_API_TOKEN)) {
    $configPath = Join-Path $env:USERPROFILE '.fly\config.yml'
    if (-not (Test-Path -LiteralPath $configPath)) {
        throw "Fly config not found at $configPath. Run flyctl auth login first."
    }

    $configText = Get-Content -LiteralPath $configPath -Raw
    $match = [regex]::Match($configText, '(?m)^access_token:\s*(.+)$')
    if (-not $match.Success) {
        throw "No access_token found in $configPath. Run flyctl auth login first."
    }

    $token = $match.Groups[1].Value.Trim()
    if (($token.StartsWith('"') -and $token.EndsWith('"')) -or ($token.StartsWith("'") -and $token.EndsWith("'"))) {
        $token = $token.Substring(1, $token.Length - 2)
    }

    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "Empty access_token found in $configPath. Run flyctl auth login first."
    }

    $env:FLY_API_TOKEN = $token
}

& $flyctl.Source @FlyArgs
exit $LASTEXITCODE
