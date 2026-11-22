; Kelvin installer hooks.
;
; Install: set up dependencies (PawnIO driver; OpenRGB ships inside Kelvin), then register the
; hardware service as a Windows service. It runs as LocalSystem so it can reach fan and lighting
; controllers without a UAC prompt each time the app opens, starts at boot, restarts itself if it
; crashes, and on every stop hands all fans back to BIOS control.
;
; Updates must never leave Kelvin dead. Four things guard that:
;   - before files are replaced, anything still running from the install folder is closed, so no
;     file is locked (a leftover OpenRGB can block an update);
;   - the programs themselves are moved aside instead of overwritten. Windows' own compatibility
;     service opens a program for a moment after it exits, and if the installer tried to overwrite
;     it in that moment it stopped on an "Error opening file for writing" dialog, halfway through.
;     A new file under a free name can't collide with anything;
;   - if the install fails or is cancelled anyway, the service that was stopped is started again;
;   - signed-in users may start (never stop or change) the service, so the app can bring it back.
;
; Every nsExec call pushes its exit code; each one is popped so nothing is left on the stack for
; Tauri's own installer logic. Registers used here are saved and restored.

!define KELVIN_SERVICE "KelvinService"

; Stop a running service and wait (up to 60 s) for it to exit, so its files can be replaced or
; removed. Each hook runs in its own section or function, so these labels never collide.
!macro KELVIN_STOP_SERVICE
  Push $R7
  Push $R8
  Push $R9
  nsExec::ExecToLog 'sc.exe stop ${KELVIN_SERVICE}'
  Pop $R8
  StrCpy $R9 0
  kelvin_wait_stop:
    nsExec::ExecToStack 'sc.exe query ${KELVIN_SERVICE}'
    Pop $R8 ; exit code
    Pop $R7 ; output
    StrCmp $R8 "0" 0 kelvin_stopped ; not installed: nothing to wait for
    nsExec::ExecToStack 'cmd.exe /c sc.exe query ${KELVIN_SERVICE} | find "STOPPED"'
    Pop $R8
    Pop $R7
    StrCmp $R8 "0" kelvin_stopped
    IntOp $R9 $R9 + 1
    IntCmp $R9 120 kelvin_stopped
    Sleep 500
    Goto kelvin_wait_stop
  kelvin_stopped:
  Pop $R9
  Pop $R8
  Pop $R7
!macroend

; Close whatever is still running from a folder under the install directory. Matching on the full
; path leaves the user's own OpenRGB, or a second Kelvin installed elsewhere, alone.
!macro KELVIN_CLOSE_RUNNING PATTERN
  Push $R8
  nsExec::ExecToLog `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Get-CimInstance Win32_Process | Where-Object { $$_.ExecutablePath -like '$INSTDIR\${PATTERN}' } | ForEach-Object { Stop-Process -Id $$_.ProcessId -Force -ErrorAction SilentlyContinue }; Start-Sleep -Milliseconds 500"`
  Pop $R8
  Pop $R8
!macroend

!macro KELVIN_FREE_FILES
  ; The app first: it would otherwise try to bring the service back while files are replaced.
  !insertmacro KELVIN_CLOSE_RUNNING "kelvin.exe"
  ; Stopping the service hands fans back to the BIOS and closes the OpenRGB it runs.
  !insertmacro KELVIN_STOP_SERVICE
  ; Anything that survived that: a service that didn't stop in time, an OpenRGB left behind.
  !insertmacro KELVIN_CLOSE_RUNNING "service\*"
  !insertmacro KELVIN_CLOSE_RUNNING "openrgb\*"
!macroend

; Move a program out of the installer's way: rename it to <name>.old, retrying for up to 60 s if
; something has it open right now. Renaming works even on a program that is still running.
!macro KELVIN_SET_ASIDE FILE ID
  Push $R9
  StrCpy $R9 0
  kelvin_aside_${ID}:
    IfFileExists "$INSTDIR\${FILE}" 0 kelvin_aside_done_${ID}
    Delete "$INSTDIR\${FILE}.old"
    ClearErrors
    Rename "$INSTDIR\${FILE}" "$INSTDIR\${FILE}.old"
    IfErrors 0 kelvin_aside_done_${ID}
    IntOp $R9 $R9 + 1
    IntCmp $R9 120 kelvin_aside_done_${ID}
    Sleep 500
    Goto kelvin_aside_${ID}
  kelvin_aside_done_${ID}:
  ClearErrors
  Pop $R9
!macroend

; The install didn't finish: put a program back if its replacement never arrived.
!macro KELVIN_PUT_BACK FILE ID
  IfFileExists "$INSTDIR\${FILE}" kelvin_back_done_${ID}
  IfFileExists "$INSTDIR\${FILE}.old" 0 kelvin_back_done_${ID}
  Rename "$INSTDIR\${FILE}.old" "$INSTDIR\${FILE}"
  kelvin_back_done_${ID}:
  ClearErrors
!macroend

; The copies set aside are no longer needed. One that is still open goes at the next restart.
!macro KELVIN_DROP_OLD FILE
  Delete "$INSTDIR\${FILE}.old"
  IfFileExists "$INSTDIR\${FILE}.old" 0 +2
    Delete /REBOOTOK "$INSTDIR\${FILE}.old"
  ClearErrors
