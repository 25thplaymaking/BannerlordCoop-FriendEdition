@echo off
setlocal
title Bannerlord Coop Friend Edition - Client Setup
echo.
echo Starting the guided Friend Edition client setup...
echo You can paste a Bannerlord folder on any drive when prompted.
echo Example: G:\Steam\steamapps\common\Mount ^& Blade II Bannerlord
echo.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-ManagedSuiteClient.ps1"
set "setup_exit=%ERRORLEVEL%"
echo.
if not "%setup_exit%"=="0" echo Setup stopped with an error. Read the message above, correct it, and try again.
pause
exit /b %setup_exit%
