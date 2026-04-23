"""Excel-rapport generator – produserer månedlig oppetidsrapport.

Tilsvarer framtidig IReportRenderer for Excel. Output har faner:
  1. Oversikt – KPI-tabell med verdi, enhet, timer, confidence
  2. Tilstandsfordeling – timer per UnitState
  3. Time-for-time klassifisering – alle 672 rader med state, cause, rationale
  4. Plan vs Bud vs Elhub – 3-veis serie med avvik
  5. Hendelser – sammenhengende tilstands-hendelser
  6. Datakvalitet – per-kolonne og sammendrag
  7. Fasit – JSON-ekvivalent for regresjon
"""
from __future__ import annotations

import json
from pathlib import Path

import pandas as pd
import xlsxwriter

from kpi import KpiReport
from quality import DataQualityReport


def write_excel_report(
    path: str | Path,
    kpi: KpiReport,
    classified: pd.DataFrame,
    events: pd.DataFrame,
    quality: DataQualityReport,
) -> None:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)

    wb = xlsxwriter.Workbook(str(path))

    # Formater
    fmt_title = wb.add_format({"bold": True, "font_size": 14, "font_color": "#1F3A5F"})
    fmt_header = wb.add_format({"bold": True, "bg_color": "#1F3A5F", "font_color": "white",
                                "border": 1, "align": "center"})
    fmt_cell = wb.add_format({"border": 1})
    fmt_num = wb.add_format({"border": 1, "num_format": "#,##0.00"})
    fmt_pct = wb.add_format({"border": 1, "num_format": "0.0%"})
    fmt_int = wb.add_format({"border": 1, "num_format": "#,##0"})
    fmt_nok = wb.add_format({"border": 1, "num_format": "#,##0 \"NOK\""})
    fmt_date = wb.add_format({"border": 1, "num_format": "yyyy-mm-dd hh:mm"})
    fmt_low = wb.add_format({"border": 1, "bg_color": "#FFE4E1"})
    fmt_med = wb.add_format({"border": 1, "bg_color": "#FFF8DC"})
    fmt_high = wb.add_format({"border": 1, "bg_color": "#E8F5E9"})

    _sheet_overview(wb, kpi, quality, fmt_title, fmt_header, fmt_cell, fmt_num, fmt_pct, fmt_int)
    _sheet_state_distribution(wb, classified, fmt_title, fmt_header, fmt_cell, fmt_int, fmt_pct)
    _sheet_hourly(wb, classified, fmt_title, fmt_header, fmt_cell, fmt_num, fmt_date,
                  fmt_low, fmt_med, fmt_high)
    _sheet_plan_vs_bid(wb, classified, fmt_title, fmt_header, fmt_cell, fmt_num, fmt_date)
    _sheet_events(wb, events, fmt_title, fmt_header, fmt_cell, fmt_num, fmt_date, fmt_int)
    _sheet_quality(wb, quality, fmt_title, fmt_header, fmt_cell, fmt_num, fmt_int)

    wb.close()


def _sheet_overview(wb, kpi: KpiReport, quality: DataQualityReport,
                    fmt_title, fmt_header, fmt_cell, fmt_num, fmt_pct, fmt_int):
    ws = wb.add_worksheet("Oversikt")
    ws.set_column("A:A", 36)
    ws.set_column("B:B", 14)
    ws.set_column("C:C", 10)
    ws.set_column("D:D", 10)
    ws.set_column("E:E", 12)
    ws.set_column("F:F", 60)

    ws.write("A1", f"Oppetidsrapport – {kpi.plant_id}", fmt_title)
    ws.write("A2", f"Periode: {kpi.period_start_utc} – {kpi.period_end_utc} UTC")
    ws.write("A3", f"Timer totalt: {kpi.period_hours}")
    ws.write("A4", f"Data­kvalitet: {quality.hours_accepted}/{quality.hours_expected} aksepterte, "
                    f"{quality.hours_flagged} flagget")

    headers = ["KPI", "Verdi", "Enhet", "Timer", "Confidence", "Definisjon"]
    row = 6
    for i, h in enumerate(headers):
        ws.write(row, i, h, fmt_header)

    row = 7
    # Grupper etter kategori
    order = ["time", "energy", "event", "plan"]
    for cat in order:
        ws.merge_range(row, 0, row, 5, _category_title(cat), fmt_header)
        row += 1
        for k in [x for x in kpi.kpis if x.category == cat]:
            ws.write(row, 0, k.name, fmt_cell)
            if k.value is None:
                ws.write(row, 1, "-", fmt_cell)
            elif k.unit == "ratio":
                ws.write_number(row, 1, k.value, fmt_pct)
            elif k.unit in ("hours", "events"):
                ws.write_number(row, 1, k.value, fmt_int)
            else:
                ws.write_number(row, 1, k.value, fmt_num)
            ws.write(row, 2, k.unit, fmt_cell)
            ws.write_number(row, 3, k.hours_basis, fmt_int)
            ws.write_number(row, 4, k.confidence, fmt_pct)
            ws.write(row, 5, k.definition, fmt_cell)
            row += 1


