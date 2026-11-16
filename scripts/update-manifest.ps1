# Signs an installer for Kelvin's updater and writes latest.json, the file the app checks.
#
# Run it on the FINAL installer (after code signing, if any), because the update signature
# covers the exact bytes users download. Needs the updater key:
#   TAURI_SIGNING_PRIVATE_KEY           key text, or set by build-installer.ps1 from %USERPROFILE%\.kelvin-signing
#   TAURI_SIGNING_PRIVATE_KEY_PASSWORD  its password
#
# Usage: ./scripts/update-manifest.ps1 -Installer path\to\Kelvin_0.2.0_x64-setup.exe [-Notes "text"]
# Output: <installer>.sig and latest.json next to the installer.

param(
  [Parameter(Mandatory)][string]$Installer,
  [string]$Notes = '',
  [string]$Repository = 'nanwer/kelvin'
)
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent

if (-not $env:TAURI_SIGNING_PRIVATE_KEY) { throw 'TAURI_SIGNING_PRIVATE_KEY is not set; the update cannot be signed.' }
$Installer = (Resolve-Path $Installer).Path
$version = (Get-Content (Join-Path $root 'desktop\tauri.conf.json') -Raw | ConvertFrom-Json).version
$name = Split-Path $Installer -Leaf

# Sign the final bytes. (Tauri's bundler also writes a .sig, but code signing changes the file.)
Remove-Item "$Installer.sig" -ErrorAction SilentlyContinue
# --app-version binds the signature to this version, so an old installer can't be replayed as an update.
cargo tauri signer sign --app-version $version $Installer
if ($LASTEXITCODE -ne 0 -or -not (Test-Path "$Installer.sig")) { throw 'Signing the update failed.' }
$signature = (Get-Content "$Installer.sig" -Raw).Trim()

$url = "https://github.com/$Repository/releases/download/v$version/$name"
$entry = [ordered]@{ signature = $signature; url = $url }
$manifest = [ordered]@{
  version   = $version
  notes     = $Notes
  pub_date  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
  platforms = [ordered]@{ 'windows-x86_64' = $entry; 'windows-x86_64-nsis' = $entry }
}
$out = Join-Path (Split-Path $Installer) 'latest.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $out -Encoding utf8
Write-Host "==> Update manifest: $out (version $version)" -ForegroundColor Green
