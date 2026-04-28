@echo off
REM Genererer EF Core-migrasjoner for KraftverkUptime.
REM Forutsetning: dotnet 10 SDK installert lokalt + restore av tool-manifestet.

setlocal
cd /d "%~dp0"

echo.
echo [1/4] Restorer dotnet-tools (forste gang) ...
dotnet tool restore
if errorlevel 1 goto :error

echo.
echo [2/4] Lager Initial-migrasjon hvis den mangler ...
if not exist "src\KraftverkUptime.Infrastructure\Migrations" (
    dotnet ef migrations add Initial ^
        --project src\KraftverkUptime.Infrastructure\KraftverkUptime.Infrastructure.csproj ^
        --startup-project src\KraftverkUptime.Api\KraftverkUptime.Api.csproj ^
        --context KraftverkDbContext ^
        --output-dir Persistence\Migrations
    if errorlevel 1 goto :error
) else (
    echo Migrasjons-mappen finnes allerede - hopper over Initial.
)

echo.
echo [3/4] Lager AddAnnotations-migrasjon ...
dotnet ef migrations add AddAnnotations ^
    --project src\KraftverkUptime.Infrastructure\KraftverkUptime.Infrastructure.csproj ^
    --startup-project src\KraftverkUptime.Api\KraftverkUptime.Api.csproj ^
    --context KraftverkDbContext ^
    --output-dir Persistence\Migrations
if errorlevel 1 goto :error

echo.
echo [4/4] Migrasjoner generert. Start API-en (eller kjor 'Tving full reset.bat' for ren DB).
echo       DatabaseBootstrapper kjorer Migrate paa oppstart i Development.
goto :eof

:error
echo.
echo *** Feil. Sjekk at dotnet 10 SDK er installert og at .config\dotnet-tools.json er commitet.
exit /b 1