!macroend

; Register the service, or bring an existing registration up to date. Uses $R8.
!macro KELVIN_REGISTER_SERVICE
  ; Create the service, or update it if it already exists from an earlier install.
  nsExec::ExecToLog 'sc.exe create ${KELVIN_SERVICE} binPath= "\"$INSTDIR\service\Kelvin.Service.exe\"" start= auto DisplayName= "Kelvin hardware service"'
  Pop $R8
  nsExec::ExecToLog 'sc.exe config ${KELVIN_SERVICE} binPath= "\"$INSTDIR\service\Kelvin.Service.exe\"" start= auto DisplayName= "Kelvin hardware service"'
  Pop $R8
  nsExec::ExecToLog 'sc.exe description ${KELVIN_SERVICE} "Runs Kelvin fan curves and lighting. If it stops, every fan goes back to BIOS control."'
  Pop $R8
  ; Restart after a crash: 5 s, 5 s, then 30 s. Reset the count after a day.
  nsExec::ExecToLog 'sc.exe failure ${KELVIN_SERVICE} reset= 86400 actions= restart/5000/restart/5000/restart/30000'
  Pop $R8

  ; Let signed-in users start the service (RP for IU) and nothing more: the app uses this to bring
  ; it back if it ever finds it stopped. Stopping or changing it still needs an administrator.
  nsExec::ExecToLog 'sc.exe sdset ${KELVIN_SERVICE} "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)"'
  Pop $R8
!macroend

!macro NSIS_HOOK_PREINSTALL
  !insertmacro KELVIN_FREE_FILES
  !insertmacro KELVIN_SET_ASIDE "kelvin.exe" app
  !insertmacro KELVIN_SET_ASIDE "service\Kelvin.Service.exe" service
  !insertmacro KELVIN_SET_ASIDE "openrgb\OpenRGB.exe" openrgb
!macroend

; The install failed or was cancelled after the service was stopped: start it again, so the PC is
; left with a working Kelvin (the old version, or as much of the new one as was copied).
Function .onInstFailed
  !insertmacro KELVIN_PUT_BACK "kelvin.exe" app
  !insertmacro KELVIN_PUT_BACK "service\Kelvin.Service.exe" service
  !insertmacro KELVIN_PUT_BACK "openrgb\OpenRGB.exe" openrgb
  ; Where the new program did arrive, the copy set aside is no longer needed.
  !insertmacro KELVIN_DROP_OLD "kelvin.exe"
  !insertmacro KELVIN_DROP_OLD "service\Kelvin.Service.exe"
  !insertmacro KELVIN_DROP_OLD "openrgb\OpenRGB.exe"
  ; A reinstall removes the old registration first, so make sure there is one to start.
  IfFileExists "$INSTDIR\service\Kelvin.Service.exe" 0 kelvin_failed_done
  !insertmacro KELVIN_REGISTER_SERVICE
  nsExec::Exec 'sc.exe start ${KELVIN_SERVICE}'
  Pop $R8
  kelvin_failed_done:
FunctionEnd

!macro NSIS_HOOK_POSTINSTALL
  Push $R8
  Push $R9

  !insertmacro KELVIN_DROP_OLD "kelvin.exe"
  !insertmacro KELVIN_DROP_OLD "service\Kelvin.Service.exe"
  !insertmacro KELVIN_DROP_OLD "openrgb\OpenRGB.exe"

  ; PawnIO driver: fetched from its official release and checksum-verified (see the script).
  ; A failure (e.g. offline) doesn't stop the install; Kelvin shows a notice until it's there.
  DetailPrint "Installing the PawnIO driver for fan and lighting control..."
  nsExec::ExecToLog 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\scripts\install-pawnio.ps1"'
  Pop $R8

  DetailPrint "Registering the Kelvin hardware service..."
  !insertmacro KELVIN_REGISTER_SERVICE

  ; Start it, retrying for up to 20 s in case an old instance is still finishing its shutdown.
  StrCpy $R9 0
  kelvin_start:
    nsExec::ExecToLog 'sc.exe start ${KELVIN_SERVICE}'
    Pop $R8
    StrCmp $R8 "0" kelvin_started
    StrCmp $R8 "1056" kelvin_started ; already running
    IntOp $R9 $R9 + 1
    IntCmp $R9 10 kelvin_started
    Sleep 2000
    Goto kelvin_start
  kelvin_started:

  Pop $R9
  Pop $R8
!macroend

!macro NSIS_HOOK_PREUNINSTALL
  ; The PawnIO driver stays installed: other apps (FanControl, LibreHardwareMonitor, OpenRGB)
  ; share it.
  !insertmacro KELVIN_FREE_FILES
  !insertmacro KELVIN_DROP_OLD "kelvin.exe"
  !insertmacro KELVIN_DROP_OLD "service\Kelvin.Service.exe"
  !insertmacro KELVIN_DROP_OLD "openrgb\OpenRGB.exe"
  Push $R8
  nsExec::ExecToLog 'sc.exe delete ${KELVIN_SERVICE}'
  Pop $R8
  Pop $R8
!macroend
