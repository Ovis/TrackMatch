namespace TrackMatch.Core.Persistence;

/// <summary>
/// Global TrackとLibrary Membershipを参照する境界を定義する。
/// </summary>
public interface ITrackLookupRepository
{
    /// <summary>
    /// Track IDからGlobal Trackを取得する。
    /// </summary>
    Task<StoredTrack?> GetByIdAsync(long trackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 対象Trackに関係するHuman Verdictを現在の派生計算へ利用できるか確認する。
    /// </summary>
    async Task<bool> IsHumanVerdictUsableAsync(long trackId, CancellationToken cancellationToken = default)
    {
        var track = await GetByIdAsync(trackId, cancellationToken);
        return track is not null && !track.IsMissing;
    }

    /// <summary>Content Verificationまたは再評価の影響範囲としてファイル整理を停止すべきか確認する。</summary>
    Task<bool> IsFileOrganizationBlockedAsync(long trackId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>
    /// 指定TrackがLibraryのMembershipに含まれるか確認する。
    /// </summary>
    Task<bool> IsInLibraryAsync(long trackId, long libraryId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定TrackをMembershipとして参照するLibrary ID一覧を取得する。
    /// </summary>
    Task<IReadOnlyList<long>> GetLibraryIdsAsync(long trackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定TrackをMembershipとして参照するLibraryの表示用情報を取得する。
    /// </summary>
    Task<IReadOnlyList<TrackLibraryReference>> GetLibrariesAsync(
        long trackId,
        CancellationToken cancellationToken = default);
}
