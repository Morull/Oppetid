"""Settlement-data parser for KraftverkUptime (Nivaa 0).

Tilsvarer framtidig ISettlementDataSource-implementasjon i .NET. Parser
maanedlig oppgjoerseksport fra portalen og returnerer normaliserte dataframes
pluss valideringsfeil. Haandterer enhetsrad, tom spacer-kolonne, DST og
datavaliderings-sjekker.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Optional

import pandas as pd


COLUMN_MAPPING = {
    "Time": "time_local",
    "MWh-Elhub": "mwh_elhub",
    "MWh-eSett": "mwh_esett",
    "Spotbud": "spotbud_mwh",
    "Spotpris": "spotpris_nok_mwh",
    "Spotomsetning": "spotomsetning_nok",
    "Ubalanse": "ubalanse_mwh",
    "RK-pris": "rk_pris_nok_mwh",
    "RK-kjop": "rk_kjop_nok",
    "RK-kjoep": "rk_kjop_nok",
    "RK-salg": "rk_salg_nok",
    "Nord Pool gebyr": "nord_pool_gebyr_nok",
    "eSett volumgebyr": "esett_volumgebyr_nok",
    "eSett ubalansegebyr": "esett_ubalansegebyr_nok",
    "Sum salg": "sum_salg_nok",
    "Meglerprovisjon": "meglerprovisjon_nok",
    "Oppgjor": "oppgjor_nok",
    "Oppgjoer": "oppgjor_nok",
    "Brutto omsetning": "brutto_omsetning_nok",
    "Produksjonplan": "produksjonplan_mwh",
    "Effektavlesninger": "effektavlesninger_mw",
    "Absolutt ubalansevolum": "abs_ubalansevolum_mwh",
    "Tap/gevinst ubalanse eks. gebyr": "ubalanse_resultat_nok",
}

# Mapping med norske spesialtegn (haandtert separat for robusthet)
_EXTRA_MAPPING = {
    "RK-kj\u00f8p": "rk_kjop_nok",
    "Oppgj\u00f8r": "oppgjor_nok",
}
COLUMN_MAPPING.update(_EXTRA_MAPPING)

REQUIRED_COLUMNS = {"time_local", "mwh_elhub"}


@dataclass
class ValidationIssue:
    severity: str
    code: str
    message: str
    affected_rows: Optional[int] = None


@dataclass
class ParsedSettlement:
    plant_name: str
    period_start: datetime
    period_end: datetime
    hourly: pd.DataFrame
    summary: pd.DataFrame
    issues: list = field(default_factory=list)
    schema_version: str = "portal-v1"


def parse_settlement(path):
    path = Path(path)
    issues = []

    xl = pd.ExcelFile(path, engine="openpyxl")
    sheets = xl.sheet_names
    if len(sheets) < 2:
        raise ValueError("Forventet minst 2 faner, fant: " + str(sheets))

    summary_sheet = sheets[0]
    hourly_sheet = None
    for s in sheets:
        if s != summary_sheet:
            hourly_sheet = s
            break

    plant_name = hourly_sheet.split(" ", 1)[-1].strip() if " " in hourly_sheet else hourly_sheet

    summary_raw = pd.read_excel(path, sheet_name=summary_sheet, engine="openpyxl", header=None)
    summary = _parse_summary(summary_raw, issues)

    # Timefane: rad 0 = tittel, rad 1 = kolonnenavn, rad 2 = enheter, rad 3+ = data
    hourly_raw = pd.read_excel(
        path, sheet_name=hourly_sheet, engine="openpyxl", header=1, skiprows=[2]
    )
    hourly = _parse_hourly(hourly_raw, issues)

    _cross_validate(summary, hourly, issues)

    period_start = hourly["time_utc"].min().to_pydatetime()
    period_end = hourly["time_utc"].max().to_pydatetime()

    return ParsedSettlement(
        plant_name=plant_name,
        period_start=period_start,
        period_end=period_end,
        hourly=hourly,
        summary=summary,
        issues=issues,
    )


def _parse_summary(raw, issues):
    if raw.empty:
        issues.append(ValidationIssue("warning", "EMPTY_SUMMARY", "Summering-fane er tom"))
        return pd.DataFrame()

    header_row = None
    for i in range(min(5, len(raw))):
        row_str = raw.iloc[i].astype(str).str.contains("Tidsserie", case=False, na=False)
        if row_str.any():
            header_row = i
            break
    if header_row is None:
        issues.append(ValidationIssue("error", "SUMMARY_HEADER_NOT_FOUND",
                                      "Fant ikke Tidsserie-header i Summering"))
        return pd.DataFrame()

    headers = raw.iloc[header_row].tolist()
    data_rows = raw.iloc[header_row + 2:]
    summary = pd.DataFrame(data_rows.values, columns=headers)
    summary = summary.dropna(axis=1, how="all").dropna(axis=0, how="all")
    summary = summary.rename(columns=COLUMN_MAPPING)
    return summary.reset_index(drop=True)


def _parse_hourly(raw, issues):
    raw = raw.dropna(axis=1, how="all")
    raw = raw.rename(columns=COLUMN_MAPPING)

    if "time_local" not in raw.columns:
        raise RuntimeError("Fant ikke time_local etter rename. Kolonner: " + str(list(raw.columns)))

    time_parsed = pd.to_datetime(raw["time_local"], dayfirst=True, errors="coerce")
    na_count = int(time_parsed.isna().sum())
    if na_count:
        issues.append(ValidationIssue("error", "INVALID_TIMESTAMP",
                                      str(na_count) + " rader med ugyldig Time-verdi ignoreres",
                                      affected_rows=na_count))
    raw = raw.loc[time_parsed.notna()].copy()
    time_parsed = time_parsed[time_parsed.notna()]

    try:
        localized = time_parsed.dt.tz_localize("Europe/Oslo", ambiguous="infer", nonexistent="NaT")
    except Exception:
        localized = time_parsed.dt.tz_localize("Europe/Oslo", ambiguous=True, nonexistent="NaT")
        issues.append(ValidationIssue("warning", "DST_AMBIGUOUS",
                                      "DST-tvetydig tid haandtert med ambiguous=True-fallback"))

    raw["time_local"] = localized
    raw["time_utc"] = localized.dt.tz_convert("UTC")

    for col in raw.columns:
        if col in ("time_local", "time_utc"):
            continue
        raw[col] = pd.to_numeric(raw[col], errors="coerce")

    raw = raw.sort_values("time_utc").reset_index(drop=True)
    return raw


def _cross_validate(summary, hourly, issues):
    if summary.empty or hourly.empty:
        return
    for col in ("mwh_elhub", "mwh_esett", "oppgjor_nok", "sum_salg_nok"):
        if col in summary.columns and col in hourly.columns:
            s_val = pd.to_numeric(summary[col].iloc[0], errors="coerce")
            h_sum = hourly[col].sum()
            if pd.isna(s_val):
                continue
            diff = abs(s_val - h_sum)
            tol = max(0.01, abs(s_val) * 0.001)
            if diff > tol:
                issues.append(ValidationIssue(
                    "warning", "SUMMARY_HOURLY_MISMATCH",
                    "Summering." + col + "=" + str(round(float(s_val), 2))
                    + " != sum(timer)=" + str(round(float(h_sum), 2)),
                ))

    if "mwh_esett" in hourly.columns and "mwh_elhub" in hourly.columns:
        diff_mask = (hourly["mwh_elhub"] - hourly["mwh_esett"]).abs() > 0.001
        if diff_mask.any():
            issues.append(ValidationIssue(
                "warning", "ELHUB_ESETT_MISMATCH",
                str(int(diff_mask.sum())) + " timer har MWh-Elhub != MWh-eSett",
                affected_rows=int(diff_mask.sum()),
            ))
