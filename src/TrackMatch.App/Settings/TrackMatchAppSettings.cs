namespace TrackMatch.App.Settings;

/// <summary>
/// TrackMatchのユーザーUI設定を保持する。
/// </summary>
public sealed record TrackMatchAppSettings(
    long? LastSelectedLibraryId,
    int SimilarityDisplayLowerBoundPercent,
    string? TrashRoot)
{
    /// <summary>
    /// 初期設定を生成する。
    /// </summary>
    public static TrackMatchAppSettings Default { get; } = new(
        LastSelectedLibraryId: null,
        SimilarityDisplayLowerBoundPercent: 70,
        TrashRoot: null);
}
