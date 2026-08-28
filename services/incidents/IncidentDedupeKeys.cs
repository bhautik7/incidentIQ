namespace IncidentIQ.Incidents;

/// <summary>
/// How "the same active problem" is identified, per rule shape.
///
/// Pattern rules key on the fingerprint. The server-error spike keys on the
/// service and environment, because it is about a service being broken rather
/// than about any one error.
///
/// Shared rather than owned by the detector, because the detector is no longer
/// the only thing that opens an incident: the diagnose endpoint opens one for
/// an uploaded log's dominant pattern. Two spellings of "fp:{fingerprint}"
/// would silently defeat the partial unique index that makes deduplication
/// work at all - the detector and the endpoint would each open their own
/// incident for the same error and neither would be wrong locally.
/// </summary>
public static class IncidentDedupeKeys
{
    public static string ForPattern(string fingerprint) => $"fp:{fingerprint}";

    public static string ForServerErrors(Guid serviceId, Guid environmentId) =>
        $"svc5xx:{serviceId:D}:{environmentId:D}";
}
