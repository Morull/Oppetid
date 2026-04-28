@echo off
title Stopp Oppetid
cd /d "%~dp0"
echo Stopper Oppetid-stacken...
docker compose down
echo Ferdig.
timeout /t 3 >nul
exit /b 0
