@echo off
setlocal
title Oppdater Web
cd /d "%~dp0"
color 0B

echo ================================
echo   Oppdater Web (etter kode-endring)
echo ================================
echo.
echo Bygger Web-containeren paa nytt og starter den.
echo Bruk denne etter endringer i .razor / .css / .cs i Web-prosjektet.
echo.

docker compose up -d --build web
if errorlevel 1 (
    color 0C
    echo.
    echo Build feilet. Scroll opp for feilmelding.
    pause
    exit /b 1
)

color 0A
echo.
echo Web er oppdatert. Apner http://localhost:5180
echo (Trykk Ctrl+F5 i nettleseren for hard-refresh av CSS/JS.)
start http://localhost:5180
timeout /t 3 >nul
exit /b 0
