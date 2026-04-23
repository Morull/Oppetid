"""Orchestrator for Drivdal Nivå-0 oppetidsanalyse.

Kjøres som:
    python src/main.py <path-to-excel>

Produserer:
    output/drivdal-feb2025-uptime-report.xlsx
    output/drivdal-feb2025-fasit.json
    output/drivdal-feb2025-summary.txt
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

from parser import parse_settlement
from quality import build_quality_report
from classifier import classify, cluster_events, PlantConfig
from kpi import compute_kpis
from report import write_excel_report, write_json_fasit


DRIVDAL = PlantConfig(
    plant_id="Drivdal",
    plant_type="Regulated",
    nominal_power_mw=2.2,
    derating_threshold=0.90,
    sustained_stop_hours=24,
    marginal_cost_nok_mwh=100.0,
)


def run(input_path: str, output_dir: str) -> dict:
    input_path = Path(input_path)
    out = Path(output_dir)
    out.mkdir(parents=True, exist_ok=True)

    print(f"[1/5] Parser {input_path.name}")
    parsed = parse_settlement(input_path)
    print(f"      Anlegg: {parsed.plant_name}")
    print(f"      Periode: {parsed.period_start} – {parsed.period_end}")
    print(f"      Timer mottatt: {len(parsed.hourly)}")

    print("[2/5] Bygger datakvalitetsrapport")
    quality, hourly = build_quality_report(parsed)
    print(f"      Aksepterte: {quality.hours_accepted}, flagget: {quality.hours_flagged}, "
          f"avvist: {quality.hours_rejected}")
    print(f"      Avvik: {len(quality.issues)}")

    print("[3/5] Klassifiserer timer")
    classified = classify(hourly, DRIVDAL)
    print(f"      Tilstandsfordeling: {dict(classified['state'].value_counts())}")

    print("[4/5] Beregner KPI-er")
    kpi = compute_kpis(classified, DRIVDAL)
    # Print et utvalg KPI-er
    highlights = {
        "TotalProduction_MWh", "CapacityFactor_CF", "AvailabilityFactor_AF",
        "ServiceFactor_SF", "ForcedOutageRate_FOR",
        "PlanFulfillment", "BidAccuracy", "PlanDeviation_MWh",
    }
    for k in kpi.kpis:
        if k.name in highlights:
            v = f"{k.value:.4f}" if k.value is not None else "None"
            print(f"      {k.name:32s} = {v} {k.unit}")

    events = cluster_events(classified)
    print(f"      Hendelser (run-kluster): {len(events)}")

    print("[5/5] Skriver rapport")
    excel_path = out / "drivdal-feb2025-uptime-report.xlsx"
    fasit_path = out / "drivdal-feb2025-fasit.json"
    summary_path = out / "drivdal-feb2025-summary.txt"

    write_excel_report(excel_path, kpi, classified, events, quality)
    write_json_fasit(fasit_path, kpi, quality, classified)

    # Tekstsammendrag (for rask inspeksjon og CI)
    lines = [
        f"Drivdal oppetidsanalyse – {parsed.period_start} til {parsed.period_end}",
        f"Timer totalt: {kpi.period_hours}",
        "",
        "Tilstandsfordeling:",
    ]
    for state, count in sorted(kpi.state_counts.items(), key=lambda kv: -kv[1]):
        pct = 100.0 * count / kpi.period_hours if kpi.period_hours else 0
        lines.append(f"  {state:28s} {count:4d}  ({pct:5.1f}%)")
    lines.append("")
    lines.append("KPI-er (utvalg):")
    for k in kpi.kpis:
        if k.value is None:
            continue
        if k.unit == "ratio":
            lines.append(f"  {k.name:36s} {k.value:8.2%}   [{k.hours_basis}h, conf {k.confidence:.2f}]")
        else:
            lines.append(f"  {k.name:36s} {k.value:12.4f} {k.unit}   [{k.hours_basis}h]")
    lines.append("")
    lines.append("Datakvalitet:")
    lines.append(f"  Forventet:  {quality.hours_expected}")
    lines.append(f"  Mottatt:    {quality.hours_received}")
    lines.append(f"  Aksepterte: {quality.hours_accepted}")
    lines.append(f"  Flagget:    {quality.hours_flagged}")
    lines.append(f"  Avvist:     {quality.hours_rejected}")
    lines.append("")
    lines.append("Avvik:")
    for i in quality.issues:
        lines.append(f"  [{i.severity:7s}] {i.code:30s} {i.message}")

    summary_path.write_text("\n".join(lines), encoding="utf-8")

    return {
        "excel": str(excel_path),
        "fasit": str(fasit_path),
        "summary": str(summary_path),
        "kpi": kpi.as_dict(),
        "quality": quality.as_dict(),
    }


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Bruk: python main.py <path-til-excel> [output-dir]")
        sys.exit(1)
    input_path = sys.argv[1]
    output_dir = sys.argv[2] if len(sys.argv) > 2 else "../output"
    result = run(input_path, output_dir)
    print()
    print("Ferdig.")
    print(f"  Excel:   {result['excel']}")
    print(f"  Fasit:   {result['fasit']}")
    print(f"  Summary: {result['summary']}")
