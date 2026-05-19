namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Whitelist over de 72 SCADA-tags som finnes i master-CSV-eksporten
/// (export-73-tags-avg-hour-20260518-160921_MASTER.csv) for 5 anlegg
/// uten egen dedikert seeder: Vikeså, Stølskraft, Ørsdalen, Øgreyfoss
/// og Løgjen. Den 73. tagen (DRIVDAL_NETT_LINJE_FASE_L3_U_L3_N_PV) ligger
/// i <see cref="DrivdalSignalMapSeeder"/> sammen med resten av Drivdal-tagene.
///
/// SCADA-prefiks → plant_id mapping:
///   VIKESA_     → vikesa
///   STOLSKRAFT_ → stolskraft
///   ORSDAL_     → orsdalen   (forkortet SCADA-prefiks, ikke 1:1 med slug)
///   OGREY1_     → ogreyfoss  (G1-generator-side, har INNTAK-tags)
///   OGREY2_     → ogreyfoss  (G2-generator-side, deler INNTAK med OGREY1)
///   LOGJEN_     → logjen
///
/// Disse anleggene har én default terminal-dam (<c>{plantId}_main</c>) som
/// opprettes av <see cref="DefaultDamSeeder"/>. INNTAK-tags festes til
/// terminal-dammen; generator/KRST/NETT-tags har <c>damId = null</c>.
///
/// Kjente dekningshull (rapportert til drifts-leder 2026-05-03):
///   - Stølskraft: kun 3 tags (G1_GEN_TURTALL, G1_GEN_P, G1_TURB_VF) —
///     ingen INNTAK, ingen alarm, ingen overflow. Vakt-ROI og
///     SCADA-basert nedetidsdeteksjon ikke mulig før flere tags eksporteres.
///   - Ørsdalen: 5 tags — mangler G1_TURB_VF (virkningsgrad-beregning
///     krever den) og overflow-tag (Vakt-ROI utilgjengelig).
///   - Vikeså, Løgjen, Øgreyfoss: har overflow-tags → Vakt-ROI fungerer.
///
/// Idempotent — fremtidige eksporter med nye tags blir upserted via
/// SCADA-importen, ikke seeder.
/// </summary>
public static class DalanePortfolio72TagCatalog
{
    public static readonly string[] Tags =
    [
        // ───────── VIKESÅ (17 tags) ─────────
        // Har overflow → Vakt-ROI fungerer for dette anlegget.
        "VIKESA_G1_GEN_COSPHI_PV",
        "VIKESA_G1_GEN_F_PV",
        "VIKESA_G1_GEN_P_PV",
        "VIKESA_G1_GEN_TURTALL_PV",
        "VIKESA_G1_HYDRL_OLJE_TRYKK_PV",
        "VIKESA_G1_RORGATE_VANN_TRYKK_PV",
        "VIKESA_G1_TURB_LEDEAPP_POS_PV",
        "VIKESA_G1_TURB_VANN_TRYKK_PV",
        "VIKESA_G1_TURB_VF_PV",
        "VIKESA_G1_TURB_VIRKNGRD_PV",
        "VIKESA_INNTAK_MAGASIN_FYLLGRAD_PV",
        "VIKESA_INNTAK_MAGASIN_NED_KAP_PV",
        "VIKESA_INNTAK_MAGASIN_VOLUM_PV",
        "VIKESA_INNTAK_MINVF_LITER_PV",
        "VIKESA_INNTAK_NIVA_OPPSTROM_REF_HRV_PV",
        "VIKESA_INNTAK_NIVA_OVERLOP_VF_PV",
        "VIKESA_KRST_KONTROLL_KOM_AL",

        // ───────── STØLSKRAFT (3 tags — minimal dekning) ─────────
        // Vakt-ROI og alarm-deteksjon ikke mulig før flere tags eksporteres.
        "STOLSKRAFT_G1_GEN_P_PV",
        "STOLSKRAFT_G1_GEN_TURTALL_PV",
        "STOLSKRAFT_G1_TURB_VF_PV",

        // ───────── ØRSDALEN (5 tags — tynn dekning) ─────────
        // Mangler G1_TURB_VF (virkningsgrad) og overflow (Vakt-ROI).
        // SCADA-prefiks 'ORSDAL' avviker fra slug 'orsdalen'.
        "ORSDAL_G1_GEN_P_PV",
        "ORSDAL_G1_TURB_PADRAG_PV",
        "ORSDAL_G1_TURB_VANN_TRYKK_PV",
        "ORSDAL_INNTAK_NIVA_OPPSTROM_KOTE_PV",
        "ORSDAL_KRST_KONTROLL_KOM_AL",

        // ───────── ØGREYFOSS — OGREY1 (20 tags, G1-side + felles INNTAK) ─────────
        // OGREY1 har all INNTAK-telemetri inklusive overflow. Vakt-ROI OK.
        "OGREY1_G1_GEN_COSPHI_PV",
        "OGREY1_G1_GEN_F_PV",
        "OGREY1_G1_GEN_P_PV",
        "OGREY1_G1_GEN_TURTALL_PV",
        "OGREY1_G1_HYDRL_OLJE_TRYKK_PV",
        "OGREY1_G1_KONTROLL_REG_P_SP_SP_LAST",
        "OGREY1_G1_RORGATE_VANN_TRYKK_PV",
        "OGREY1_G1_TURB_VANN_TRYKK_PV",
        "OGREY1_G1_TURB_VF_PV",
        "OGREY1_G1_TURB_VIRKNGRD_PV",
        "OGREY1_INNTAK_MAGASIN_FYLLGRAD_PV",
        "OGREY1_INNTAK_MAGASIN_NED_KAP_PV",
        "OGREY1_INNTAK_MAGASIN_TOT_VF_PV",
        "OGREY1_INNTAK_MAGASIN_VOLUM_PV",
        "OGREY1_INNTAK_NIVA_NEDSTROM_REF_HRV_PV",
        "OGREY1_INNTAK_NIVA_OPPSTROM_KOTE_PV",
        "OGREY1_INNTAK_NIVA_OPPSTROM_REF_HRV_PV",
        "OGREY1_INNTAK_NIVA_OVERLOP_VF_PV",
        "OGREY1_INNTAK_NIVA_VTA_PV",
        "OGREY1_KRST_KONTROLL_KOM_AL",

        // ───────── ØGREYFOSS — OGREY2 (11 tags, G2-side) ─────────
        // OGREY2 har kun generator-telemetri + egen KRST. Deler INNTAK med OGREY1.
        "OGREY2_G2_GEN_COSPHI_PV",
        "OGREY2_G2_GEN_F_PV",
        "OGREY2_G2_GEN_P_PV",
        "OGREY2_G2_GEN_TURTALL_PV",
        "OGREY2_G2_HYDRL_OLJE_TRYKK_PV",
        "OGREY2_G2_KONTROLL_REG_NIVA_SP_SP_LAST",
        "OGREY2_G2_RORGATE_VANN_TRYKK_PV",
        "OGREY2_G2_TURB_LEDEAPP_POS_PV",
        "OGREY2_G2_TURB_VF_PV",
        "OGREY2_G2_TURB_VIRKNGRD_PV",
        "OGREY2_KRST_KONTROLL_KOM_AL",

        // ───────── LØGJEN (16 tags) ─────────
        // Har overflow → Vakt-ROI fungerer. Avvik: GEN_TURTALL_PRST_PV-suffix.
        "LOGJEN_G1_GEN_COSPHI_PV",
        "LOGJEN_G1_GEN_F_PV",
        "LOGJEN_G1_GEN_P_PV",
        "LOGJEN_G1_GEN_TURTALL_PRST_PV",
        "LOGJEN_G1_KONTROLL_REG_NIVA_SP_SP_LAST",
        "LOGJEN_G1_TURB_LEDEAPP_POS_PV",
        "LOGJEN_G1_TURB_VF_PV",
        "LOGJEN_G1_TURB_VIRKNGRD_PV",
        "LOGJEN_INNTAK_MAGASIN_FYLLGRAD_PV",
        "LOGJEN_INNTAK_MAGASIN_NED_KAP_PV",
        "LOGJEN_INNTAK_MAGASIN_TOT_VF_PV",
        "LOGJEN_INNTAK_MAGASIN_VOLUM_PV",
        "LOGJEN_INNTAK_MINVF_LITER_PV",
        "LOGJEN_INNTAK_NIVA_OPPSTROM_REF_HRV_PV",
        "LOGJEN_INNTAK_NIVA_OVERLOP_VF_PV",
        "LOGJEN_KRST_KONTROLL_KOM_AL",
    ];
}
