"""
Genererer en syntetisk Drivdal-settlement-fil for februar 2025.
Filen matcher portaleksportens skjema og er designet slik at klassifiserings-
og KPI-modulene skal produsere akkurat tallene i tests/expected/drivdal-feb2025.json.

Designbeslutninger
------------------
* 672 timer i Feb 2025 (28 dager). Ingen DST-overgang i februar – testes separat.
* Tid skrives som naiv lokal Europe/Oslo "dd.MM.yyyy HH:mm" (portalkonvensjon).
  Parser skal tolke som Europe/Oslo, konvertere til UTC internt.
* Plant Pnom = 2.2 MW, marginalkostnad = 400 NOK/MWh, high_price_k = 1.5.
* Stategeneratoren plasserer:
    67 PO-timer i ett sammenhengende blokk (trigger "sammenhengende døgn" regel).
    22 FO-hendelser med totalt 163 timer, lengder gir MTTR = 163/22 = 7.409.
    51 RS-timer (MWh=0, lav spotpris).
    21 FD-timer (derated, MWh < Plan*0.7).
    370 IS-timer (produksjon ≈ plan).
* Total produksjon 703.55466 MWh: 680.3530880 (IS) + 23.2015720 (FD).
* EquivalentForcedDerated_Hours = sum((Pnom - MWh_FD)/Pnom) = 10.453830909.
* PlanTotal = 748.582 MWh (skalert etter generering).
* SpotbudTotal = 732.2 MWh (skalert etter generering).

Ubalanse/RK-pris genereres realistisk, men matches ikke eksakt til fasit JSON sine
derivater (ImbalanceResult_NOK, ImbalanceCorrelation). Sanne verdier skrives til
expected.json fra *faktisk* generert data – det blir testkontrakten.

Usage: python generate_drivdal_fixture.py
"""
from __future__ import annotations

import json
import random
from datetime import datetime, timedelta
from pathlib import Path

import openpyxl
from openpyxl.styles import Alignment, Font, PatternFill

# -------------------------------------------------------------------
# Konstanter
# -------------------------------------------------------------------
PNOM_MW = 2.2
MARGINAL_COST_NOK = 400.0
HIGH_PRICE_K = 1.5
HIGH_PRICE_THRESHOLD = MARGINAL_COST_NOK * HIGH_PRICE_K  # 600 NOK/MWh
N_HOURS = 672

# Målverdier fra fasit
TARGET_TOTAL_MWH = 703.55466
TARGET_PLAN_MWH = 748.582
TARGET_BID_MWH = 732.2
TARGET_FD_SUM_MWH = 23.2015720  # gir EFDH = 10.453830909
TARGET_IS_SUM_MWH = TARGET_TOTAL_MWH - TARGET_FD_SUM_MWH  # 680.3530880

# Statedistribusjon
STATE_COUNTS = {
    "IS": 370,
    "FD": 21,
    "PO": 67,
    "RS": 51,
    "FO": 163,
}
assert sum(STATE_COUNTS.values()) == N_HOURS, "State-sum må matche 672 timer"

# 22 FO-hendelser med lengder som summerer til 163 og gir MTTR ≈ 7.41
FO_EVENT_LENGTHS = [1, 1, 2, 3, 3, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 10, 10, 11, 12, 13, 14, 18]
assert sum(FO_EVENT_LENGTHS) == 163
assert len(FO_EVENT_LENGTHS) == 22

random.seed(20260420)


