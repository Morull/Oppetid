@echo off
setlocal
title Tving Web rebuild
cd /d "%~dp0"
color 0E

echo ================================
echo   Tving full rebuild av Web
echo ================================
echo.
echo Stopper Web-containeren, sletter image-et og bygger fra scratch.
echo Bruk denne hvis Web-containeren leverer gammel CSS/HTML selv etter
echo "Oppdater Web.bat" eller "Rebuild Oppetid.bat".
echo.

echo [1/4] Stopper Web-container...
docker compose stop web 2>nul
docker compose rm -f web 2>nul

echo.
echo [2/4] Sletter Web-image...
docker rmi 00oppetid-web 2>nul

echo.
echo [3/4] Bygger Web fra scratch (uten cache)...
docker compose build --no-cache web
if errorlevel 1 (
    color 0C
    echo.
    echo Build feilet. Scroll opp for feilmelding.
    pause
    exit /b 1
)

echo.
echo [4/4] Starter Web...
docker compose up -d web
if errorlevel 1 (
    color 0C
    echo Start feilet.
    pause
    exit /b 1
)

color 0A
echo.
echo ================================
echo   Ferdig
echo ================================
echo.
echo Apner http://localhost:5180
echo VIKTIG: I nettleseren etterpa, gjor dette for aa kvitte deg med
echo Blazor sin service-worker-cache:
echo.
echo   1. F12 (Developer Tools)
echo   2. Application-fanen -^> Service Workers -^> Unregister
echo   3. Application-fanen -^> Storage -^> Clear site data
echo   4. Refresh siden
echo.
start http://localhost:5180
timeout /t 3 >nul
exit /b 0
