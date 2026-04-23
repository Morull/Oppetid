namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Type vannkraftverk. Styrer hvilke klassifiseringsregler som brukes:
/// magasinverk kan styres av marked/manuelt valg, mens elvekraft primært
/// styres av hydrologisk ressurs.
/// </summary>
public enum PlantType
{
    /// <summary>Magasinkraftverk med regulerbar produksjon.</summary>
    Regulated,

    /// <summary>Elvekraftverk / uregulert småkraft. Produksjon følger tilløp.</summary>
    RunOfRiver,

    /// <summary>Kombinasjon av magasin- og elvekraftverk (f.eks. langs samme vassdrag).</summary>
    Mixed,

    /// <summary>Pumpekraftverk – kan både produsere og pumpe (negativ produksjon).</summary>
    Pumped
}
