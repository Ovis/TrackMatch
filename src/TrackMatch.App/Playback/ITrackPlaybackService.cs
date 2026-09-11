namespace TrackMatch.App.Playback;

/// <summary>
/// 候補レビュー画面から利用する単一トラック再生機能を表す。
/// </summary>
public interface ITrackPlaybackService : IDisposable
{
    /// <summary>
    /// 再生中のトラックが終了したときに発生する。
    /// </summary>
    event EventHandler? PlaybackEnded;

    /// <summary>
    /// メディア再生に失敗したときに発生する。
    /// </summary>
    event EventHandler<string>? PlaybackFailed;

    /// <summary>
    /// 指定した音声ファイルを先頭から再生する。
    /// </summary>
    /// <param name="path">再生対象のローカル音声ファイル</param>
    void Play(string path);

    /// <summary>
    /// 現在の再生を停止する。
    /// </summary>
    void Stop();
}
