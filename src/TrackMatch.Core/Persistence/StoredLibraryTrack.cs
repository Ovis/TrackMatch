namespace TrackMatch.Core.Persistence;

/// <summary>
/// LibraryがGlobal Trackを比較対象として保持するMembershipを表す。
/// </summary>
/// <param name="LibraryId">Membershipを所有するLibrary ID</param>
/// <param name="TrackId">共有されるGlobal Track ID</param>
/// <param name="RootId">Membershipを確立したLibrary Root ID</param>
/// <param name="RelativePath">RootからTrackまでの相対Path</param>
/// <param name="CandidateGenerationPending">Candidate再探索が必要かどうか</param>
/// <param name="CandidateGenerationVersion">最後に正常完了したCandidate Generation Version。未完了の場合はnull</param>
public sealed record StoredLibraryTrack(
    long LibraryId,
    long TrackId,
    long RootId,
    string RelativePath,
    bool CandidateGenerationPending,
    int? CandidateGenerationVersion);
