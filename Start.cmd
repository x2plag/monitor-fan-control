@echo off
setlocal DisableDelayedExpansion
set "LG_QUIET_PACKAGE=%~dp0"
set "LG_QUIET_LAUNCHER=%~dp0Start.ps1"
powershell.exe -NoLogo -NoProfile -Command "& ([scriptblock]::Create([System.IO.File]::ReadAllText($env:LG_QUIET_LAUNCHER))) -PackageDirectory $env:LG_QUIET_PACKAGE"
set "LG_QUIET_EXIT=%ERRORLEVEL%"
if not "%LG_QUIET_EXIT%"=="0" pause
exit /b %LG_QUIET_EXIT%
