@echo off
setlocal
title Patch annotation tables
cd /d "%~dp0"
color 0E

echo ================================
echo   Patch annotation tables
echo ================================
echo.
echo MERKNAD: DatabaseBootstrapper kjorer naa samme DDL automatisk ved
echo API/Worker-oppstart, saa denne batchen er normalt ikke noedvendig.
echo Behold den som manuell fallback hvis bootstrapper-kallet feiler eller
echo hvis DB maa fikses uten omstart av API-en.
echo.
echo Oppretter manglende tabeller (downtime_categories, downtime_annotations)
echo idempotent. Bruk denne hvis API krasjer med "relation downtime_categories
echo does not exist".
echo.
echo Krever at postgres-containeren kjorer.
echo.

set "SQL=CREATE TABLE IF NOT EXISTS core.downtime_categories ( id varchar(64) PRIMARY KEY, display_name varchar(200) NOT NULL, color_hex varchar(16) NOT NULL DEFAULT '#888888', unit_state_override varchar(32) NOT NULL, sort_order integer NOT NULL DEFAULT 0, is_active boolean NOT NULL DEFAULT TRUE, is_system boolean NOT NULL DEFAULT FALSE ); CREATE INDEX IF NOT EXISTS ix_downtime_categories_sort_order ON core.downtime_categories (sort_order); CREATE TABLE IF NOT EXISTS core.downtime_annotations ( id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, owner_org_id varchar(64) NOT NULL, plant_id varchar(64) NOT NULL, start_utc timestamptz NOT NULL, end_utc timestamptz NOT NULL, category_id varchar(64) NOT NULL, comment varchar(2000) NULL, created_at timestamptz NOT NULL, created_by varchar(128) NULL, updated_at timestamptz NOT NULL, updated_by varchar(128) NULL, deleted_at timestamptz NULL, deleted_by varchar(128) NULL, CONSTRAINT fk_downtime_annotations_category FOREIGN KEY (category_id) REFERENCES core.downtime_categories(id) ON DELETE RESTRICT ); CREATE INDEX IF NOT EXISTS ix_downtime_annotations_plant_period ON core.downtime_annotations (plant_id, start_utc, end_utc) WHERE deleted_at IS NULL;"

echo Kjorer DDL...
docker compose exec -T postgres psql -U kraftverk -d kraftverk -c "%SQL%"
if errorlevel 1 (
    color 0C
    echo.
    echo Patch feilet. Sjekk at postgres-containeren kjorer (docker ps).
    pause
    exit /b 1
)

echo.
color 0A
echo Tabellene er pa plass. Restarter API saa seederen far kjore...
docker compose restart api worker
echo.
echo Vent ~10 sekunder, deretter test http://localhost:5180
timeout /t 10 >nul
exit /b 0
