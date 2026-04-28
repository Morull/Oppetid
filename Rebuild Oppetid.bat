@echo off
setlocal
title Rebuild Oppetid
cd /d "%~dp0"
color 0E

echo ================================
echo    Rebuild Oppetid (full reset)
echo ================================
echo.
echo Dette stopper alt, tvinger full rebuild (uten cache)
echo og starter stacken pa nytt. Bruk ved endringer i
echo Dockerfile eller hvis noe sitter fast.
echo.
set "confirm="
set /p confirm="Fortsett? (J/N): "
if /i not "%confirm%"=="j" exit /b 0

echo.
echo [1/3] Stopper...
docker compose down
echo.

echo [2/3] Bygger uten cache... (kan ta 3-10 min)
docker compose build --no-cache
if errorlevel 1 (
    color 0C
    echo Build feilet.
    pause
    exit /b 1
)

echo.
echo [3/3] Starter...
docker compose up -d
if errorlevel 1 (
    color 0C
    echo Start feilet.
    pause
    exit /b 1
)

color 0A
echo.
echo Ferdig. Apner http://localhost:5180
start http://localhost:5180
timeout /t 3 >nul
exit /b 0
