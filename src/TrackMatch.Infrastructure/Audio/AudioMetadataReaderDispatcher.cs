using TrackMatch.Core.Models;
using TrackMatch.Core.Scanning;

namespace TrackMatch.Infrastructure.Audio;

/// <summary>
/// 正式対応Extensionに応じてFormat固有Metadata Readerへ処理を振り分ける。
/// </summary>
public sealed class AudioMetadataReaderDispatcher : IAudioMetadataReader
{
    private readonly IReadOnlyDictionary<string, IAudioMetadataReader> _readers;

    /// <summary>
    /// FLACとMP3の標準Readerを使用するDispatcherを生成する。
    /// </summary>
    public AudioMetadataReaderDispatcher()
        : this(new Dictionary<string, IAudioMetadataReader>(StringComparer.OrdinalIgnoreCase)
        {
            [".flac"] = new FlacMetadataReader(),
            [".mp3"] = new Mp3MetadataReader(),
        })
    {
    }

    /// <summary>
    /// ExtensionごとのReaderを指定してDispatcherを生成する。
    /// </summary>
    /// <param name="readers">先頭に`.`を含むExtensionとReaderの対応表</param>
    internal AudioMetadataReaderDispatcher(IReadOnlyDictionary<string, IAudioMetadataReader> readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        _readers = readers;
    }

    /// <inheritdoc />
    public AudioTrackMetadata Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var extension = Path.GetExtension(path);
        if (!_readers.TryGetValue(extension, out var reader))
        {
            // Decode backendが他Formatを扱えても正式対応範囲を拡張しないため、Extensionを明示的に制限する。
            throw new NotSupportedException($"対応していないAudio Formatである: {extension}");
        }

        return reader.Read(path);
    }
}
