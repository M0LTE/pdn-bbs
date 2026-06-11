using System.Collections.Frozen;

namespace Bbs.Core;

/// <summary>
/// The continent and country tables used to canonicalise and complete hierarchical addresses.
/// Transcribed verbatim from [BPQ-SRC MailRouting.c] (<c>struct Continent Continents[]</c> and
/// <c>struct Country Countries[]</c>) so our equivalences match LinBPQ exactly — compat spec
/// §2.4: "Country codes are understood (ALL@USA ≡ ALL@USA.NA); 2-char and 4-char continents
/// equivalent (NA≡NOAM)". We canonicalise internally to the 2-char form (LinBPQ's default,
/// FOURCHARCONT = 0).
/// </summary>
internal static class GeographicCodes
{
    /// <summary>4-char continent code → canonical 2-char code [BPQ-SRC MailRouting.c Continents[]].</summary>
    private static readonly FrozenDictionary<string, string> FourCharContinents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["EURO"] = "EU", // Europe
        ["MEDR"] = "EU", // Mediterranean
        ["ASIA"] = "AS", // The Orient
        ["INDI"] = "AS", // Indian Ocean including the Indian subcontinent
        ["MDLE"] = "AS", // Middle East
        ["SEAS"] = "AS", // South-East Asia
        ["NOAM"] = "NA", // North America (Canada, USA, Mexico)
        ["CEAM"] = "NA", // Central America
        ["CARB"] = "NA", // Caribbean
        ["SOAM"] = "SA", // South America
        ["AUNZ"] = "OC", // Australia/New Zealand
        ["EPAC"] = "OC", // Eastern Pacific
        ["NPAC"] = "OC", // Northern Pacific
        ["SPAC"] = "OC", // Southern Pacific
        ["WPAC"] = "OC", // Western Pacific
        ["NAFR"] = "AF", // Northern Africa
        ["CAFR"] = "AF", // Central Africa
        ["SAFR"] = "AF", // Southern Africa
        ["ANTR"] = "OC", // Antarctica
        ["MARS"] = "MARS", // Special for MARS network
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> TwoCharContinents =
        new[] { "EU", "AS", "NA", "SA", "OC", "AF", "MARS" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Country code → 2-char continent [BPQ-SRC MailRouting.c Countries[], Continent2 column].</summary>
    private static readonly FrozenDictionary<string, string> Countries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["AFG"] = "AS", ["ALA"] = "EU", ["ALB"] = "EU", ["DZA"] = "AF", ["ASM"] = "AS", ["AND"] = "EU",
        ["AGO"] = "AF", ["AIA"] = "NA", ["ATG"] = "NA", ["ARG"] = "SA", ["ARM"] = "AS", ["ABW"] = "NA",
        ["AUS"] = "OC", ["AUT"] = "EU", ["AZE"] = "AS", ["BHS"] = "NA", ["BHR"] = "AS", ["BGD"] = "AS",
        ["BRB"] = "NA", ["BLR"] = "EU", ["BEL"] = "EU", ["BLZ"] = "NA", ["BEN"] = "AF", ["BMU"] = "NA",
        ["BTN"] = "AS", ["BOL"] = "SA", ["BIH"] = "EU", ["BWA"] = "AF", ["BRA"] = "SA", ["VGB"] = "NA",
        ["BRN"] = "AS", ["BGR"] = "EU", ["BFA"] = "AF", ["BDI"] = "AF", ["KHM"] = "AS", ["CMR"] = "AF",
        ["CAN"] = "NA", ["CPV"] = "AF", ["CYM"] = "NA", ["CAF"] = "AF", ["TCD"] = "AF", ["CHL"] = "SA",
        ["CHN"] = "AS", ["HKG"] = "AS", ["MAC"] = "AS", ["COL"] = "SA", ["COG"] = "AF", ["COK"] = "OC",
        ["CRI"] = "NA", ["CIV"] = "AF", ["HRV"] = "EU", ["CUB"] = "NA", ["CYP"] = "EU", ["CZE"] = "EU",
        ["PRK"] = "AS", ["COD"] = "AF", ["DNK"] = "EU", ["DJI"] = "AF", ["DMA"] = "NA", ["DOM"] = "NA",
        ["ECU"] = "SA", ["EGY"] = "AF", ["SLV"] = "NA", ["GNQ"] = "AF", ["ERI"] = "AF", ["EST"] = "EU",
        ["ETH"] = "AF", ["FRO"] = "EU", ["FLK"] = "SA", ["FJI"] = "OC", ["FIN"] = "EU", ["FRA"] = "EU",
        ["GUF"] = "SA", ["PYF"] = "OC", ["GAB"] = "AF", ["GMB"] = "AF", ["GEO"] = "AS", ["DEU"] = "EU",
        ["GHA"] = "AF", ["GIB"] = "EU", ["GRC"] = "EU", ["GRL"] = "EU", ["GRD"] = "NA", ["GLP"] = "NA",
        ["GUM"] = "OC", ["GTM"] = "NA", ["GGY"] = "EU", ["GIN"] = "AF", ["GNB"] = "AF", ["GUY"] = "SA",
        ["HTI"] = "NA", ["VAT"] = "EU", ["HND"] = "NA", ["HUN"] = "EU", ["ISL"] = "EU", ["IND"] = "AS",
        ["IDN"] = "AS", ["IRN"] = "AS", ["IRQ"] = "AS", ["IRL"] = "EU", ["IMN"] = "EU", ["ISR"] = "AS",
        ["ITA"] = "EU", ["JAM"] = "NA", ["JPN"] = "AS", ["JEY"] = "EU", ["JOR"] = "AS", ["KAZ"] = "AS",
        ["KEN"] = "AF", ["KIR"] = "OC", ["KWT"] = "AS", ["KGZ"] = "AS", ["LAO"] = "AS", ["LVA"] = "EU",
        ["LBN"] = "AS", ["LSO"] = "AF", ["LBR"] = "AF", ["LBY"] = "AS", ["LIE"] = "EU", ["LTU"] = "EU",
        ["LUX"] = "EU", ["MDG"] = "AF", ["MWI"] = "AF", ["MYS"] = "AS", ["MDV"] = "AS", ["MLI"] = "AF",
        ["MLT"] = "EU", ["MHL"] = "OC", ["MTQ"] = "NA", ["MRT"] = "AF", ["MUS"] = "AF", ["MYT"] = "AF",
        ["MEX"] = "NA", ["FSM"] = "OC", ["MCO"] = "EU", ["MNG"] = "AS", ["MNE"] = "EU", ["MSR"] = "NA",
        ["MAR"] = "AF", ["MOZ"] = "AF", ["MMR"] = "AS", ["NAM"] = "AF", ["NRU"] = "OC", ["NPL"] = "AS",
        ["NLD"] = "EU", ["ANT"] = "NA", ["NCL"] = "OC", ["NZL"] = "OC", ["NIC"] = "SA", ["NER"] = "AF",
        ["NGA"] = "AF", ["NIU"] = "OC", ["NFK"] = "OC", ["MNP"] = "OC", ["NOR"] = "EU", ["PSE"] = "AS",
        ["OMN"] = "AS", ["PAK"] = "AS", ["PLW"] = "OC", ["PAN"] = "SA", ["PNG"] = "OC", ["PRY"] = "SA",
        ["PER"] = "SA", ["PHL"] = "AS", ["PCN"] = "OC", ["POL"] = "EU", ["PRT"] = "EU", ["PRI"] = "NA",
        ["QAT"] = "AS", ["KOR"] = "AS", ["MDA"] = "EU", ["REU"] = "AF", ["ROU"] = "EU", ["RUS"] = "AS",
        ["RWA"] = "AF", ["BLM"] = "NA", ["SHN"] = "SA", ["KNA"] = "NA", ["LCA"] = "NA", ["MAF"] = "NA",
        ["SPM"] = "NA", ["VCT"] = "NA", ["WSM"] = "OC", ["SMR"] = "EU", ["STP"] = "AF", ["SAU"] = "AS",
        ["SEN"] = "AF", ["SRB"] = "EU", ["SYC"] = "AF", ["SLE"] = "AF", ["SGP"] = "AS", ["SVK"] = "EU",
        ["SVN"] = "EU", ["SLB"] = "OC", ["SOM"] = "AF", ["ZAF"] = "AF", ["ESP"] = "EU", ["LKA"] = "AS",
        ["SDN"] = "AF", ["SUR"] = "SA", ["SJM"] = "EU", ["SWZ"] = "AF", ["SWE"] = "EU", ["CHE"] = "EU",
        ["SYR"] = "AS", ["TJK"] = "AS", ["THA"] = "AS", ["MKD"] = "EU", ["TLS"] = "AS", ["TGO"] = "AF",
        ["TKL"] = "OC", ["TON"] = "OC", ["TTO"] = "NA", ["TUN"] = "AF", ["TUR"] = "EU", ["TKM"] = "AS",
        ["TCA"] = "NA", ["TUV"] = "OC", ["UGA"] = "AF", ["UKR"] = "EU", ["ARE"] = "AS", ["GBR"] = "EU",
        ["TZA"] = "AF", ["USA"] = "NA", ["VIR"] = "NA", ["URY"] = "SA", ["UZB"] = "AS", ["VUT"] = "OC",
        ["VEN"] = "SA", ["VNM"] = "AS", ["WLF"] = "OC", ["ESH"] = "AF", ["YEM"] = "AF", ["ZMB"] = "AF",
        ["ZWE"] = "AF",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="element"/> is a known 2- or 4-char continent code.</summary>
    internal static bool IsContinent(string element) =>
        TwoCharContinents.Contains(element) || FourCharContinents.ContainsKey(element);

    /// <summary>
    /// Canonicalises a continent code to its 2-char form; returns the input unchanged when it
    /// is not a known continent code.
    /// </summary>
    internal static string CanonicalContinent(string element)
    {
        if (FourCharContinents.TryGetValue(element, out string? twoChar))
        {
            return twoChar;
        }

        return element;
    }

    /// <summary>Looks up the 2-char continent for a known country code.</summary>
    internal static bool TryGetCountryContinent(string element, out string continent)
    {
        if (Countries.TryGetValue(element, out string? found))
        {
            continent = found;
            return true;
        }

        continent = "";
        return false;
    }
}