def _category_title(cat: str) -> str:
    return {
        "time": "Tidsbaserte KPI-er (IEEE 762 / NERC GADS)",
        "energy": "Energibaserte KPI-er",
        "event": "Hendelsesbaserte KPI-er",
        "plan": "Plan- og markedsavvik (3-veis sammenligning)",
    }.get(cat, cat)


def _sheet_state_distribution(wb, classified, fmt_title, fmt_header, fmt_cell, fmt_int, fmt_pct):
    ws = wb.add_worksheet("Tilstandsfordeling")
    ws.set_column("A:A", 28)
    ws.set_column("B:B", 10)
    ws.set_column("C:C", 10)
    ws.write("A1", "Tilstandsfordeling per time", fmt_title)
    ws.write_row("A3", ["UnitState", "Timer", "Andel"], fmt_header)
    counts = classified["state"].value_counts()
    total = int(counts.sum())
    for i, (state, count) in enumerate(counts.items()):
        ws.write(3 + i, 0, state, fmt_cell)
        ws.write_number(3 + i, 1, int(count), fmt_int)
        ws.write_number(3 + i, 2, float(count) / total if total else 0, fmt_pct)

    # Søylediagram
    chart = wb.add_chart({"type": "bar"})
    chart.add_series({
        "categories": ["Tilstandsfordeling", 3, 0, 3 + len(counts) - 1, 0],
        "values":     ["Tilstandsfordeling", 3, 1, 3 + len(counts) - 1, 1],
        "name": "Timer per tilstand",
    })
    chart.set_title({"name": "Tilstandsfordeling"})
    chart.set_x_axis({"name": "Timer"})
    ws.insert_chart("E3", chart, {"x_scale": 1.2, "y_scale": 1.2})


def _sheet_hourly(wb, classified, fmt_title, fmt_header, fmt_cell, fmt_num, fmt_date,
                  fmt_low, fmt_med, fmt_high):
    ws = wb.add_worksheet("Timer")
    ws.set_column("A:A", 20)
    ws.set_column("B:E", 12)
    ws.set_column("F:F", 22)
    ws.set_column("G:G", 12)
    ws.set_column("H:H", 20)
    ws.set_column("I:I", 50)

    ws.write("A1", "Time-for-time klassifisering", fmt_title)
    headers = ["Time (UTC)", "MWh-Elhub", "Plan", "Spotbud", "Spotpris",
               "UnitState", "Conf.", "Cause", "Rationale"]
    for i, h in enumerate(headers):
        ws.write(2, i, h, fmt_header)

    for ri, row in classified.iterrows():
        r = 3 + ri
        conf = float(row.get("confidence", 0))
        fmt_conf = fmt_low if conf < 0.5 else fmt_med if conf < 0.8 else fmt_high
        ws.write_datetime(r, 0, row["time_utc"].to_pydatetime().replace(tzinfo=None), fmt_date)
        _write_num(ws, r, 1, row.get("mwh_elhub"), fmt_num)
        _write_num(ws, r, 2, row.get("produksjonplan_mwh"), fmt_num)
        _write_num(ws, r, 3, row.get("spotbud_mwh"), fmt_num)
        _write_num(ws, r, 4, row.get("spotpris_nok_mwh"), fmt_num)
        ws.write(r, 5, str(row.get("state", "")), fmt_cell)
        ws.write_number(r, 6, conf, fmt_conf)
        ws.write(r, 7, str(row.get("cause_code", "")), fmt_cell)
        ws.write(r, 8, str(row.get("rationale", "")), fmt_cell)

    ws.freeze_panes(3, 0)


