namespace AgentFox.Plugins.Research;

/// <summary>
/// A single web source consulted during research: the URL plus optional human-readable
/// title and source label. Surfaced to the UI as a "Sources" citation under a chat reply.
/// </summary>
public sealed record ResearchReference(string Url, string? Title = null, string? Source = null);
