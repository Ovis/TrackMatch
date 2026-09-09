using TrackMatch.Core.Models;

namespace TrackMatch.Core.Scanning;

/// <summary>
/// 音源ファイルから比較に必要なメタデータを読み取る。
/// </summary>
public interface IAudioMetadataReader
{
    AudioTrackMetadata Read(string path);
}
