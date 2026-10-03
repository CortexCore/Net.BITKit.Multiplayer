@echo off
setlocal
title BITKit Multiplayer - Arena V2.1
echo Building and starting Arena (player-host uses Relay; F3 toggles interpolation)...
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Samples\Arena\Start-Arena.ps1" %*
set "ARENA_EXIT=%ERRORLEVEL%"
if not "%ARENA_EXIT%"=="0" (
    echo.
    echo Arena could not start. Check the messages above.
    pause
)
exit /b %ARENA_EXIT%