def _sheet_plan_vs_bid(wb, classified, fmt_title, fmt_header, fmt_cell, fmt_num, fmt_date):
    ws = wb.add_worksheet("Plan vs Bud vs Elhub")
    ws.set_column("A:A", 20)
    ws.set_column("B:D", 12)
    ws.set_column("E:F", 12)

    ws.write("A1", "3-veis sammenligning: Produksjonplan, Spotbud, Elhub", fmt_title)
    headers = ["Time (UTC)", "Plan (MWh)", "Spotbud (MWh)", "Elhub (MWh)",
               "Plan−Elhub", "Bud−Elhub"]
    for i, h in enumerate(headers):
        ws.write(2, i, h, fmt_header)

    for ri, row in classified.iterrows():
        r = 3 + ri
        plan = row.get("produksjonplan_mwh")
        bud = row.get("spotbud_mwh")
        elhub = row.get("mwh_elhub")
        ws.write_datetime(r, 0, row["time_utc"].to_pydatetime().replace(tzinfo=None), fmt_date)
        _write_num(ws, r, 1, plan, fmt_num)
        _write_num(ws, r, 2, bud, fmt_num)
        _write_num(ws, r, 3, elhub, fmt_num)
        if pd.notna(plan) and pd.notna(elhub):
            ws.write_number(r, 4, float(plan) - float(elhub), fmt_num)
        if pd.notna(bud) and pd.notna(elhub):
            ws.write_number(r, 5, float(bud) - float(elhub), fmt_num)

    # Linjediagram for 3-veis
    n = len(classified)
    last = 3 + n - 1
    chart = wb.add_chart({"type": "line"})
    for idx, name in enumerate(["Plan", "Spotbud", "Elhub"]):
        chart.add_series({
            "name":       f"='Plan vs Bud vs Elhub'!${chr(ord('B') + idx)}$3",
            "categories": ["Plan vs Bud vs Elhub", 3, 0, last, 0],
            "values":     ["Plan vs Bud vs Elhub", 3, 1 + idx, last, 1 + idx],
        })
    chart.set_title({"name": "Plan vs Bud vs Elhub over perioden"})
    chart.set_x_axis({"name": "Time (UTC)"})
    chart.set_y_axis({"name": "MWh"})
    chart.set_size({"width": 900, "height": 420})
    ws.insert_chart("H3", chart)

    ws.freeze_panes(3, 0)


def _sheet_events(wb, events, fmt_title, fmt_header, fmt_cell, fmt_num, fmt_date, fmt_int):
    ws = wb.add_worksheet("Hendelser")
    ws.set_column("A:A", 20)
    ws.set_column("B:C", 20)
    ws.set_column("D:D", 10)
    ws.set_column("E:G", 14)

    ws.write("A1", "Sammenhengende tilstandshendelser", fmt_title)
    if events.empty:
        ws.write("A3", "Ingen hendelser")
        return
    headers = ["UnitState", "Fra (UTC)", "Til (UTC)", "Timer", "MWh sum", "Plan sum", "Cause"]
    for i, h in enumerate(headers):
        ws.write(2, i, h, fmt_header)

    for ri, row in events.iterrows():
        r = 3 + ri
        ws.write(r, 0, str(row["state"]), fmt_cell)
        ws.write_datetime(r, 1, row["start_utc"].to_pydatetime().replace(tzinfo=None), fmt_date)
        ws.write_datetime(r, 2, row["end_utc"].to_pydatetime().replace(tzinfo=None), fmt_date)
        ws.write_number(r, 3, int(row["duration_h"]), fmt_int)
        ws.write_number(r, 4, float(row["mwh_sum"] or 0), fmt_num)
        ws.write_number(r, 5, float(row["plan_sum"] or 0), fmt_num)
        ws.write(r, 6, str(row.get("cause_code", "")), fmt_cell)

    ws.freeze_panes(3, 0)


