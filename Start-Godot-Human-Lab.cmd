@echo off
powershell -NoProfile -File "%~dp0Samples\NetRpcGodot\Start-Human-Lab.ps1" %*
if errorlevel 1 pause
