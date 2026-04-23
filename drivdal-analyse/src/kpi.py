"""KPI-beregninger per IEEE 762 og NERC GADS, pluss hydro- og plan-spesifikke.

Tilsvarer framtidig UptimeAnalyzer.Settlement sin KPI-matrise i .NET.
Alle formler holder seg til standard definisjoner. Verdiene rapporteres
sammen med antall timer de er basert på og gjennomsnittlig confidence
fra klassifiseringen.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Optional

import numpy as np
import pandas as pd

from classifier import (
    UNIT_IN_SERVICE, UNIT_RESERVE_SHUTDOWN, UNIT_PLANNED_OUTAGE,
    UNIT_MAINT_OUTAGE, UNIT_FORCED_OUTAGE, UNIT_FORCED_DERATING,
    UNIT_PLANNED_DERATING, UNIT_RESOURCE_UNAVAIL, UNIT_INFO_UNAVAIL,
    PlantConfig,
)


OMC_STATES = {UNIT_RESOURCE_UNAVAIL}  # Outside Management Control
AVAILABLE_STATES = {UNIT_IN_SERVICE, UNIT_RESERVE_SHUTDOWN}
UNAVAILABLE_STATES = {UNIT_PLANNED_OUTAGE, UNIT_MAINT_OUTAGE, UNIT_FORCED_OUTAGE}


@dataclass
class KpiResult:
    name: str
    value: Optional[float]
    unit: str
    hours_basis: int
    confidence: float  # 0-1
    category: str      # "time" | "energy" | "event" | "plan"
    definition: str

    def as_dict(self) -> dict:
        return {
            "name": self.name,
            "value": self.value,
            "unit": self.unit,
            "hours_basis": self.hours_basis,
            "confidence": self.confidence,
            "category": self.category,
            "definition": self.definition,
        }


@dataclass
class KpiReport:
    plant_id: str
    period_start_utc: str
    period_end_utc: str
    period_hours: int
    state_counts: dict[str, int] = field(default_factory=dict)
    kpis: list[KpiResult] = field(default_factory=list)

    def as_dict(self) -> dict:
        return {
            "plant_id": self.plant_id,
            "period_start_utc": self.period_start_utc,
            "period_end_utc": self.period_end_utc,
            "period_hours": self.period_hours,
            "state_counts": self.state_counts,
            "kpis": [k.as_dict() for k in self.kpis],
        }


def compute_kpis(classified: pd.DataFrame, plant: PlantConfig) -> KpiReport:
    df = classified.copy()
    ph = len(df)
    state_counts = df["state"].value_counts().to_dict()
    avg_conf = float(df["confidence"].mean())

    # Tell timer per tilstand
    sh = _count(df, {UNIT_IN_SERVICE})  # ServiceHours
    rsh = _count(df, {UNIT_RESERVE_SHUTDOWN})
    ah = sh + rsh  # AvailableHours
    poh = _count(df, {UNIT_PLANNED_OUTAGE})
    moh = _count(df, {UNIT_MAINT_OUTAGE})
    foh = _count(df, {UNIT_FORCED_OUTAGE})
    fdh = _count(df, {UNIT_FORCED_DERATING})
    pdh = _count(df, {UNIT_PLANNED_DERATING})
    ruh = _count(df, {UNIT_RESOURCE_UNAVAIL})  # OMC
    iuh = _count(df, {UNIT_INFO_UNAVAIL})  # Egen kategori
    uh = poh + moh + foh

    # Unit Performance (ekskluderer OMC og InformationUnavailable fra nevner)
    effective_hours = ph - ruh - iuh

    kpis: list[KpiResult] = []

    kpis.append(KpiResult(
        "ServiceHours_SH", sh, "hours", ph, 1.0, "time",
        "Antall timer i InService-tilstand",
    ))
    kpis.append(KpiResult(
        "AvailableHours_AH", ah, "hours", ph, 1.0, "time",
        "SH + ReserveShutdownHours (timer verket er tilgjengelig)",
    ))
    kpis.append(KpiResult(
        "UnavailableHours_UH", uh, "hours", ph, 1.0, "time",
        "POH + MOH + FOH (timer verket ikke er tilgjengelig)",
    ))
    kpis.append(KpiResult(
        "ForcedOutageHours_FOH", foh, "hours", ph, 1.0, "time",
        "Timer i ForcedOutage-tilstand",
    ))
    kpis.append(KpiResult(
        "InformationUnavailable_Hours", iuh, "hours", ph, 1.0, "time",
        "Timer uten data (ekskluderes fra KPI-nevnere)",
    ))
    kpis.append(KpiResult(
        "ResourceUnavailable_Hours", ruh, "hours", ph, 1.0, "time",
        "Timer med ressursbegrensning (OMC)",
    ))

    # Tilgjengelighets-indekser
    kpis.append(KpiResult(
        "AvailabilityFactor_AF",
        _safe_ratio(ah, effective_hours), "ratio", effective_hours, avg_conf, "time",
        "AF = AH / (PH − OMC − IU). Unit Performance Index.",
    ))
    kpis.append(KpiResult(
        "AvailabilityFactor_AF_SystemView",
        _safe_ratio(ah, ph - iuh), "ratio", ph - iuh, avg_conf, "time",
        "AF inkludert OMC i nevner. System Reliability Index.",
    ))
    kpis.append(KpiResult(
        "ServiceFactor_SF",
        _safe_ratio(sh, effective_hours), "ratio", effective_hours, avg_conf, "time",
        "SF = SH / effektive timer",
    ))
    kpis.append(KpiResult(
        "ForcedOutageRate_FOR",
        _safe_ratio(foh, foh + sh), "ratio", foh + sh, avg_conf, "time",
        "FOR = FOH / (FOH + SH)",
    ))

    # Equivalent metrics basert på derated hours som MWh-ekvivalent
    # EFDH = equivalent forced derated hours = sum(plan - elhub) for FD-timer / pnom
    if plant.nominal_power_mw > 0:
        fd_mask = df["state"] == UNIT_FORCED_DERATING
        plan = df.loc[fd_mask, "produksjonplan_mwh"].fillna(plant.nominal_power_mw)
        actual = df.loc[fd_mask, "mwh_elhub"].fillna(0)
        efdh = float(((plan - actual).clip(lower=0) / plant.nominal_power_mw).sum())
        kpis.append(KpiResult(
            "EquivalentForcedDerated_Hours", efdh, "hours", ph, avg_conf, "time",
            "EFDH = Σ (Plan - Elhub) / Pnom for timer i ForcedDerating",
        ))
        eaf_num = ah - efdh
        kpis.append(KpiResult(
            "EquivalentAvailabilityFactor_EAF",
            _safe_ratio(eaf_num, effective_hours), "ratio", effective_hours, avg_conf, "time",
            "EAF = (AH − EFDH) / effektive timer",
        ))

    # Energi-baserte
    total_mwh = float(df["mwh_elhub"].fillna(0).sum())
    max_possible_mwh = plant.nominal_power_mw * ph
    kpis.append(KpiResult(
        "TotalProduction_MWh", total_mwh, "MWh", ph, 1.0, "energy",
        "Σ MWh-Elhub",
    ))
    kpis.append(KpiResult(
        "CapacityFactor_CF",
        _safe_ratio(total_mwh, max_possible_mwh), "ratio", ph, avg_conf, "energy",
        f"CF = Σ Elhub / (Pnom × PH), Pnom={plant.nominal_power_mw} MW",
    ))
    max_output_mwh = plant.nominal_power_mw * sh
    kpis.append(KpiResult(
        "OutputFactor_OF",
        _safe_ratio(total_mwh, max_output_mwh), "ratio", sh, avg_conf, "energy",
        "OF = Σ Elhub / (Pnom × SH)",
    ))

    # Hendelses-baserte
    fo_events = _count_events(df, {UNIT_FORCED_OUTAGE})
    mtbf = _safe_ratio(sh, fo_events) if fo_events else None
    mttr = _safe_ratio(foh, fo_events) if fo_events else None
    kpis.append(KpiResult(
        "ForcedOutageEvents", fo_events, "events", ph, avg_conf, "event",
        "Antall sammenhengende ForcedOutage-hendelser",
    ))
    kpis.append(KpiResult(
        "MTBF", mtbf, "hours", ph, avg_conf, "event",
        "Mean Time Between Failures = SH / FO-hendelser",
    ))
    kpis.append(KpiResult(
        "MTTR", mttr, "hours", ph, avg_conf, "event",
        "Mean Time To Repair = FOH / FO-hendelser",
    ))

    # Plan-avviks KPI-er (3-veis Plan / Bud / Elhub)
    if "produksjonplan_mwh" in df.columns:
        plan_sum = float(df["produksjonplan_mwh"].fillna(0).sum())
        bud_sum = float(df.get("spotbud_mwh", pd.Series()).fillna(0).sum()) if "spotbud_mwh" in df else 0.0
        kpis.append(KpiResult(
            "PlanTotal_MWh", plan_sum, "MWh", ph, 1.0, "plan",
            "Σ Produksjonplan",
        ))
        kpis.append(KpiResult(
            "SpotbudTotal_MWh", bud_sum, "MWh", ph, 1.0, "plan",
            "Σ Spotbud (Day-Ahead)",
        ))
        kpis.append(KpiResult(
            "PlanFulfillment",
            _safe_ratio(total_mwh, plan_sum), "ratio", ph, 1.0, "plan",
            "Elhub / Plan – andel av planlagt produksjon som ble levert",
        ))
        kpis.append(KpiResult(
            "BidAccuracy",
            _safe_ratio(total_mwh, bud_sum) if bud_sum else None, "ratio", ph, 1.0, "plan",
            "Elhub / Spotbud – andel av markedsforpliktelse som ble levert",
        ))
        kpis.append(KpiResult(
            "PlanToBidDeviation",
            _safe_ratio(plan_sum - bud_sum, plan_sum) if plan_sum else None, "ratio", ph, 1.0, "plan",
            "(Plan − Bud) / Plan – hvor mye av planen ble ikke budt inn",
        ))
        kpis.append(KpiResult(
            "PlanDeviation_MWh", total_mwh - plan_sum, "MWh", ph, 1.0, "plan",
            "Elhub − Plan",
        ))
        kpis.append(KpiResult(
            "PlanDeviation_NOK",
            _plan_deviation_nok(df), "NOK", ph, 1.0, "plan",
            "Verdien av planavviket priset til spotpris per time",
        ))

    # Ubalansekostnad
    if "ubalanse_resultat_nok" in df.columns:
        imb_nok = float(df["ubalanse_resultat_nok"].fillna(0).sum())
        kpis.append(KpiResult(
            "ImbalanceResult_NOK", imb_nok, "NOK", ph, 1.0, "plan",
            "Tap/gevinst på ubalanse (før gebyrer) – negativ = tap",
        ))
    if "abs_ubalansevolum_mwh" in df.columns and "rk_pris_nok_mwh" in df.columns:
        corr_df = df[["abs_ubalansevolum_mwh", "rk_pris_nok_mwh"]].dropna()
        corr = float(corr_df.corr().iloc[0, 1]) if len(corr_df) > 1 else None
        kpis.append(KpiResult(
            "ImbalanceCorrelation_AbsVol_RKPris", corr, "correlation", len(corr_df),
            1.0, "plan",
            "Korrelasjon mellom ubalansevolum og regulerkraftpris",
        ))

    report = KpiReport(
        plant_id=plant.plant_id,
        period_start_utc=str(df["time_utc"].min()),
        period_end_utc=str(df["time_utc"].max()),
        period_hours=ph,
        state_counts={k: int(v) for k, v in state_counts.items()},
        kpis=kpis,
    )
    return report


def _count(df: pd.DataFrame, states: set[str]) -> int:
    return int(df["state"].isin(states).sum())


def _count_events(df: pd.DataFrame, states: set[str]) -> int:
    """Tell sammenhengende hendelser (run-based)."""
    mask = df["state"].isin(states).values
    if not mask.any():
        return 0
    # Tell antall run-starter
    prev = np.concatenate([[False], mask[:-1]])
    starts = mask & ~prev
    return int(starts.sum())


def _safe_ratio(num: float, den: float) -> Optional[float]:
    if den is None or den == 0 or pd.isna(den):
        return None
    if num is None or pd.isna(num):
        return None
    return float(num) / float(den)


def _plan_deviation_nok(df: pd.DataFrame) -> Optional[float]:
    if "produksjonplan_mwh" not in df or "spotpris_nok_mwh" not in df or "mwh_elhub" not in df:
        return None
    dev = (df["mwh_elhub"].fillna(0) - df["produksjonplan_mwh"].fillna(0))
    val = (dev * df["spotpris_nok_mwh"].fillna(0)).sum()
    return float(val)