def _sheet_quality(wb, quality: DataQualityReport, fmt_title, fmt_header, fmt_cell, fmt_num, fmt_int):
    ws = wb.add_worksheet("Datakvalitet")
    ws.set_column("A:A", 32)
    ws.set_column("B:F", 14)

    ws.write("A1", "Datakvalitetsrapport", fmt_title)
    summary = [
        ("Anlegg", quality.plant_name),
        ("Timer forventet", quality.hours_expected),
        ("Timer mottatt", quality.hours_received),
        ("Timer akseptert (Good)", quality.hours_accepted),
        ("Timer flagget", quality.hours_flagged),
        ("Timer avvist", quality.hours_rejected),
    ]
    for i, (k, v) in enumerate(summary):
        ws.write(2 + i, 0, k, fmt_cell)
        if isinstance(v, int):
            ws.write_number(2 + i, 1, v, fmt_int)
        else:
            ws.write(2 + i, 1, str(v), fmt_cell)

    row = 2 + len(summary) + 2
    ws.write(row, 0, "Per-kolonne statistikk", fmt_title)
    row += 2
    headers = ["Kolonne", "Null", "Non-null", "Min", "Max", "Sum"]
    for i, h in enumerate(headers):
        ws.write(row, i, h, fmt_header)
    row += 1
    for col, stats in quality.column_stats.items():
        ws.write(row, 0, col, fmt_cell)
        ws.write_number(row, 1, stats.get("null_count", 0), fmt_int)
        ws.write_number(row, 2, stats.get("non_null_count", 0), fmt_int)
        _write_num(ws, row, 3, stats.get("min"), fmt_num)
        _write_num(ws, row, 4, stats.get("max"), fmt_num)
        _write_num(ws, row, 5, stats.get("sum"), fmt_num)
        row += 1

    row += 2
    ws.write(row, 0, "Avvik og varsler", fmt_title)
    row += 2
    headers = ["Alvorlighet", "Kode", "Melding", "Rader"]
    for i, h in enumerate(headers):
        ws.write(row, i, h, fmt_header)
    row += 1
    for issue in quality.issues:
        ws.write(row, 0, issue.severity, fmt_cell)
        ws.write(row, 1, issue.code, fmt_cell)
        ws.write(row, 2, issue.message, fmt_cell)
        if issue.affected_rows is not None:
            ws.write_number(row, 3, issue.affected_rows, fmt_int)
        row += 1


def _write_num(ws, row, col, val, fmt):
    if val is None or (isinstance(val, float) and pd.isna(val)):
        ws.write_blank(row, col, None, fmt)
    else:
        ws.write_number(row, col, float(val), fmt)


def write_json_fasit(path: str | Path, kpi: KpiReport, quality: DataQualityReport,
                      classified: pd.DataFrame) -> None:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    state_counts = classified["state"].value_counts().to_dict()
    fasit = {
        "plant_id": kpi.plant_id,
        "period_start_utc": kpi.period_start_utc,
        "period_end_utc": kpi.period_end_utc,
        "period_hours": kpi.period_hours,
        "state_counts": {str(k): int(v) for k, v in state_counts.items()},
        "kpis": {k.name: {"value": k.value, "unit": k.unit, "hours_basis": k.hours_basis}
                 for k in kpi.kpis},
        "quality": {
            "hours_expected": quality.hours_expected,
            "hours_received": quality.hours_received,
            "hours_accepted": quality.hours_accepted,
            "hours_flagged": quality.hours_flagged,
            "hours_rejected": quality.hours_rejected,
            "issues": [i.code for i in quality.issues],
        },
    }
    path.write_text(json.dumps(fasit, indent=2, ensure_ascii=False), encoding="utf-8")
