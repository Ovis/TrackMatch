namespace TrackMatch.Core.Trash;

/// <summary>
/// Reject Track 1件のTrash移動計画または実行結果を表す。
/// </summary>
public sealed record RejectedTrackMoveItem(
    long TrackId,
    string? SourcePath,
    string? DestinationPath,
    RejectedTrackMoveStatus Status,
    string? Message = null);
