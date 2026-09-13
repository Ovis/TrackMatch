using TrackMatch.Core.Persistence;

namespace TrackMatch.Core.Trash;

/// <summary>
/// 1つの物理TrackをTrashへ移動したとき、現在Library以外へ及ぶ影響を表す。
/// </summary>
public sealed record SharedTrackTrashImpact(
    long TrackId,
    IReadOnlyList<TrackLibraryReference> OtherLibraries,
    IReadOnlyList<TrackLibraryReference> KeepLibraries)
{
    /// <summary>他LibraryでもMembershipされているShared Trackかどうか。</summary>
    public bool IsShared => OtherLibraries.Count > 0;

    /// <summary>他LibraryのCurrent KeepをMissingにするため強い警告が必要かどうか。</summary>
    public bool AffectsOtherLibraryKeep => KeepLibraries.Count > 0;
}
