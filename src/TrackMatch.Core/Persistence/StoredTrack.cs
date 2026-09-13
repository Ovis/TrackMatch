using TrackMatch.Core.Models;

namespace TrackMatch.Core.Persistence;

/// <summary>
/// Libraryから独立して永続化された、物理Path単位のGlobal Trackを表す。
/// </summary>
public sealed record StoredTrack(
    long Id,
    AudioTrackMetadata Metadata,
    bool IsMissing);
