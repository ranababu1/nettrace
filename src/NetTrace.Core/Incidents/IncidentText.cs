namespace NetTrace.Core.Incidents;

/// <summary>
/// User-facing wording for classifications. Deliberately states what was observed, not a
/// proven cause, and never names an ISP outage as fact.
/// </summary>
public static class IncidentText
{
    public static string Title(IncidentClassification classification) => classification switch
    {
        IncidentClassification.LocalInterfaceDisconnected => "No network connection",
        IncidentClassification.GatewayUnreachable => "Router not responding",
        IncidentClassification.ExternalConnectivityUnavailable => "Internet not reachable",
        IncidentClassification.DnsResolutionFailure => "Website names not resolving",
        IncidentClassification.HttpsConnectivityFailure => "Websites not loading",
        _ => "Connectivity problem",
    };

    /// <summary>One sentence for the "what happened" line.</summary>
    public static string Explanation(IncidentClassification classification) => classification switch
    {
        IncidentClassification.LocalInterfaceDisconnected => "This computer had no connected network.",
        IncidentClassification.GatewayUnreachable => "Your router did not respond.",
        IncidentClassification.ExternalConnectivityUnavailable => "Your router responded but the internet did not.",
        IncidentClassification.DnsResolutionFailure => "The internet was reachable but website names did not resolve.",
        IncidentClassification.HttpsConnectivityFailure => "The internet was reachable but websites did not load.",
        _ => "Connectivity problem.",
    };
}
