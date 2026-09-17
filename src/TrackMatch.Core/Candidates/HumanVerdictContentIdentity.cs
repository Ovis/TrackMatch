namespace TrackMatch.Core.Candidates;

/// <summary>ファイル操作前後で音声Content Identityを確認した結果を表す。</summary>
public sealed record HumanVerdictContentIdentity(long FileSize, long LastWriteTimeUtcTicks, long DurationTicks)
{
    /// <summary>軽量属性が一致する場合に同一Content候補として扱う。最終判定は呼び出し側の検証結果と組み合わせる。</summary>
    public bool Matches(HumanVerdictContentIdentity other)
        => other is not null && FileSize == other.FileSize && DurationTicks == other.DurationTicks;
}
