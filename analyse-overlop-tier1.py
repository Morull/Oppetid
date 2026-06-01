#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Tier 1 — overløpsanalyse for Dalane Kraft-porteføljen.

Leser de timesmidlede SCADA-master-CSV-ene og svarer på:
  Q1: Hvor mange overløp-timer + hvor mye volum per anlegg?
  Q2: For hvert overløp-event — hvor stor del kunne realistisk vært unngått
      gitt turbinens slukeevne og det vannet som faktisk lå i magasinet?

Kun lesing. Norsk tallformat (komma desimal, semikolon separator, BOM).

Kjør fra prosjektroten (C:\\Morten\\00 Oppetid):
    python analyse-overlop-tier1.py

MERK: "unngåelig" er et FYSISK øvre estimat. Det tar ikke hensyn til
minstevannføring, turbinens ramp-rate eller kraftpris — det hører til Tier 2.
"""
import glob
import os
import sys
import pandas as pd
import numpy as np

# Datamappe relativt til scriptet — fungerer både på Windows og i sandbox.
DATA_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "CSV Eksporter", "done", "2026-05")

OVERFLOW_THRESHOLD = 0.5       # m3/s — fra OverflowQueryService, filtrerer sensorstøy
PRODUCTION_THRESHOLD_KW = 1.0  # kW — over dette regnes anlegget som "i produksjon"

# Anlegg → SCADA-tags i 73-tag-masterfilen.
PLANTS = {
    "Øgreyfoss": {
        "overflow": "OGREY1_INNTAK_NIVA_OVERLOP_VF_PV",
        "prod":     ["OGREY1_G1_GEN_P_PV", "OGREY2_G2_GEN_P_PV"],
        "turb":     ["OGREY1_G1_TURB_VF_PV", "OGREY2_G2_TURB_VF_PV"],
        "volume":   "OGREY1_INNTAK_MAGASIN_VOLUM_PV",
        "inflow":   "OGREY1_INNTAK_MAGASIN_TOT_VF_PV",
    },
    "Vikeså": {
        "overflow": "VIKESA_INNTAK_NIVA_OVERLOP_VF_PV",
        "prod":     ["VIKESA_G1_GEN_P_PV"],
        "turb":     ["VIKESA_G1_TURB_VF_PV"],
        "volume":   "VIKESA_INNTAK_MAGASIN_VOLUM_PV",
        "inflow":   None,
    },
    "Løgjen": {
        "overflow": "LOGJEN_INNTAK_NIVA_OVERLOP_VF_PV",
        "prod":     ["LOGJEN_G1_GEN_P_PV"],
        "turb":     ["LOGJEN_G1_TURB_VF_PV"],
        "volume":   "LOGJEN_INNTAK_MAGASIN_VOLUM_PV",
        "inflow":   "LOGJEN_INNTAK_MAGASIN_TOT_VF_PV",
    },
}


def load_master():
    """Last + slå sammen alle 73-tag-masterfiler. Dedupliserer på DateTime."""
    files = sorted(glob.glob(os.path.join(DATA_DIR, "*73-tags*MASTER.csv")))
    if not files:
        sys.exit(f"Fant ingen 73-tag-masterfiler i:\n  {DATA_DIR}")
    df = pd.concat([pd.read_csv(f, sep=";", decimal=",", encoding="utf-8-sig")
                    for f in files], ignore_index=True)
    df["DateTime"] = pd.to_datetime(df["DateTime"])
    df = df.drop_duplicates(subset="DateTime").sort_values("DateTime").set_index("DateTime")
    return df, files


def col(df, tag):
    """Hent 'Value (Cluster1.TAG)'-kolonnen som numerisk serie."""
    return pd.to_numeric(df[f"Value (Cluster1.{tag})"], errors="coerce")


def find_events(mask):
    """Grupper sammenhengende True-timer til (start, slutt)-events."""
    events, start = [], None
    idx, vals = list(mask.index), list(mask.values)
    for i, v in enumerate(vals):
        if v and start is None:
            start = i
        elif not v and start is not None:
            events.append((idx[start], idx[i - 1]))
            start = None
    if start is not None:
        events.append((idx[start], idx[-1]))
    return events


def analyze():
    df, files = load_master()
    print("=" * 80)
    print("TIER 1 — OVERLØPSANALYSE")
    print("=" * 80)
    print(f"Filer:    {len(files)}")
    print(f"Tidsrom:  {df.index.min()}  →  {df.index.max()}")
    print(f"Timer:    {len(df)}  ({len(df) / 24:.0f} døgn)")
    print(f"Terskel:  {OVERFLOW_THRESHOLD} m3/s (filtrerer sensorstøy)")

    summary = []
    for plant, tags in PLANTS.items():
        print("\n" + "=" * 80)
        print(f"  {plant.upper()}")
        print("=" * 80)

        overflow = col(df, tags["overflow"]).fillna(0.0)
        prod = sum(col(df, t).fillna(0.0) for t in tags["prod"])   # kW
        turb = sum(col(df, t).fillna(0.0) for t in tags["turb"])   # m3/s

        # Empirisk slukeevne = 99-persentil av turbinflyt mens anlegget produserer.
        twp = turb[prod > PRODUCTION_THRESHOLD_KW]
        slukeevne = float(np.percentile(twp, 99)) if len(twp) > 20 else float(turb.max())

        # Empirisk spesifikk energi (kWh/m3) fra produksjonstimer.
        ph, th = prod[prod > PRODUCTION_THRESHOLD_KW], turb[prod > PRODUCTION_THRESHOLD_KW]
        valid = th > 0.1
        spec = float((ph[valid] / th[valid]).median() / 3600.0) if valid.sum() > 20 else float("nan")

        # Q1 — overløp-timer + volum
        of_mask = overflow > OVERFLOW_THRESHOLD
        of_hours = int(of_mask.sum())
        of_volume = float(overflow[of_mask].sum() * 3600.0)        # m3
        lost_mwh = of_volume * spec / 1000.0

        print(f"Q1  Overløp-timer:        {of_hours} av {len(df)} "
              f"({100 * of_hours / len(df):.1f} %)")
        print(f"    Overløp-volum:        {of_volume:,.0f} m3  ({of_volume / 1e6:.3f} Mill.m3)")
        print(f"    Slukeevne (99-pst):   {slukeevne:.2f} m3/s")
        if not np.isnan(lost_mwh):
            print(f"    Est. tapt produksjon: {lost_mwh:,.0f} MWh  (volum × spes.energi, ikke prisvektet)")

        # Magasingulv = 5-persentil av observert volum (praktisk nedre nivå).
        vol_series = col(df, tags["volume"]).dropna() if tags["volume"] else None
        floor = float(np.percentile(vol_series, 5)) if vol_series is not None else float("nan")

        # Q2 — events med realistisk unngåelig-estimat
        events = find_events(of_mask)
        print(f"Q2  Antall overløp-events: {len(events)}")
        if events:
            print()
            print(f"    {'#':>2}  {'start':<16} {'varigh':>7} {'volum m3':>11} "
                  f"{'turbin-hr':>11} {'magasin-kr':>11} {'unngåelig':>10}  vurdering")
            print("    " + "-" * 86)

        classed = {"hadde ikke hjulpet": 0, "delvis": 0, "kan unngås": 0}
        tot_avoid = 0.0
        for n, (s, e) in enumerate(events, 1):
            dur = int((e - s).total_seconds() / 3600) + 1
            ev = df.loc[s:e]
            ev_vol = float(overflow.loc[s:e].sum() * 3600.0)

            # Turbin-headroom UNDER eventet (ekstra vann turbinen kunne tatt).
            ev_turb = sum(col(ev, t).fillna(0.0) for t in tags["turb"])
            turb_hr = float((slukeevne - ev_turb).clip(lower=0).sum() * 3600.0)

            # Magasin-kreditt = vann som FAKTISK lå i magasinet 24t før eventet
            # og som kunne vært forhåndstappet (volum minus praktisk gulv).
            if vol_series is not None:
                v24 = vol_series.asof(s - pd.Timedelta(hours=24))
                v24 = float(v24) if pd.notna(v24) else floor
                mag_credit = max(0.0, v24 - floor) * 1e6
            else:
                mag_credit = 0.0

            avoidable = min(ev_vol, turb_hr + mag_credit)
            ratio = avoidable / ev_vol if ev_vol > 0 else 0.0
            tot_avoid += avoidable

            if ratio >= 0.90:
                verdict = "kan unngås"; classed["kan unngås"] += 1
            elif ratio >= 0.25:
                verdict = "delvis"; classed["delvis"] += 1
            else:
                verdict = "hadde ikke hjulpet"; classed["hadde ikke hjulpet"] += 1

            print(f"    {n:>2}  {str(s)[:16]:<16} {dur:>5} t {ev_vol:>11,.0f} "
                  f"{turb_hr:>11,.0f} {mag_credit:>11,.0f} {avoidable:>10,.0f}  {verdict}")

        if events:
            print()
            for k, v in classed.items():
                print(f"      {k:<22}: {v}")
            avp = 100 * tot_avoid / of_volume if of_volume > 0 else 0.0
            print(f"      → realistisk unngåelig: {tot_avoid / 1e6:.2f} Mill.m3 av "
                  f"{of_volume / 1e6:.2f} ({avp:.0f} %)")

        summary.append(dict(plant=plant, of_hours=of_hours, of_volume=of_volume,
                            of_events=len(events), lost_mwh=lost_mwh,
                            avoidable=tot_avoid))

    # Porteføljeoppsummering
    print("\n" + "=" * 80)
    print("  PORTEFØLJE-OPPSUMMERING")
    print("=" * 80)
    print(f"{'Anlegg':<12}{'OF-timer':>10}{'OF-volum Mm3':>15}{'Events':>8}"
          f"{'Tapt MWh':>11}{'Unngåelig':>12}")
    print("-" * 80)
    for s in summary:
        mwh = f"{s['lost_mwh']:,.0f}" if not np.isnan(s["lost_mwh"]) else "n/a"
        avp = 100 * s["avoidable"] / s["of_volume"] if s["of_volume"] > 0 else 0.0
        print(f"{s['plant']:<12}{s['of_hours']:>10}{s['of_volume'] / 1e6:>15.3f}"
              f"{s['of_events']:>8}{mwh:>11}{avp:>11.0f}%")
    print()
    print("MERK: 'unngåelig' = turbin-headroom under eventet + vann som faktisk lå")
    print("i magasinet før eventet. Fysisk øvre estimat — Tier 2 trekker fra")
    print("minstevannføring, ramp-rate og pris.")


if __name__ == "__main__":
    analyze()
