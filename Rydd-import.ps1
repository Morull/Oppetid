<#
.SYNOPSIS
  Rydder opp i hot-folderen etter maskinbytte/re-import:
   1) Flytter parkerte datafiler fra quarantine/ og duplicates/ tilbake til roten
      med korte, trygge filnavn (unngaar PathTooLong).
   2) Flytter stoy (_recovery-mapping.csv) til _ignored/ saa watcheren slutter aa feile.
   3) Nullstiller innholds-hash-loggen (.hotfolder-dedup.json) slik at filene ikke
      umiddelbart avvises som duplikat. DB-laget er idempotent paa (file_hash, plant_id),
      saa re-import av noe som allerede finnes er trygt.

  Default er DRY-RUN. Kjor med -Execute for aa utfore.

  Kjorerekkefolge (dedup-loggen ligger ogsaa i minnet paa kjorende app):
    1) docker compose -f docker-compose.yml -f docker-compose.tailscale.yml stop api worker
    2) powershell -ExecutionPolicy Bypass -File "C:\Morten\00 Oppetid\Rydd-import.ps1" -Execute
    3) docker compose -f docker-compose.yml -f docker-compose.tailscale.yml up -d
#>
param(
    [string]$Root = "C:\Morten\00 Oppetid\CSV Eksporter",
    [switch]$Execute
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Root)) { throw "Fant ikke hot-folder: $Root" }

$dedupPath  = Join-Path $Root ".hotfolder-dedup.json"
$ignoredDir = Join-Path $Root "_ignored"
$sourceDirs = @("quarantine", "duplicates") | ForEach-Object { Join-Path $Root $_ }

$dataExt = @(".csv", ".xlsx", ".xls")
$plants  = @('drivdal','grodemfoss','haukland','honnefoss','lindland','logjen',
             'ogreyfoss','orsdalen','liavatn','stolskraft','vikesa')

function Get-ShortName([string]$name) {
    $ext  = [System.IO.Path]::GetExtension($name)
    $orig = [System.IO.Path]::GetFileNameWithoutExtension($name)

    # Behold plant-slug KUN for ekte enkelt-plant-filer (navn starter med slug).
    # Multi-filer (_multi_/export-/dup_) detekteres paa innhold.
    $plant = $plants | Where-Object { $orig.ToLower().StartsWith($_) } | Select-Object -First 1

    # Strip akkumulerte stamp-prefikser: alt t.o.m. siste yyyyMMddTHHmmssfff.
    $stem = $orig
    $m = [regex]::Matches($stem, '\d{8}T\d{9}')
    if ($m.Count -gt 0) {
        $last = $m[$m.Count - 1]
        $stem = $stem.Substring($last.Index + $last.Length).TrimStart('_')
    }
    $stem = $stem -replace '^(dup_|_multi__|_+)', ''
    if ([string]::IsNullOrWhiteSpace($stem)) { $stem = $orig }
    if ($stem.Length -gt 90) { $stem = $stem.Substring(0, 90) }
    if ($plant -and -not $stem.ToLower().StartsWith($plant)) { $stem = ($plant + "_" + $stem) }
    return ($stem + $ext)
}

# --- Samle datafiler som skal tilbake ---
$toMove = @()
foreach ($dir in $sourceDirs) {
    if (-not (Test-Path $dir)) { continue }
    Get-ChildItem -Path $dir -Recurse -File | Where-Object {
        ($dataExt -contains $_.Extension.ToLower()) -and
        ($_.Name -ne "_recovery-mapping.csv") -and
        ($_.Name -notlike "*.diag.json") -and
        ($_.Name -notlike "*.error.txt")
    } | ForEach-Object { $toMove += $_ }
}

Write-Host "== Filer som flyttes tilbake til roten ($($toMove.Count)) ==" -ForegroundColor Cyan
$plan = @()
foreach ($f in $toMove) {
    $short = Get-ShortName $f.Name
    $dest  = Join-Path $Root $short
    if (Test-Path $dest) {
        $b = [System.IO.Path]::GetFileNameWithoutExtension($short)
        $e = [System.IO.Path]::GetExtension($short)
        $dest = Join-Path $Root ($b + "_" + (Get-Date -Format "HHmmssfff") + $e)
    }
    $plan += [pscustomobject]@{ Fra = $f.FullName; Til = $dest }
    $srcShort = $f.Name
    if ($srcShort.Length -gt 55) { $srcShort = $srcShort.Substring(0, 55) }
    Write-Host ("  " + $srcShort + "  ->  " + (Split-Path $dest -Leaf))
}

# --- Stoy ut av roten ---
$recovery = Get-ChildItem -Path $Root -Filter "_recovery-mapping.csv" -File -ErrorAction SilentlyContinue
Write-Host ""
if ($recovery) { Write-Host "== _recovery-mapping.csv flyttes til _ignored/ ==" -ForegroundColor Cyan }

# --- Dedup-logg ---
$ledgerCount = 0
if (Test-Path $dedupPath) {
    try { $ledgerCount = (Get-Content $dedupPath -Raw | ConvertFrom-Json).Count } catch { $ledgerCount = "ukjent" }
}
Write-Host ("== Dedup-logg nullstilles (.hotfolder-dedup.json, " + $ledgerCount + " rader) - backup tas ==") -ForegroundColor Cyan

if (-not $Execute) {
    Write-Host ""
    Write-Host "DRY-RUN - ingen endringer gjort. Kjor med -Execute for aa utfore." -ForegroundColor Yellow
    return
}

# ================= UTFOR =================
# 1) Backup + fjern dedup-logg (lastes tomt ved neste app-oppstart)
if (Test-Path $dedupPath) {
    $bak = ($dedupPath + ".bak-" + (Get-Date -Format "yyyyMMddHHmmss"))
    Move-Item -Path $dedupPath -Destination $bak -Force
    Write-Host ("Dedup-logg sikkerhetskopiert: " + (Split-Path $bak -Leaf))
}

# 2) Flytt stoy
if ($recovery) {
    New-Item -ItemType Directory -Force -Path $ignoredDir | Out-Null
    Move-Item -Path $recovery.FullName -Destination (Join-Path $ignoredDir $recovery.Name) -Force
}

# 3) Flytt datafiler tilbake til roten
$moved = 0
foreach ($p in $plan) {
    try {
        Move-Item -Path $p.Fra -Destination $p.Til
        $moved = $moved + 1
    }
    catch {
        Write-Warning ("Kunne ikke flytte " + $p.Fra + ": " + $_)
    }
}

Write-Host ""
Write-Host ("Ferdig: " + $moved + " filer flyttet, dedup-logg nullstilt.") -ForegroundColor Green
Write-Host "Neste steg: start appen igjen saa watcheren skanner roten:" -ForegroundColor Green
Write-Host "  docker compose -f docker-compose.yml -f docker-compose.tailscale.yml up -d"
