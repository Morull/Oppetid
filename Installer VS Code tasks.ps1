# Installer VS Code tasks for Oppetid-prosjektet.
# Kjor med hoyreklikk -> "Kjor med PowerShell", eller via bat-wrapperen.

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

if (-not (Test-Path '.vscode')) {
    New-Item -ItemType Directory -Path '.vscode' | Out-Null
}

$target = Join-Path '.vscode' 'tasks.json'

if (Test-Path $target) {
    $answer = Read-Host ".vscode\tasks.json finnes allerede. Overskrive? (J/N)"
    if ($answer -notmatch '^(j|J|y|Y)$') {
        Write-Host "Avbrutt."
        exit 0
    }
}

$json = @'
{
  "version": "2.0.0",
  "tasks": [
    {
      "label": "Oppetid: Start",
      "type": "shell",
      "command": "docker compose up --build -d; Start-Process http://localhost:5180",
      "group": { "kind": "build", "isDefault": true },
      "problemMatcher": [],
      "presentation": {
        "reveal": "always",
        "panel": "dedicated",
        "clear": true
      },
      "detail": "Bygg og start hele stacken, aapne UI i nettleser"
    },
    {
      "label": "Oppetid: Stopp",
      "type": "shell",
      "command": "docker compose down",
      "problemMatcher": [],
      "presentation": { "reveal": "always", "panel": "dedicated" },
      "detail": "Stopp alle containere"
    },
    {
      "label": "Oppetid: Rebuild (uten cache)",
      "type": "shell",
      "command": "docker compose down; docker compose build --no-cache; docker compose up -d",
      "problemMatcher": [],
      "presentation": { "reveal": "always", "panel": "dedicated", "clear": true },
      "detail": "Full reset - bruk ved Dockerfile-endringer"
    },
    {
      "label": "Oppetid: Logger (follow)",
      "type": "shell",
      "command": "docker compose logs -f",
      "problemMatcher": [],
      "presentation": { "reveal": "always", "panel": "dedicated", "clear": true },
      "detail": "Foelg live-logger (Ctrl+C for aa avslutte)"
    },
    {
      "label": "Oppetid: Logger (kun API)",
      "type": "shell",
      "command": "docker compose logs -f api",
      "problemMatcher": [],
      "presentation": { "reveal": "always", "panel": "dedicated", "clear": true }
    },
    {
      "label": "Oppetid: Status",
      "type": "shell",
      "command": "docker compose ps",
      "problemMatcher": [],
      "presentation": { "reveal": "always", "panel": "shared" },
      "detail": "Vis hvilke containere som kjorer"
    },
    {
      "label": "Oppetid: Restart API",
      "type": "shell",
      "command": "docker compose restart api",
      "problemMatcher": [],
      "presentation": { "reveal": "silent", "panel": "shared" },
      "detail": "Restart kun API-containeren"
    },
    {
      "label": "Oppetid: Seed Drivdal",
      "type": "shell",
      "command": "docker compose exec postgres psql -U kraftverk -d kraftverk -c \"INSERT INTO core.plants (id, owner_org_id, name, type, installed_capacity_mw, time_zone, created_at) VALUES ('drivdal', 'dev-org', 'Drivdal', 'Regulated', 2.2, 'Europe/Oslo', NOW()) ON CONFLICT (id) DO NOTHING;\"",
      "problemMatcher": [],
      "presentation": { "reveal": "always", "panel": "shared" },
      "detail": "Seed Drivdal-anlegget (idempotent)"
    }
  ]
}
'@

Set-Content -Path $target -Value $json -Encoding UTF8

Write-Host ""
Write-Host "OK. .vscode\tasks.json er installert." -ForegroundColor Green
Write-Host ""
Write-Host "I VS Code:"
Write-Host "  - Ctrl+Shift+B          => kjor 'Oppetid: Start'"
Write-Host "  - Ctrl+Shift+P -> 'Tasks: Run Task' => velg fra menyen"
Write-Host ""
Write-Host "Trykk Enter for aa lukke..."
[void][System.Console]::ReadLine()
