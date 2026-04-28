@echo off
setlocal
title Oppdater alt
cd /d "%~dp0"
color 0B

echo ================================
echo   Oppdater hele stacken
echo ================================
echo.
echo Bygger alle containere paa nytt og restarter.
echo Bruk denne hvis du er i tvil om hva som er endret.
echo.

docker compose up -d --build
if errorlevel 1 (
    color 0C
    echo.
    echo Build feilet. Scroll opp for feilmelding.
    pause
    exit /b 1
)

color 0A
echo.
echo Alt er oppdatert. Apner http://localhost:5180
echo (Trykk Ctrl+F5 i nettleseren for hard-refresh.)
start http://localhost:5180
timeout /t 3 >nul
exit /b 0
