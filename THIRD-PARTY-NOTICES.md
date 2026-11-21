# Third-party software in Kelvin

Kelvin includes or installs the following software. Each keeps its own license.

## Shipped inside Kelvin

### OpenRGB 1.1
- License: GNU General Public License v2.0. The full text is in `openrgb\LICENSE.txt`.
- Source: https://github.com/CalcProgrammer1/OpenRGB/tree/release_1.1
  (also https://codeberg.org/OpenRGB/OpenRGB)
- Shipped unmodified, from the official release `OpenRGB_1.1_Windows_64_8e16de2.zip`.
- Kelvin runs OpenRGB as a separate program and talks to it over its network SDK. Kelvin's own
  code is not a derivative of OpenRGB.

### LibreHardwareMonitorLib
- License: Mozilla Public License 2.0
- Source: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor

### OpenRGB.NET
- License: MIT
- Source: https://github.com/diogotr7/OpenRGB.NET

### .NET runtime and ASP.NET Core
- License: MIT
- Source: https://github.com/dotnet/runtime, https://github.com/dotnet/aspnetcore

### Tauri
- License: MIT or Apache 2.0
- Source: https://github.com/tauri-apps/tauri

### Wix Madefor Display and Wix Madefor Text (fonts)
- Copyright 2023 The Wix Madefor Project Authors. https://github.com/wix/wixmadefor
- SIL Open Font License 1.1. Shipped unmodified in the app's `fonts` folder.

## Installed by Kelvin's installer, not shipped

### PawnIO driver 2.2.0
- By namazso. https://pawnio.eu
- Kelvin does not redistribute PawnIO. When it's missing, Kelvin's installer downloads the official
  release (`PawnIO_setup.exe`) from https://github.com/namazso/PawnIO.Setup and checks it against a
  pinned SHA-256 before installing it. Uninstalling Kelvin leaves PawnIO in place, because other
  apps use it too.

### Microsoft Edge WebView2 Runtime
- Installed by the Tauri installer if missing. Microsoft license terms apply.
