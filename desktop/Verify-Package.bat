@echo off
setlocal
set "PWSH=%~dp0.deps\pwsh\pwsh.exe"
if not exist "%PWSH%" (
  echo The bundled PowerShell runtime is missing. Extract the complete package into a new folder.
  pause
  exit /b 1
)
"%PWSH%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Verify-Package.ps1"
set "VERIFY_EXIT=%ERRORLEVEL%"
pause
exit /b %VERIFY_EXIT%
