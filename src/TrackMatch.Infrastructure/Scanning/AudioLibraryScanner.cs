using TrackMatch.Core.Scanning;

namespace TrackMatch.Infrastructure.Scanning;

/// <summary>
/// 指定ディレクトリ以下の正式対応Audio Fileを再帰的に走査する。
/// </summary>
public sealed class AudioLibraryScanner(IAudioMetadataReader metadataReader) : ILibraryScanner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac",
        ".mp3",
    };

    private readonly IAudioMetadataReader _metadataReader = metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));

    /// <inheritdoc />
    public int? GetSupportedFileCount(string rootPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        EnsureDirectoryExists(rootPath);

        var count = 0;
        foreach (var path in EnumerateFiles(rootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SupportedExtensions.Contains(Path.GetExtension(path)))
            {
                count++;
            }
        }

        return count;
    }

    /// <inheritdoc />
    public IEnumerable<LibraryScanResult> Scan(string rootPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        EnsureDirectoryExists(rootPath);

        foreach (var path in EnumerateFiles(rootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Backendが偶然DecodeできるFormatを正式対応扱いしないため、Scanner入口でExtensionを固定する。
            if (!SupportedExtensions.Contains(Path.GetExtension(path)))
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
                // 1 Fileの破損でLibrary全体を中断せず、Current RunのAnalysis Errorとして返す。
                result = LibraryScanResult.Failure(path, exception.Message);
            }

            yield return result;
        }
    }

    private static IEnumerable<string> EnumerateFiles(string rootPath)
        => Directory.EnumerateFiles(
            rootPath,
            "*",
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            });

    private static void EnsureDirectoryExists(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"走査対象ディレクトリが存在しない: {rootPath}");
        }
    }
}
