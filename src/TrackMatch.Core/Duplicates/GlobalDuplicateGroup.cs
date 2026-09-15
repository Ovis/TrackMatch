namespace TrackMatch.Core.Duplicates;

/// <summary>
/// Library ProjectionやKeep状態を含まない、Global ConfirmedDuplicate Graphの連結成分を表す。
/// </summary>
public sealed record GlobalDuplicateGroup(
    long Id,
    IReadOnlyList<long> TrackIds);