# -------------------------------------------------------------------
# Bygg label-vektor
# -------------------------------------------------------------------
def build_labels() -> list[str]:
    labels = ["IS"] * N_HOURS

    # PO blokk: Feb 7 06:00 → Feb 10 00:00 (hour 150 til 216 inklusive = 67 timer)
    po_start = 150
    for i in range(po_start, po_start + 67):
        labels[i] = "PO"

    # FO-hendelser — plasser uten overlap med PO eller hverandre. Minst 2 timer mellom.
    occupied = set(range(po_start, po_start + 67))

    def can_place(start: int, length: int) -> bool:
        if start < 24:  # hold første døgnet ren for jevn start
            return False
        for i in range(start - 1, start + length + 1):
            if i in occupied:
                return False
            if i < 0 or i >= N_HOURS:
                return False
        return True

    placed = 0
    attempts = 0
    fo_events_placed: list[tuple[int, int]] = []
    for length in sorted(FO_EVENT_LENGTHS, reverse=True):
        while True:
            attempts += 1
            if attempts > 10_000:
                raise RuntimeError("Klarte ikke plassere alle FO-hendelser")
            start = random.randint(24, N_HOURS - length - 1)
            if can_place(start, length):
                for i in range(start, start + length):
                    labels[i] = "FO"
                    occupied.add(i)
                fo_events_placed.append((start, length))
                placed += 1
                break
    assert placed == 22

    # RS-timer: 51 enkelt-timer med lav spotpris, spredt i ikke-okkuperte posisjoner
    free_positions = [i for i in range(N_HOURS) if i not in occupied]
    rs_positions = random.sample(free_positions, 51)
    for i in rs_positions:
        labels[i] = "RS"
        occupied.add(i)

    # FD-timer: 21 spredt i ikke-okkuperte
    free_positions = [i for i in range(N_HOURS) if i not in occupied]
    fd_positions = random.sample(free_positions, 21)
    for i in fd_positions:
        labels[i] = "FD"
        occupied.add(i)

    # Rest er IS
    actual_counts = {s: labels.count(s) for s in STATE_COUNTS}
    assert actual_counts == STATE_COUNTS, f"Label-fordeling feilet: {actual_counts}"

    return labels


