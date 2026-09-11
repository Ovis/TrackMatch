using TrackMatch.Core.Playback;

namespace TrackMatch.App.Playback;

/// <summary>
/// Candidate A/Bを1本のCommon Timeline上で同期再生する機能を表す。
/// </summary>
public interface ISynchronizedPlaybackService : IDisposable
{
    event EventHandler? PlaybackEnded;

    event Action<string>? PlaybackFailed;

    TimeSpan Position { get; }

    TimeSpan Duration { get; }

    PlaybackOffsets Offsets { get; }

    SynchronizedPlaybackMode Mode { get; set; }

    float VolumeA { get; set; }

    float VolumeB { get; set; }

    bool IsPlaying { get; }

    bool IsPaused { get; }

    /// <summary>
    /// Candidateを読み込み、Common PositionとVolumeを初期状態へResetする。
    /// </summary>
    /// <param name="pathA">Track AのAudio File</param>
    /// <param name="pathB">Track BのAudio File</param>
    /// <param name="bestOffset">Chromaprint解析で得たBのAに対する相対Offset</param>
    void Load(string pathA, string pathB, TimeSpan bestOffset);

    void Play();

    void Pause();

    void Stop();

    void Seek(TimeSpan position);

    /// <summary>
    /// A/B双方のOffsetを指定し、相対差を維持してNormalizeする。
    /// </summary>
    void SetOffsets(TimeSpan offsetA, TimeSpan offsetB);
}
