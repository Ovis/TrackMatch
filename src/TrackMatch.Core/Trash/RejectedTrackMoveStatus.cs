namespace TrackMatch.Core.Trash;

/// <summary>
/// Reject TrackのTrash移動可否または実行結果を表す。
/// </summary>
public enum RejectedTrackMoveStatus
{
    Ready,
    Moved,
    ReviewConflict,
    TrackNotFound,
    AlreadyMissing,
    SourceMissing,
    OutsideLibraryRoot,
    DestinationExists,
    Failed,
}