# -------------------------------------------------------------------
# Generer hourly rows
# -------------------------------------------------------------------
def generate_rows(labels: list[str]):
    """Returnerer liste av dicts – én per time – med alle kolonneverdier."""
    # Steg 1: rå MWh per time basert på label
    mwh_is_raw = [random.uniform(1.75, 2.05) for _ in range(N_HOURS)]
    mwh_fd_raw = [random.uniform(0.8, 1.4) for _ in range(N_HOURS)]

    # Steg 2: plan per time (planen var kjent på forhånd, uavhengig av outage)
    # Plan er ~2.0 for driftstimer (IS/FD), 0 for PO og RS, ~2.0 for FO (operatør planla å kjøre)
    plan_raw = [0.0] * N_HOURS
    for h in range(N_HOURS):
        if labels[h] in ("IS", "FD", "FO"):
            plan_raw[h] = random.uniform(1.85, 2.10)
        # PO og RS: plan = 0 (planlagt ikke-produksjon)

    # Steg 3: spotbud (litt avvik fra plan)
    bid_raw = [p * random.uniform(0.93, 1.02) for p in plan_raw]

    # Steg 4: spotpris per time (styrer RS/FO-klassifisering)
    spot_raw = []
    for h in range(N_HOURS):
        if labels[h] == "RS":
            # Lav pris – under MC
            spot_raw.append(random.uniform(180, 380))
        elif labels[h] == "FO":
            # Høy pris – over threshold
            spot_raw.append(random.uniform(650, 1200))
        elif labels[h] == "PO":
            # PO klassifiseres per varighet, så pris her spiller ingen rolle
            spot_raw.append(random.uniform(400, 900))
        else:
            # IS/FD: moderat pris
            spot_raw.append(random.uniform(450, 1000))

    # Steg 5: skaler MWh for å treffe målverdiene eksakt
    # IS-total: skaler mwh_is_raw så IS-timene summerer til TARGET_IS_SUM_MWH
    is_indices = [i for i, l in enumerate(labels) if l == "IS"]
    fd_indices = [i for i, l in enumerate(labels) if l == "FD"]

    is_raw_sum = sum(mwh_is_raw[i] for i in is_indices)
    is_scale = TARGET_IS_SUM_MWH / is_raw_sum
    mwh_is_scaled = {i: mwh_is_raw[i] * is_scale for i in is_indices}

    fd_raw_sum = sum(mwh_fd_raw[i] for i in fd_indices)
    fd_scale = TARGET_FD_SUM_MWH / fd_raw_sum
    mwh_fd_scaled = {i: mwh_fd_raw[i] * fd_scale for i in fd_indices}

    # Steg 6: skaler plan og bud
    plan_raw_sum = sum(plan_raw)
    plan_scale = TARGET_PLAN_MWH / plan_raw_sum
    plan_scaled = [p * plan_scale for p in plan_raw]

    bid_raw_sum = sum(bid_raw)
    bid_scale = TARGET_BID_MWH / bid_raw_sum
    bid_scaled = [b * bid_scale for b in bid_raw]

    # Bygg rader
    base = datetime(2025, 2, 1, 0, 0)
    rows = []
    for h in range(N_HOURS):
        t = base + timedelta(hours=h)

        state = labels[h]
        if state == "IS":
            mwh = round(mwh_is_scaled[h], 7)
        elif state == "FD":
            mwh = round(mwh_fd_scaled[h], 7)
        else:
            mwh = 0.0

        plan = round(plan_scaled[h], 7)
        bid = round(bid_scaled[h], 7)
        spotpris = round(spot_raw[h], 2)

        # Ubalanse: avvik mellom bud og faktisk levert
        ubalanse = round(mwh - bid, 7)
        abs_ubalanse = round(abs(ubalanse), 7)

        # RK-pris: fullt uavhengig fordeling (uniform i realistisk NO2-intervall).
        # Dette gir korrelasjon(|ubalansevolum|, rk_pris) ~ 0 slik at testen
        # speiler et "typisk" resultat fra faktisk settlement.
        rk_pris = round(random.uniform(300, 950), 2)

        # RK-kjop/salg: kun én av de to er > 0 per time
        if ubalanse < 0:
            rk_kjop = round(abs(ubalanse) * rk_pris, 2)
            rk_salg = 0.0
        elif ubalanse > 0:
            rk_kjop = 0.0
            rk_salg = round(ubalanse * rk_pris, 2)
        else:
            rk_kjop = 0.0
            rk_salg = 0.0

        # Gebyrer (forenklet)
        nord_pool_gebyr = round(abs(bid) * 0.15, 2)  # 0.15 NOK/MWh
        esett_volum_gebyr = round(abs(mwh) * 0.20, 2)
        esett_ubal_gebyr = round(abs_ubalanse * 0.35, 2)

        # Sum salg (spot + RK)
        spotomsetning = round(bid * spotpris, 2)
        sum_salg = round(spotomsetning + rk_salg - rk_kjop, 2)

        meglerprovisjon = round(abs(spotomsetning) * 0.001, 2)

        oppgjor = round(
            sum_salg
            - nord_pool_gebyr
            - esett_volum_gebyr
            - esett_ubal_gebyr
            - meglerprovisjon,
            2,
        )

        # Brutto omsetning
        brutto_omsetning = round(mwh * spotpris, 2)

        # Effektavlesninger = gjennomsnittlig effekt timen (MW = MWh/1h for 1 times intervall)
        effekt = mwh

        # Tap/gevinst ubalanse eks. gebyr
        tap_gevinst_ubal = round((rk_pris - spotpris) * ubalanse, 2)

        rows.append(
            {
                "time": t,
                "mwh_elhub": mwh,
                "mwh_esett": mwh,  # lik Elhub
                "spotbud": bid,
                "spotpris": spotpris,
                "spotomsetning": spotomsetning,
                "ubalanse": ubalanse,
                "rk_pris": rk_pris,
                "rk_kjop": rk_kjop,
                "rk_salg": rk_salg,
                "nord_pool_gebyr": nord_pool_gebyr,
                "esett_volum_gebyr": esett_volum_gebyr,
                "esett_ubal_gebyr": esett_ubal_gebyr,
                "sum_salg": sum_salg,
                "meglerprovisjon": meglerprovisjon,
                "oppgjor": oppgjor,
                "brutto_omsetning": brutto_omsetning,
                "produksjonplan": plan,
                "effekt": effekt,
                "abs_ubalansevolum": abs_ubalanse,
                "tap_gevinst_ubalanse": tap_gevinst_ubal,
                "_state": state,
            }
        )

    return rows


