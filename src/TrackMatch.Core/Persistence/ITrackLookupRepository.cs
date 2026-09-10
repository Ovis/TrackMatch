namespace TrackMatch.Core.Persistence;

/// <summary>
/// Track IDから保存済みTrackを参照する境界を定義する。
/// </summary>
public interface ITrackLookupRepository
{
    Task<StoredTrack?> GetByIdAsync(long trackId, CancellationToken cancellationToken = default);
}
