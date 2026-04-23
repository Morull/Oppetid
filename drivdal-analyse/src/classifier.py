"""Proxy-klassifisering av hver time basert på settlement-data (Nivå 0).

Tilsvarer framtidig UptimeAnalyzer.Settlement i .NET. Klassifiserer hver
time som en UnitState med confidence og cause code. Regler er betinget
på PlantType (Regulated her; RunOfRiver blir lagt til på Nivå 1 når
hydrologi kobles på).

Viktig metode: bruker produksjonsplanen som sterkt signal om operatør-
intensjon. Plan > 0 men Elhub = 0 er et mye sterkere FO-signal enn
bare en null-produksjonstime alene ville vært.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Iterable

import numpy as np
import pandas as pd


# UnitState-enum, matcher .NET-kjernen
UNIT_IN_SERVICE = "InService"
UNIT_RESERVE_SHUTDOWN = "ReserveShutdown"
UNIT_PLANNED_OUTAGE = "PlannedOutage"
UNIT_MAINT_OUTAGE = "MaintenanceOutage"
UNIT_FORCED_OUTAGE = "ForcedOutage"
UNIT_FORCED_DERATING = "ForcedDerating"
UNIT_PLANNED_DERATING = "PlannedDerating"
UNIT_RESOURCE_UNAVAIL = "ResourceUnavailable"
UNIT_INFO_UNAVAIL = "InformationUnavailable"


@dataclass
class PlantConfig:
    plant_id: str
    plant_type: str  # "Regulated" | "RunOfRiver" | "Mixed" | "Pumped"
    nominal_power_mw: float
    derating_threshold: float = 0.90  # Elhub < Plan * threshold → derating
    sustained_stop_hours: int = 24  # ≥ N timer sammenhengende → kandidat for PO
    marginal_cost_nok_mwh: float = 100.0  # for RS-klassifisering


def classify(hourly: pd.DataFrame, plant: PlantConfig) -> pd.DataFrame:
    """Returner hourly-df beriket med kolonnene: state, confidence, cause_code, rationale."""
    df = hourly.copy().reset_index(drop=True)

    # Beregnet kolonne: kandidat for planlagt stans (lange sammenhengende 0-timer)
    df["_is_zero"] = (df["mwh_elhub"].fillna(-1) == 0)
    df["_zero_run_len"] = _run_lengths(df["_is_zero"].values)

    # Median spotpris brukes for vurdering av FO vs RS
    median_spot = df["spotpris_nok_mwh"].median() if "spotpris_nok_mwh" in df else np.nan

    states: list[str] = []
    confidences: list[float] = []
    causes: list[str] = []
    rationales: list[str] = []

    for _, row in df.iterrows():
        state, conf, cause, why = _classify_row(row, plant, median_spot)
        states.append(state)
        confidences.append(conf)
        causes.append(cause)
        rationales.append(why)

    df["state"] = states
    df["confidence"] = confidences
    df["cause_code"] = causes
    df["rationale"] = rationales
    df = df.drop(columns=["_is_zero", "_zero_run_len"])
    return df


def _classify_row(row, plant: PlantConfig, median_spot: float) -> tuple[str, float, str, str]:
    """Returner (state, confidence, cause, rationale)."""
    elhub = row.get("mwh_elhub")
    plan = row.get("produksjonplan_mwh", np.nan)
    dq = row.get("dq_state")
    spot = row.get("spotpris_nok_mwh", np.nan)
    run_len = row.get("_zero_run_len", 0)

    # 1. Mangler data → InformationUnavailable
    if dq == "InformationUnavailable" or pd.isna(elhub):
        return UNIT_INFO_UNAVAIL, 1.0, "9.1-DataMissing", "Manglende Elhub-data"

    # 2. Negativ Elhub → Uncertain tilstand, men behandles som InformationUnavailable
    if elhub < 0:
        return UNIT_INFO_UNAVAIL, 0.6, "9.2-NegativeReading", (
            f"Negativ MWh-Elhub={elhub:.3f}, kan være regulerkraft-kjøp eller måleavvik"
        )

    # 3. Null produksjon – klassifiser etter plan og kontekst
    if elhub == 0:
        if pd.notna(plan) and plan > 0.01:
            # Plan > 0, leverte 0 → sannsynlig forced outage
            return UNIT_FORCED_OUTAGE, 0.80, "U1-UnplannedStop", (
                f"Plan={plan:.3f} men Elhub=0 (uvarslet stans)"
            )
        # Plan = 0 eller ukjent
        if plant.plant_type == "Regulated":
            if run_len >= plant.sustained_stop_hours:
                return UNIT_PLANNED_OUTAGE, 0.70, "P1-ScheduledStop", (
                    f"Elhub=0 sammenhengende {int(run_len)}h (≥ {plant.sustained_stop_hours}h), sannsynlig planlagt"
                )
            # Kortere stopp - markedsstyrt sparing av vann
            if pd.notna(spot) and pd.notna(median_spot):
                if spot < median_spot:
                    return UNIT_RESERVE_SHUTDOWN, 0.55, "M1-MarketDriven", (
                        f"Elhub=0, Plan=0, Spotpris={spot:.0f} < median {median_spot:.0f} – markedsstyrt"
                    )
                else:
                    return UNIT_FORCED_OUTAGE, 0.45, "U2-UnexpectedStop", (
                        f"Elhub=0, Plan=0, Spotpris={spot:.0f} ≥ median {median_spot:.0f} – uvanlig"
                    )
            return UNIT_RESERVE_SHUTDOWN, 0.40, "M1-MarketDriven", (
                "Elhub=0, Plan=0 – antatt markedsstyrt (lav confidence)"
            )
        # RunOfRiver – uten hydrologi: sannsynlig ressursbegrenset
        return UNIT_RESOURCE_UNAVAIL, 0.40, "8.1-WaterLimited", (
            "Elhub=0 på elvekraft – antatt ressursbegrenset (krever hydrologi for bekreftelse)"
        )

    # 4. Positiv produksjon – sammenlign mot plan
    if pd.notna(plan) and plan > 0:
        ratio = elhub / plan if plan > 0 else 1.0
        if ratio < plant.derating_threshold:
            return UNIT_FORCED_DERATING, 0.65, "D1-ForcedDerating", (
                f"Elhub={elhub:.3f} = {ratio:.0%} av Plan={plan:.3f} – redusert kapasitet"
            )
        # InService
        return UNIT_IN_SERVICE, 0.95, "0-Normal", (
            f"Elhub={elhub:.3f} ≈ Plan={plan:.3f} (ratio={ratio:.0%})"
        )

    # 5. Positiv produksjon, ingen plan – anta InService med lavere confidence
    return UNIT_IN_SERVICE, 0.80, "0-Normal", (
        f"Elhub={elhub:.3f} (ingen plan-sammenligning)"
    )


def _run_lengths(arr: np.ndarray) -> np.ndarray:
    """Returner run-length for hver posisjon i en boolean array.

    For hver indeks i: hvor lang er den sammenhengende True-sekvensen som
    indeks i tilhører (hvis arr[i] er True), ellers 0.
    """
    n = len(arr)
    out = np.zeros(n, dtype=int)
    if n == 0:
        return out
    # Scan frem
    current = 0
    for i in range(n):
        if arr[i]:
            current += 1
        else:
            current = 0
        out[i] = current
    # Utvid bakover slik at alle elementer i en run får run sin lengde
    i = n - 1
    while i >= 0:
        if out[i] > 0:
            run_end = i
            run_len = out[i]
            run_start = run_end - run_len + 1
            out[run_start:run_end + 1] = run_len
            i = run_start - 1
        else:
            i -= 1
    return out


def cluster_events(classified: pd.DataFrame) -> pd.DataFrame:
    """Agreger sammenhengende rader med samme state til hendelser."""
    if classified.empty:
        return pd.DataFrame()
    df = classified.sort_values("time_utc").reset_index(drop=True)
    state = df["state"].values
    change = np.concatenate([[True], state[1:] != state[:-1]])
    event_id = np.cumsum(change)
    df["_event_id"] = event_id
    events = df.groupby("_event_id").agg(
        state=("state", "first"),
        cause_code=("cause_code", "first"),
        confidence=("confidence", "mean"),
        start_utc=("time_utc", "min"),
        end_utc=("time_utc", "max"),
        duration_h=("time_utc", "count"),
        mwh_sum=("mwh_elhub", "sum"),
        plan_sum=("produksjonplan_mwh", "sum"),
    ).reset_index(drop=True)
    return events
