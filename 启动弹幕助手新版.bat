@echo off
setlocal
set "PWSH=%~dp0.deps\pwsh\pwsh.exe"
if not exist "%PWSH%" (
  echo Project PowerShell 7 runtime is missing. Extract the complete app package again.
  echo Developers can run scripts\Setup-Desktop.ps1 with PowerShell 7.
  pause
  exit /b 1
)
"%PWSH%" -NoLogo -NoProfile -NonInteractive -Command "$v=$PSVersionTable.PSVersion; if ($PSVersionTable.PSEdition -eq 'Core' -and $v.Major -eq 7 -and $v.Minor -eq 6 -and -not $v.ToString().Contains('-') -and [Environment]::Is64BitProcess -and [Environment]::OSVersion.Platform -eq 'Win32NT') { exit 0 } else { exit 1 }" >nul 2>nul
if not "%ERRORLEVEL%"=="0" (
  echo PowerShell 7.6 stable x64 runtime validation failed. Repair the project runtime before starting.
  pause
  exit /b 1
)
start "" "%PWSH%" -NoLogo -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0desktop\Start.ps1"
