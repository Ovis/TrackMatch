namespace TrackMatch.Core.Duplicates;

/// <summary>
/// Global ConfirmedDuplicate Graphから再構成した1つの連結成分を表す。
/// </summary>
/// <param name="ExistingGroupId">再利用候補のGlobal Group ID。新規の場合はnull</param>
/// <param name="TrackIds">Global Groupを構成するTrack ID</param>
public sealed record DuplicateGroupRebuildItem(
    long? ExistingGroupId,
    IReadOnlyList<long> TrackIds);
