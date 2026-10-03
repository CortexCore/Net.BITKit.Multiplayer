@echo off
setlocal
title BITKit Multiplayer - Arena V2.1 Pose Delay Comparison
echo Starting with 100 ms interpolation and client pose-display delay/jitter.
echo Press F3 in a game window to compare interpolation ON/OFF.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Samples\Arena\Start-Arena.ps1" -PoseDelayMs 120 -PoseJitterMs 60 %*
set "ARENA_EXIT=%ERRORLEVEL%"
if not "%ARENA_EXIT%"=="0" (
    echo.
    echo Arena could not start. Check the messages above.
    pause
)
exit /b %ARENA_EXIT%
