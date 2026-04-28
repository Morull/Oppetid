@echo off
setlocal
cd /d "%~dp0"
title Installer VS Code tasks

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer VS Code tasks.ps1"
if errorlevel 1 (
    echo.
    echo Noe gikk galt. Les meldingen over.
    pause
)
exit /b %errorlevel%
