# Builds the Kelvin Windows installer.
#   1. Downloads pinned, checksum-verified OpenRGB (bundled; Kelvin runs it in the background).
#   2. Publishes the .NET hardware service as a self-contained exe (no .NET install needed).
#   3. Builds the Tauri desktop app and packages everything into one NSIS installer.
# The PawnIO driver is not bundled (its license doesn't grant redistribution); the installer
# downloads it from the official release at install time and verifies its checksum.
#
# Requires: .NET 10 SDK, Rust, Tauri CLI (cargo install tauri-cli --version "^2" --locked),
# and the MSVC C++ Build Tools.
#
# Usage:  ./build-installer.ps1
# Output: <cargo target dir>\release\bundle\nsis\Kelvin_<version>_x64-setup.exe

# Native tools (cargo, dotnet) print progress on stderr, which Windows PowerShell 5.1 would treat as
# a terminating error under 'Stop'. Each step checks $LASTEXITCODE instead.
$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot
$serviceOut = Join-Path $root 'desktop\service-dist'
$deps = Join-Path $root 'desktop\deps'

# Third-party pieces Kelvin ships with. Pinned by version AND checksum: if an upstream file ever
# changes, the build stops instead of shipping something nobody reviewed.
$OpenRgb = @{
  Url     = 'https://github.com/CalcProgrammer1/OpenRGB/releases/download/release_1.1/OpenRGB_1.1_Windows_64_8e16de2.zip'
  Sha256  = '63291726C6FC84EB479EF2EE752E97E1187F6CE9C49FD27EEE40DED588A8172A'
  License = 'https://raw.githubusercontent.com/CalcProgrammer1/OpenRGB/release_1.1/LICENSE'
  LicenseSha256 = '7687B2F71C25336B0273F5C175286EBBC12D89ADD925B2F6C333E51FA3B1782D'
}

function Get-Pinned([string]$Url, [string]$Sha256, [string]$OutFile) {
  if ((Test-Path $OutFile) -and (Get-FileHash $OutFile -Algorithm SHA256).Hash -eq $Sha256) { return }
  Write-Host "    downloading $Url"
  [Net.ServicePointManager]::SecurityProtocol = 'Tls12'
  Invoke-WebRequest $Url -OutFile $OutFile -UseBasicParsing
  $actual = (Get-FileHash $OutFile -Algorithm SHA256).Hash
  if ($actual -ne $Sha256) {
    Remove-Item $OutFile -Force
    throw "Checksum mismatch for $Url`n  expected $Sha256`n  got      $actual"
  }
}

Write-Host '==> Fetching bundled dependencies' -ForegroundColor Cyan
New-Item -ItemType Directory -Force (Join-Path $deps 'cache') | Out-Null
$zip = Join-Path $deps 'cache\openrgb.zip'
Get-Pinned $OpenRgb.Url $OpenRgb.Sha256 $zip
$openRgbDir = Join-Path $deps 'openrgb'
if (Test-Path $openRgbDir) { Remove-Item -Recurse -Force $openRgbDir }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$extract = Join-Path $deps 'cache\openrgb-extract'
if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
[IO.Compression.ZipFile]::ExtractToDirectory($zip, $extract)
# The zip holds a single "OpenRGB Windows 64-bit" folder; ship its contents as deps\openrgb.
Move-Item (Get-ChildItem $extract -Directory | Select-Object -First 1).FullName $openRgbDir
$license = Join-Path $deps 'cache\openrgb-LICENSE.txt'
Get-Pinned $OpenRgb.License $OpenRgb.LicenseSha256 $license
Copy-Item $license (Join-Path $openRgbDir 'LICENSE.txt')

Write-Host '==> Publishing the Kelvin service' -ForegroundColor Cyan
if (Test-Path $serviceOut) { Remove-Item -Recurse -Force $serviceOut }
dotnet publish (Join-Path $root 'src\Kelvin.Service') `
  -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none `
  -o $serviceOut
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# The desktop app bundles its own copy of the UI; the service's copy is only for browser use.
Remove-Item -Recurse -Force (Join-Path $serviceOut 'wwwroot') -ErrorAction SilentlyContinue

Write-Host '==> Building the desktop app and installer' -ForegroundColor Cyan
Push-Location (Join-Path $root 'desktop')
try {
  cargo tauri build
  if ($LASTEXITCODE -ne 0) { throw 'cargo tauri build failed' }
} finally {
  Pop-Location
}

$target = if ($env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR } else { Join-Path $root 'desktop\target' }
$installer = Get-ChildItem (Join-Path $target 'release\bundle\nsis\*-setup.exe') | Sort-Object LastWriteTime | Select-Object -Last 1
Write-Host "==> Installer: $($installer.FullName) ($([math]::Round($installer.Length / 1MB, 1)) MB)" -ForegroundColor Green
