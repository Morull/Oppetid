@echo off
setlocal
title Oppetid - Dalane Kraft
cd /d "%~dp0"
color 0B

echo ================================
echo    Oppetid - Dalane Kraft
echo ================================
echo.

rem --- 0/3: Er Docker Desktop oppe? ---
echo Sjekker Docker Desktop...
docker version >nul 2>&1
if errorlevel 1 (
    color 0C
    echo.
    echo FEIL: Docker Desktop kjorer ikke.
    echo   1. Apne Docker Desktop fra Start-menyen
    echo   2. Vent til hval-ikonet i systray er rolig
    echo   3. Start dette scriptet pa nytt
    echo.
    pause
    exit /b 1
)
echo   OK
echo.

rem --- 1/3: Bygg ---
echo [1/3] Bygger images... (1-5 min forste gang, sekunder senere)
echo ----------------------------------------
docker compose build
if errorlevel 1 (
    color 0C
    echo.
    echo Build feilet. Scroll opp for a se feilmeldingen.
    echo Hele utdataen er ogsa i build.log hvis du kjorte via den.
    pause
    exit /b 1
)
echo ----------------------------------------
echo.

rem --- 2/3: Start ---
echo [2/3] Starter containere...
docker compose up -d
if errorlevel 1 (
    color 0C
    echo.
    echo Start feilet. Kjor "docker compose logs" for detaljer.
    pause
    exit /b 1
)
echo.

rem --- 3/3: Vent til UI svarer ---
echo [3/3] Venter pa at UI svarer paa http://localhost:5180 ...
set /a tries=0
:waitloop
set /a tries+=1
if %tries% GTR 30 (
    echo   Tidsavbrudd etter ~60s - apner nettleser likevel.
    goto openbrowser
)
timeout /t 2 /nobreak >nul
curl -sf -o nul http://localhost:5180 2>nul
if errorlevel 1 goto waitloop

:openbrowser
color 0A
echo.
echo ================================
echo    Oppetid kjorer
echo    UI:  http://localhost:5180
echo    API: http://localhost:5080
echo ================================
start http://localhost:5180

echo.
echo  [L] Vis live-logger (Ctrl+C for a ga tilbake)
echo  [S] Stopp alt
echo  [Q] Lukk vinduet (stacken fortsetter a kjore)
echo.

:menu
set "choice="
set /p choice="Velg (L/S/Q): "
if /i "%choice%"=="l" (
    echo.
    docker compose logs -f
    echo.
    goto menu
)
if /i "%choice%"=="s" (
    echo Stopper...
    docker compose down
    echo Stoppet.
    timeout /t 2 >nul
    exit /b 0
)
if /i "%choice%"=="q" exit /b 0
goto menu
