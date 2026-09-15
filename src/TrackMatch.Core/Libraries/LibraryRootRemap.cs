namespace TrackMatch.Core.Libraries;

/// <summary>
/// Root保存場所変更によって連動して移動扱いとなるLibrary Rootを表す。
/// </summary>
/// <param name="LibraryId">影響を受けるLibrary ID</param>
/// <param name="LibraryName">確認表示用のLibrary名</param>
/// <param name="RootId">影響を受けるRoot ID</param>
/// <param name="OldRootPath">変更前Root Path</param>
/// <param name="NewRootPath">変更後Root Path</param>
/// <param name="IsRequestedRoot">ユーザーが直接変更を指定したRootならtrue</param>
public sealed record LibraryRootRemapImpact(
    long LibraryId,
    string LibraryName,
    long RootId,
    string OldRootPath,
    string NewRootPath,
    bool IsRequestedRoot)
{
    /// <summary>確認画面で1行表示する影響内容を返す。</summary>
    public string DisplayText => $"{LibraryName}: {OldRootPath} → {NewRootPath}";
}

/// <summary>
/// Global Trackの移転先Pathが既存Track Identityと衝突する状態を表す。
/// </summary>
public sealed record LibraryRootRemapCollision(
    long MovingTrackId,
    string OldPath,
    string NewPath,
    long ExistingTrackId)
{
    /// <summary>確認画面で1行表示する衝突内容を返す。</summary>
    public string DisplayText => $"Track #{MovingTrackId}: {NewPath} は既存Track #{ExistingTrackId} と衝突";
}

/// <summary>
/// Root保存場所変更前のGlobal relocation検証結果を保持する。
/// </summary>
public sealed record LibraryRootRemapPreview(
    long LibraryId,
    long RootId,
    string OldRootPath,
    string NewRootPath,
    int TargetTrackCount,
    int MatchedTrackCount,
    IReadOnlyList<string> MissingRelativePaths,
    int UnknownAudioFileCount,
    IReadOnlyList<LibraryRootRemapImpact> AffectedRoots,
    IReadOnlyList<LibraryRootRemapCollision> PathCollisions)
{
    /// <summary>新Rootで見つからなかった既存Track数を返す。</summary>
    public int MissingTrackCount => MissingRelativePaths.Count;

    /// <summary>Path Identity衝突がなく、DBへ安全に適用できるかを返す。</summary>
    public bool CanApply => PathCollisions.Count == 0;
}

/// <summary>
/// Global Root relocationを適用した結果を保持する。
/// </summary>
public sealed record LibraryRootRemapResult(
    long LibraryId,
    long RootId,
    string OldRootPath,
    string NewRootPath,
    int TargetTrackCount,
    int MatchedTrackCount,
    int MissingTrackCount,
    int UnknownAudioFileCount,
    int AffectedRootCount,
    int RemovedMembershipCount);
