@echo off
setlocal
title BITKit Multiplayer - Relay Arena
echo Starting Lobby, Relay, player Host and Client...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Samples\Arena\Start-Arena.ps1" -Route Relay %*
set "ARENA_EXIT=%ERRORLEVEL%"
if not "%ARENA_EXIT%"=="0" (
    echo.
    echo Arena could not start. Check the messages above.
    pause
)
exit /b %ARENA_EXIT%
