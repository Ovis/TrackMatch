namespace TrackMatch.Core.Trash;

/// <summary>
/// ConfirmedDuplicateレビューから構築したTrash移動結果を表す。
/// </summary>
public sealed record RejectedTrackTrashResult(
    IReadOnlyList<RejectedTrackMoveItem> Items,
    bool Executed)
{
    public int ReadyCount => Items.Count(item => item.Status == RejectedTrackMoveStatus.Ready);

    public int MovedCount => Items.Count(item => item.Status == RejectedTrackMoveStatus.Moved);

    public int BlockedCount => Items.Count(item => item.Status is not RejectedTrackMoveStatus.Ready and not RejectedTrackMoveStatus.Moved);
}
