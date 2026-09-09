using TrackMatch.Core.Scanning;

namespace TrackMatch.Infrastructure.Scanning;

/// <summary>
/// 指定ディレクトリ以下のFLACファイルを再帰的に走査する。
/// </summary>
public sealed class FlacLibraryScanner(IAudioMetadataReader metadataReader) : ILibraryScanner
{
    private readonly IAudioMetadataReader _metadataReader = metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));

    public IEnumerable<LibraryScanResult> Scan(string rootPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"走査対象ディレクトリが存在しない: {rootPath}");
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var path in Directory.EnumerateFiles(rootPath, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(Path.GetExtension(path), ".flac", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            LibraryScanResult result;
            try
            {
                result = LibraryScanResult.Success(_metadataReader.Read(path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                result = LibraryScanResult.Failure(path, exception.Message);
            }

            yield return result;
        }
    }
}
