# Installs the PawnIO driver if it's missing. Run by Kelvin's installer (already elevated).
#
# PawnIO is the signed kernel driver that LibreHardwareMonitor and OpenRGB use to reach the
# motherboard's fan chip and SMBus (RAM and board lighting). Its license doesn't grant
# redistribution, so Kelvin doesn't ship it: this downloads the official release and checks it
# against a pinned SHA-256 before running it. The same file and checksum winget uses.
#
# Exit codes: 0 = installed or already present, 3010 = installed, reboot needed, other = failed.
# A failure never blocks Kelvin's install; Kelvin shows a notice until the driver is present.

$ErrorActionPreference = 'Continue'
$Url = 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe'
$Sha256 = '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'
$log = Join-Path $env:ProgramData 'Kelvin\install-pawnio.log'
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null
function Log($m) { "$(Get-Date -Format s)  $m" | Add-Content $log }

if (Get-Service -Name 'PawnIO' -ErrorAction SilentlyContinue) {
  Log 'PawnIO already installed; nothing to do.'
  exit 0
}

$dir = Join-Path $env:TEMP "kelvin-pawnio-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $dir | Out-Null
$exe = Join-Path $dir 'PawnIO_setup.exe'
try {
  [Net.ServicePointManager]::SecurityProtocol = 'Tls12'
  Log "Downloading $Url"
  Invoke-WebRequest $Url -OutFile $exe -UseBasicParsing -TimeoutSec 120
  $actual = (Get-FileHash $exe -Algorithm SHA256).Hash
  if ($actual -ne $Sha256) {
    Log "Checksum mismatch (got $actual). Not running it."
    exit 2
  }
  Log 'Checksum OK. Installing silently.'
  $p = Start-Process -FilePath $exe -ArgumentList '-install', '-silent' -Wait -PassThru
  Log "PawnIO setup exited with $($p.ExitCode)."
  exit $p.ExitCode
} catch {
  Log "Failed: $_"
  exit 1
} finally {
  Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
}
