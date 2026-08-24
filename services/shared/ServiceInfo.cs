namespace IncidentIQ.Shared;

/// <summary>
/// Identity of the running process. Registered as a singleton by
/// <see cref="ServiceDefaults.AddIncidentIqDefaults"/> and surfaced on the root
/// and health endpoints so it is obvious which container answered a request.
/// </summary>
public sealed record ServiceInfo(string Name, string Version, string Environment);
