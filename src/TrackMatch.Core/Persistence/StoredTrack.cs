using TrackMatch.Core.Models;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// 永続化された音源トラックを表す。
/// </summary>
public sealed record StoredTrack(
    long Id,
    AudioTrackMetadata Metadata,
    bool IsMissing,
    long LibraryId,
    long RootId,
    string RelativePath);
