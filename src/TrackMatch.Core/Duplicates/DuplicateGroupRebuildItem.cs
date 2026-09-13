namespace TrackMatch.Core.Duplicates;

/// <summary>
/// ConfirmedDuplicateの関係から再構成した1グループ分の永続化計画を表す。
/// </summary>
/// <param name="ExistingGroupId">再利用する既存グループID。新規グループの場合はnull</param>
/// <param name="KeepTrackId">グループ全体で残すTrack ID</param>
/// <param name="TrackIds">グループを構成するTrack ID</param>
public sealed record DuplicateGroupRebuildItem(
    long? ExistingGroupId,
    long KeepTrackId,
    IReadOnlyList<long> TrackIds);
