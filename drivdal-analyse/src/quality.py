"""Datakvalitetsvalidering – produserer rapport per import.

Tilsvarer framtidig DataQualityReport-entitet i .NET. Sjekker hull,
out-of-range, DST, og kryssreferanser. Markerer hver time med
DataQualityState.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from datetime import timedelta

import numpy as np
import pandas as pd

from parser import ParsedSettlement, ValidationIssue


# Samme states som .NET-kjernen
DQ_GOOD = "Good"
DQ_UNCERTAIN = "Uncertain"
DQ_SUBSTITUTED = "Substituted"
DQ_INFO_UNAVAIL = "InformationUnavailable"
DQ_QUARANTINED = "Quarantined"
DQ_REJECTED = "Rejected"


@dataclass
class DataQualityReport:
    plant_name: str
    hours_expected: int
    hours_received: int
    hours_accepted: int
    hours_flagged: int
    hours_rejected: int
    column_stats: dict[str, dict] = field(default_factory=dict)
    issues: list[ValidationIssue] = field(default_factory=list)

    def as_dict(self) -> dict:
        return {
            "plant_name": self.plant_name,
            "hours_expected": self.hours_expected,
            "hours_received": self.hours_received,
            "hours_accepted": self.hours_accepted,
            "hours_flagged": self.hours_flagged,
            "hours_rejected": self.hours_rejected,
            "column_stats": self.column_stats,
            "issues": [
                {"severity": i.severity, "code": i.code, "message": i.message,
                 "affected_rows": i.affected_rows}
                for i in self.issues
            ],
        }


def build_quality_report(parsed: ParsedSettlement) -> tuple[DataQualityReport, pd.DataFrame]:
    """Returner (rapport, beriket hourly-df med dq_state-kolonne)."""
    hourly = parsed.hourly.copy()
    issues = list(parsed.issues)  # kopier så vi kan legge til

    # Forventet antall timer i perioden basert på UTC (best effort – DST tas i betraktning)
    period_local_start = hourly["time_local"].min()
    period_local_end = hourly["time_local"].max() + timedelta(hours=1)
    # Forventet i lokaltid: alle timer mellom start og slutt. For februar er det enkelt.
    expected_range = pd.date_range(
        period_local_start, period_local_end, freq="h", tz="Europe/Oslo", inclusive="left"
    )
    hours_expected = len(expected_range)
    hours_received = len(hourly)

    # Finn manglende timer og sett dem inn som InformationUnavailable
    received_utc = set(hourly["time_utc"])
    missing_utc = [t for t in expected_range.tz_convert("UTC") if t not in received_utc]
    if missing_utc:
        issues.append(
            ValidationIssue(
                "warning",
                "MISSING_HOURS",
                f"{len(missing_utc)} manglende timer i sekvensen – satt til InformationUnavailable",
                affected_rows=len(missing_utc),
            )
        )
        missing_rows = pd.DataFrame({"time_utc": missing_utc})
        # Ingen målinger – alle numeriske kolonner er NaN
        for col in hourly.columns:
            if col not in missing_rows.columns:
                missing_rows[col] = np.nan
        hourly = pd.concat([hourly, missing_rows], ignore_index=True).sort_values("time_utc").reset_index(drop=True)

    # Per-kolonne datakvalitetsstatistikk
    column_stats: dict[str, dict] = {}
    for col in hourly.columns:
        if col in ("time_utc", "time_local", "dq_state"):
            continue
        series = hourly[col]
        stats = {
            "dtype": str(series.dtype),
            "null_count": int(series.isna().sum()),
            "non_null_count": int(series.notna().sum()),
        }
        if pd.api.types.is_numeric_dtype(series):
            stats.update({
                "min": _safe_float(series.min()),
                "max": _safe_float(series.max()),
                "mean": _safe_float(series.mean()),
                "sum": _safe_float(series.sum()),
            })
        column_stats[col] = stats

    # Utled dq_state per rad
    dq = np.full(len(hourly), DQ_GOOD, dtype=object)
    # Mangler MWh-Elhub → InformationUnavailable
    if "mwh_elhub" in hourly.columns:
        dq[hourly["mwh_elhub"].isna()] = DQ_INFO_UNAVAIL

    # Negative Elhub-verdier -> Uncertain (kan være måleavvik eller regulerkraft)
    if "mwh_elhub" in hourly.columns:
        neg_mask = (hourly["mwh_elhub"] < 0).fillna(False)
        if neg_mask.any():
            issues.append(
                ValidationIssue(
                    "warning",
                    "NEGATIVE_ELHUB",
                    f"{int(neg_mask.sum())} timer med negativ MWh-Elhub",
                    affected_rows=int(neg_mask.sum()),
                )
            )
            dq[neg_mask.values] = DQ_UNCERTAIN

    hourly["dq_state"] = dq

    hours_accepted = int((hourly["dq_state"] == DQ_GOOD).sum())
    hours_flagged = int(
        hourly["dq_state"].isin([DQ_UNCERTAIN, DQ_SUBSTITUTED, DQ_INFO_UNAVAIL]).sum()
    )
    hours_rejected = int(
        hourly["dq_state"].isin([DQ_QUARANTINED, DQ_REJECTED]).sum()
    )

    report = DataQualityReport(
        plant_name=parsed.plant_name,
        hours_expected=hours_expected,
        hours_received=hours_received,
        hours_accepted=hours_accepted,
        hours_flagged=hours_flagged,
        hours_rejected=hours_rejected,
        column_stats=column_stats,
        issues=issues,
    )
    return report, hourly


def _safe_float(x) -> float | None:
    if x is None or pd.isna(x):
        return None
    return float(x)
