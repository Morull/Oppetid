@echo off
setlocal
title Tving full reset (alle data slettes)
cd /d "%~dp0"
color 0E

echo ================================
echo   TVING FULL RESET
echo ================================
echo.
echo Dette vil:
echo   1. Stoppe alle containere
echo   2. SLETTE postgres-data og azurite-data (importerte rapporter mistes)
echo   3. Bygge alle containere fra scratch (uten cache)
echo   4. Starte alt paa nytt
echo   5. Seede Drivdal-anlegget
echo   6. Apne UI saa du kan re-importere Drivdal-fila
echo.
echo Bruk denne naar du vil regenerere rapporter med ny KPI-katalog
echo eller naar noe sitter helt fast.
echo.

set "confirm="
set /p confirm="Fortsett? (J/N): "
if /i not "%confirm%"=="j" (
    echo Avbrutt.
    exit /b 0
)

echo.
echo [1/5] Stopper og sletter volumer...
docker compose down -v
if errorlevel 1 (
    color 0C
    echo Stop feilet.
    pause
    exit /b 1
)

echo.
echo [2/5] Bygger alle containere fra scratch...
docker compose build --no-cache
if errorlevel 1 (
    color 0C
    echo Build feilet. Scroll opp for feilmelding.
    pause
    exit /b 1
)

echo.
echo [3/5] Starter alt...
docker compose up -d
if errorlevel 1 (
    color 0C
    echo Start feilet.
    pause
    exit /b 1
)

echo.
echo [4/5] Venter paa at Postgres blir klar...
:waitloop
timeout /t 2 /nobreak >nul
docker compose exec postgres pg_isready -U kraftverk -d kraftverk >nul 2>&1
if errorlevel 1 goto waitloop
echo   Postgres er klar.

rem Vent litt ekstra paa at API kjorer migrasjoner
echo Venter paa at API kjorer migrasjoner (15s)...
timeout /t 15 /nobreak >nul

echo.
echo [5/5] Seeder Drivdal-anlegget...
docker compose exec postgres psql -U kraftverk -d kraftverk -c "INSERT INTO core.plants (id, owner_org_id, name, type, installed_capacity_mw, time_zone, created_at) VALUES ('drivdal', 'dev-org', 'Drivdal', 'Regulated', 2.2, 'Europe/Oslo', NOW()) ON CONFLICT (id) DO NOTHING;"
if errorlevel 1 (
    color 0E
    echo Seeding feilet — du kan kjore det manuelt etterpa via VS Code task 'Oppetid: Seed Drivdal'.
)

color 0A
echo.
echo ================================
echo   FERDIG
echo ================================
echo.
echo Apner http://localhost:5180/upload?plant=drivdal
echo Last opp Drivdal-feb-2025-fila for aa generere ny rapport.
echo.
start http://localhost:5180/upload?plant=drivdal
timeout /t 3 >nul
exit /b 0