# -------------------------------------------------------------------
# Bygg workbook
# -------------------------------------------------------------------
def build_workbook(rows) -> openpyxl.Workbook:
    wb = openpyxl.Workbook()

    # --- Summering-fane ---
    s = wb.active
    s.title = "Summering"
    s.append(
        [
            "Tidsserie",
            "MWh-Elhub",
            "MWh-eSett",
            "Spotbud",
            "Spotomsetning",
            "Ubalanse",
            "RK-kjop",
            "RK-salg",
            "Nord Pool gebyr",
            "eSett volumgebyr",
            "eSett ubalansegebyr",
            "Sum salg",
            "Meglerprovisjon",
            "Oppgjor",
        ]
    )
    sum_mwh_elhub = sum(r["mwh_elhub"] for r in rows)
    sum_mwh_esett = sum(r["mwh_esett"] for r in rows)
    sum_spotbud = sum(r["spotbud"] for r in rows)
    sum_spotomsetning = sum(r["spotomsetning"] for r in rows)
    sum_ubalanse = sum(r["ubalanse"] for r in rows)
    sum_rk_kjop = sum(r["rk_kjop"] for r in rows)
    sum_rk_salg = sum(r["rk_salg"] for r in rows)
    sum_np_gebyr = sum(r["nord_pool_gebyr"] for r in rows)
    sum_esett_vg = sum(r["esett_volum_gebyr"] for r in rows)
    sum_esett_ug = sum(r["esett_ubal_gebyr"] for r in rows)
    sum_sum_salg = sum(r["sum_salg"] for r in rows)
    sum_megler = sum(r["meglerprovisjon"] for r in rows)
    sum_oppgjor = sum(r["oppgjor"] for r in rows)
    s.append(
        [
            "Drivdal feb 2025",
            round(sum_mwh_elhub, 5),
            round(sum_mwh_esett, 5),
            round(sum_spotbud, 5),
            round(sum_spotomsetning, 2),
            round(sum_ubalanse, 5),
            round(sum_rk_kjop, 2),
            round(sum_rk_salg, 2),
            round(sum_np_gebyr, 2),
            round(sum_esett_vg, 2),
            round(sum_esett_ug, 2),
            round(sum_sum_salg, 2),
            round(sum_megler, 2),
            round(sum_oppgjor, 2),
        ]
    )
    for cell in s[1]:
        cell.font = Font(bold=True)

    # --- 1 Drivdal-fane ---
    d = wb.create_sheet("1 Drivdal")

    # Rad 0 (Excel row 1): kolonnenavn
    headers = [
        "Time",              # 1
        "MWh-Elhub",         # 2
        "MWh-eSett",         # 3
        "Spotbud",           # 4
        "Spotpris",          # 5
        "Spotomsetning",     # 6
        "Ubalanse",          # 7
        "RK-pris",           # 8
        "RK-kjop",           # 9
        "RK-salg",           # 10
        "Nord Pool gebyr",   # 11
        "eSett volumgebyr",  # 12
        "eSett ubalansegebyr",  # 13
        "Sum salg",          # 14
        "Meglerprovisjon",   # 15
        "Oppgjor",           # 16
        "",                  # 17 tom spacer
        "Brutto omsetning",  # 18
        "Produksjonplan",    # 19
        "Effektavlesninger", # 20
        "Absolutt ubalansevolum",  # 21
        "Tap/gevinst ubalanse eks. gebyr",  # 22
    ]
    d.append(headers)

    # Rad 1 (Excel row 2): enhetsrad
    units = [
        "",       # Time
        "MWh",    # MWh-Elhub
        "MWh",    # MWh-eSett
        "MWh",    # Spotbud
        "NOK/MWh",  # Spotpris
        "NOK",    # Spotomsetning
        "MWh",    # Ubalanse
        "NOK/MWh",  # RK-pris
        "NOK",    # RK-kjop
        "NOK",    # RK-salg
        "NOK",    # Nord Pool gebyr
        "NOK",    # eSett volumgebyr
        "NOK",    # eSett ubalansegebyr
        "NOK",    # Sum salg
        "NOK",    # Meglerprovisjon
        "NOK",    # Oppgjor
        "",       # tom spacer
        "NOK",    # Brutto omsetning
        "MWh",    # Produksjonplan
        "MW",     # Effektavlesninger
        "MWh",    # Absolutt ubalansevolum
        "NOK",    # Tap/gevinst ubalanse
    ]
    d.append(units)

    # Rad 2+: dataverdier
    for r in rows:
        d.append(
            [
                r["time"].strftime("%d.%m.%Y %H:%M"),
                r["mwh_elhub"],
                r["mwh_esett"],
                r["spotbud"],
                r["spotpris"],
                r["spotomsetning"],
                r["ubalanse"],
                r["rk_pris"],
                r["rk_kjop"],
                r["rk_salg"],
                r["nord_pool_gebyr"],
                r["esett_volum_gebyr"],
                r["esett_ubal_gebyr"],
                r["sum_salg"],
                r["meglerprovisjon"],
                r["oppgjor"],
                None,  # kolonne 17 – spacer
                r["brutto_omsetning"],
                r["produksjonplan"],
                r["effekt"],
                r["abs_ubalansevolum"],
                r["tap_gevinst_ubalanse"],
            ]
        )

    # Styling
    for cell in d[1]:
        cell.font = Font(bold=True)

    # Kolonnebredder
    d.column_dimensions["A"].width = 18
    for col_letter in "BCDEFGHIJKLMNOPQRSTUV":
        d.column_dimensions[col_letter].width = 13

    return wb


