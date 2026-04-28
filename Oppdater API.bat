@echo off
setlocal
title Oppdater API
cd /d "%~dp0"
color 0B

echo ================================
echo   Oppdater API (etter kode-endring)
echo ================================
echo.
echo Bygger API-containeren paa nytt og starter den.
echo Bruk denne etter endringer i .cs-filer i API/Modules/Infrastructure.
echo.

docker compose up -d --build api worker
if errorlevel 1 (
    color 0C
    echo.
    echo Build feilet. Scroll opp for feilmelding.
    pause
    exit /b 1
)

color 0A
echo.
echo API + Worker er oppdatert.
timeout /t 3 >nul
exit /b 0
