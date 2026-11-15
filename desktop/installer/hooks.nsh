; Kelvin installer hooks.
;
; Install: set up dependencies (PawnIO driver; OpenRGB ships inside Kelvin), then register the
; hardware service as a Windows service. It runs as LocalSystem so it can reach fan and lighting
; controllers without a UAC prompt each time the app opens, starts at boot, restarts itself if it
; crashes, and on every stop hands all fans back to BIOS control.
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

!macro NSIS_HOOK_PREINSTALL
  ; Upgrading: stop the old service before its files are overwritten. It also stops the OpenRGB
  ; copy it runs, so those files are free to replace too.
  !insertmacro KELVIN_STOP_SERVICE
!macroend

!macro NSIS_HOOK_POSTINSTALL
  Push $R8
  Push $R9

  ; PawnIO driver: fetched from its official release and checksum-verified (see the script).
  ; A failure (e.g. offline) doesn't stop the install; Kelvin shows a notice until it's there.
  DetailPrint "Installing the PawnIO driver for fan and lighting control..."
  nsExec::ExecToLog 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\scripts\install-pawnio.ps1"'
  Pop $R8

  ; Create the service, or update it if it already exists from an earlier install.
  DetailPrint "Registering the Kelvin hardware service..."
  nsExec::ExecToLog 'sc.exe create ${KELVIN_SERVICE} binPath= "\"$INSTDIR\service\Kelvin.Service.exe\"" start= auto DisplayName= "Kelvin hardware service"'
  Pop $R8
  nsExec::ExecToLog 'sc.exe config ${KELVIN_SERVICE} binPath= "\"$INSTDIR\service\Kelvin.Service.exe\"" start= auto DisplayName= "Kelvin hardware service"'
  Pop $R8
  nsExec::ExecToLog 'sc.exe description ${KELVIN_SERVICE} "Runs Kelvin fan curves and lighting. If it stops, every fan goes back to BIOS control."'
  Pop $R8
  ; Restart after a crash: 5 s, 5 s, then 30 s. Reset the count after a day.
  nsExec::ExecToLog 'sc.exe failure ${KELVIN_SERVICE} reset= 86400 actions= restart/5000/restart/5000/restart/30000'
  Pop $R8

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
  ; Stopping the service also stops its OpenRGB and hands fans back to the BIOS. The PawnIO
  ; driver stays installed: other apps (FanControl, LibreHardwareMonitor, OpenRGB) share it.
  !insertmacro KELVIN_STOP_SERVICE
  Push $R8
  nsExec::ExecToLog 'sc.exe delete ${KELVIN_SERVICE}'
  Pop $R8
  Pop $R8
!macroend
