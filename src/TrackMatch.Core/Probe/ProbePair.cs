namespace TrackMatch.Core.Probe;

/// <summary>
/// Probeで比較する既知の音源ペアを表す。
/// </summary>
public sealed record ProbePair(
    string Label,
    string ExpectedRelation,
    string FileA,
    string FileB,
    string? Notes = null);