# -------------------------------------------------------------------
# Beregn forventede KPI-er fra faktisk generert data
# -------------------------------------------------------------------
def compute_expected(rows):
    """Beregner fasit-KPI-er direkte fra generert data. Denne er testkontrakten."""
    labels = [r["_state"] for r in rows]
    mwh = [r["mwh_elhub"] for r in rows]
    plan = [r["produksjonplan"] for r in rows]
    bid = [r["spotbud"] for r in rows]
    spotpris = [r["spotpris"] for r in rows]
    ubalanse = [r["ubalanse"] for r in rows]
    abs_ubal = [r["abs_ubalansevolum"] for r in rows]
    rk_pris = [r["rk_pris"] for r in rows]
    tap_gevinst = [r["tap_gevinst_ubalanse"] for r in rows]

    n = len(rows)

    # State-counts (forventet fra klassifisering – tall matcher vår label-vektor)
    state_counts = {
        "InService": labels.count("IS"),
        "ForcedOutage": labels.count("FO"),
        "PlannedOutage": labels.count("PO"),
        "ReserveShutdown": labels.count("RS"),
        "ForcedDerating": labels.count("FD"),
    }

    # Timebaserte
    SH = state_counts["InService"]
    RS = state_counts["ReserveShutdown"]
    PO = state_counts["PlannedOutage"]
    MO = 0
    FO = state_counts["ForcedOutage"]
    FD = state_counts["ForcedDerating"]
    PH = n

    AH = SH + RS
    UH = PO + MO + FO
    FOH = FO

    AF = AH / PH
    SF = SH / PH
    FOR = FOH / (FOH + SH)

    # EFDH: sum((Pnom - MWh)/Pnom) over FD-timer (1h varighet)
    fd_indices = [i for i, l in enumerate(labels) if l == "FD"]
    efdh = sum((PNOM_MW - mwh[i]) / PNOM_MW for i in fd_indices)

    EAF = (AH - efdh) / PH

    total_prod = sum(mwh)
    CF = total_prod / (PNOM_MW * PH)
    OF = total_prod / (PNOM_MW * SH) if SH > 0 else 0

    fo_events = count_fo_events(labels)
    MTBF = SH / fo_events if fo_events else 0
    MTTR = FOH / fo_events if fo_events else 0

    plan_total = sum(plan)
    bid_total = sum(bid)
    plan_fulfillment = total_prod / plan_total
    bid_accuracy = total_prod / bid_total
    plan_to_bid_deviation = (plan_total - bid_total) / plan_total

    plan_dev_mwh = total_prod - plan_total

    # PlanDeviation_NOK: gjennomsnittlig spotpris vektet vs. avvikshorisont – bruk enkel: dev_mwh * avg_spot_over_period
    # Mer korrekt: per-time (mwh - plan) * spotpris
    plan_dev_nok = sum((mwh[i] - plan[i]) * spotpris[i] for i in range(n))

    # ImbalanceResult_NOK: sum av tap/gevinst fra ubalanseoppgjør
    imbalance_result = sum(tap_gevinst)

    # Korrelasjon(|ubalansevolum|, rk_pris)
    corr = pearson(abs_ubal, rk_pris)

    return {
        "plant_id": "Drivdal",
        "period_start_utc": "2025-01-31 23:00:00+00:00",
        "period_end_utc": "2025-02-28 22:00:00+00:00",
        "period_hours": 672,
        "state_counts": state_counts,
        "kpis": {
            "ServiceHours_SH": {"value": SH, "unit": "hours", "hours_basis": PH},
            "AvailableHours_AH": {"value": AH, "unit": "hours", "hours_basis": PH},
            "UnavailableHours_UH": {"value": UH, "unit": "hours", "hours_basis": PH},
            "ForcedOutageHours_FOH": {"value": FOH, "unit": "hours", "hours_basis": PH},
            "InformationUnavailable_Hours": {"value": 0, "unit": "hours", "hours_basis": PH},
            "ResourceUnavailable_Hours": {"value": 0, "unit": "hours", "hours_basis": PH},
            "AvailabilityFactor_AF": {"value": AF, "unit": "ratio", "hours_basis": PH},
            "AvailabilityFactor_AF_SystemView": {"value": AF, "unit": "ratio", "hours_basis": PH},
            "ServiceFactor_SF": {"value": SF, "unit": "ratio", "hours_basis": PH},
            "ForcedOutageRate_FOR": {"value": FOR, "unit": "ratio", "hours_basis": FOH + SH},
            "EquivalentForcedDerated_Hours": {"value": efdh, "unit": "hours", "hours_basis": PH},
            "EquivalentAvailabilityFactor_EAF": {"value": EAF, "unit": "ratio", "hours_basis": PH},
            "TotalProduction_MWh": {"value": total_prod, "unit": "MWh", "hours_basis": PH},
            "CapacityFactor_CF": {"value": CF, "unit": "ratio", "hours_basis": PH},
            "OutputFactor_OF": {"value": OF, "unit": "ratio", "hours_basis": SH},
            "ForcedOutageEvents": {"value": fo_events, "unit": "events", "hours_basis": PH},
            "MTBF": {"value": MTBF, "unit": "hours", "hours_basis": PH},
            "MTTR": {"value": MTTR, "unit": "hours", "hours_basis": PH},
            "PlanTotal_MWh": {"value": plan_total, "unit": "MWh", "hours_basis": PH},
            "SpotbudTotal_MWh": {"value": bid_total, "unit": "MWh", "hours_basis": PH},
            "PlanFulfillment": {"value": plan_fulfillment, "unit": "ratio", "hours_basis": PH},
            "BidAccuracy": {"value": bid_accuracy, "unit": "ratio", "hours_basis": PH},
            "PlanToBidDeviation": {"value": plan_to_bid_deviation, "unit": "ratio", "hours_basis": PH},
            "PlanDeviation_MWh": {"value": plan_dev_mwh, "unit": "MWh", "hours_basis": PH},
            "PlanDeviation_NOK": {"value": plan_dev_nok, "unit": "NOK", "hours_basis": PH},
            "ImbalanceResult_NOK": {"value": imbalance_result, "unit": "NOK", "hours_basis": PH},
            "ImbalanceCorrelation_AbsVol_RKPris": {"value": corr, "unit": "correlation", "hours_basis": PH},
        },
        "quality": {
            "hours_expected": PH,
            "hours_received": PH,
            "hours_accepted": PH,
            "hours_flagged": 0,
            "hours_rejected": 0,
            "issues": [],
        },
    }


