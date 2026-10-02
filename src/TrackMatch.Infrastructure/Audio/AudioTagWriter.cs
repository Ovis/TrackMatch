using TrackMatch.Core.Models;

namespace TrackMatch.Infrastructure.Audio;

/// <summary>MP3/FLACの曲情報タグだけを更新する。</summary>
public sealed class AudioTagWriter
{
    public void Write(string path, AudioTrackMetadata values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(values);

        var extension = Path.GetExtension(path);
        if (!extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".flac", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"タグ編集に対応していないAudio Formatである: {extension}");
        }

        using var file = TagLib.File.Create(path);
        file.Tag.Performers = values.Artists.ToArray();
        file.Tag.Title = values.Title;
        file.Tag.Album = values.Album;
        file.Tag.Genres = values.Genres.ToArray();
        file.Tag.Year = values.Year ?? 0;
        file.Tag.Track = values.TrackNumber ?? 0;
        file.Tag.Disc = values.DiscNumber ?? 0;
        file.Save();
    }
}
