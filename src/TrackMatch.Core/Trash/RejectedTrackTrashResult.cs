namespace TrackMatch.Core.Trash;

/// <summary>
/// Library固有Keepから算出したTrash移動結果と、Shared TrackへのGlobal影響を表す。
/// </summary>
public sealed record RejectedTrackTrashResult(
    IReadOnlyList<RejectedTrackMoveItem> Items,
    bool Executed,
    IReadOnlyList<SharedTrackTrashImpact>? SharedImpacts = null)
{
    public int ReadyCount => Items.Count(item => item.Status == RejectedTrackMoveStatus.Ready);

    public int MovedCount => Items.Count(item => item.Status == RejectedTrackMoveStatus.Moved);

    public int BlockedCount => Items.Count(item => item.Status is not RejectedTrackMoveStatus.Ready and not RejectedTrackMoveStatus.Moved);

    /// <summary>Shared Trackに関する影響一覧。影響がなければ空。</summary>
    public IReadOnlyList<SharedTrackTrashImpact> SharedTrackImpacts => SharedImpacts ?? [];
}