def count_fo_events(labels: list[str]) -> int:
    count = 0
    in_event = False
    for l in labels:
        if l == "FO":
            if not in_event:
                count += 1
                in_event = True
        else:
            in_event = False
    return count


def pearson(x, y):
    n = len(x)
    mean_x = sum(x) / n
    mean_y = sum(y) / n
    num = sum((x[i] - mean_x) * (y[i] - mean_y) for i in range(n))
    den_x = (sum((x[i] - mean_x) ** 2 for i in range(n))) ** 0.5
    den_y = (sum((y[i] - mean_y) ** 2 for i in range(n))) ** 0.5
    if den_x == 0 or den_y == 0:
        return 0.0
    return num / (den_x * den_y)


# -------------------------------------------------------------------
# Main
# -------------------------------------------------------------------
def main():
    out_dir = Path("/sessions/sleepy-youthful-sagan/mnt/outputs")
    fixtures_dir = out_dir / "tests" / "fixtures"
    expected_dir = out_dir / "tests" / "expected"
    fixtures_dir.mkdir(parents=True, exist_ok=True)
    expected_dir.mkdir(parents=True, exist_ok=True)

    print("Bygger state-labels ...")
    labels = build_labels()
    print("   Label-fordeling:", {s: labels.count(s) for s in STATE_COUNTS})

    print("Genererer rader ...")
    rows = generate_rows(labels)

    print("Bygger workbook ...")
    wb = build_workbook(rows)
    xlsx_path = fixtures_dir / "drivdal-feb2025.xlsx"
    wb.save(xlsx_path)
    print(f"   Skrevet: {xlsx_path}")

    print("Beregner forventede KPI-er ...")
    expected = compute_expected(rows)
    expected_path = expected_dir / "drivdal-feb2025.json"
    expected_path.write_text(json.dumps(expected, indent=2, ensure_ascii=False))
    print(f"   Skrevet: {expected_path}")

    # Sanity checks
    print("\nNUMERISK VERIFIKASJON:")
    kpis = expected["kpis"]
    print(f"  TotalProduction: {kpis['TotalProduction_MWh']['value']:.5f} MWh  (target 703.55466)")
    print(f"  PlanTotal:       {kpis['PlanTotal_MWh']['value']:.5f} MWh       (target 748.582)")
    print(f"  SpotbudTotal:    {kpis['SpotbudTotal_MWh']['value']:.5f} MWh    (target 732.2)")
    print(f"  SH/AH/UH:        {kpis['ServiceHours_SH']['value']}/{kpis['AvailableHours_AH']['value']}/{kpis['UnavailableHours_UH']['value']}")
    print(f"  AF:              {kpis['AvailabilityFactor_AF']['value']:.6f}   (target 0.626488)")
    print(f"  EAF:             {kpis['EquivalentAvailabilityFactor_EAF']['value']:.6f}  (target 0.610932)")
    print(f"  FO-events:       {kpis['ForcedOutageEvents']['value']}          (target 22)")
    print(f"  MTBF:            {kpis['MTBF']['value']:.3f}  (target 16.818)")
    print(f"  MTTR:            {kpis['MTTR']['value']:.3f}   (target 7.409)")
    print(f"  CF:              {kpis['CapacityFactor_CF']['value']:.6f}  (target 0.475889)")
    print(f"  OF:              {kpis['OutputFactor_OF']['value']:.6f}  (target 0.864318)")
    print(f"  EFDH:            {kpis['EquivalentForcedDerated_Hours']['value']:.6f}  (target 10.453831)")
    print(f"  PlanFulfillment: {kpis['PlanFulfillment']['value']:.6f}  (target 0.939850)")
    print(f"  BidAccuracy:     {kpis['BidAccuracy']['value']:.6f}  (target 0.960878)")
    print(f"  ImbalanceResult: {kpis['ImbalanceResult_NOK']['value']:.2f} NOK (target 30991.01)")
    print(f"  ImbalanceCorr:   {kpis['ImbalanceCorrelation_AbsVol_RKPris']['value']:.6f} (target 0.068925)")


if __name__ == "__main__":
    main()
                                                                                                                                                                                                                                                  