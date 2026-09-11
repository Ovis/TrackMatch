using System.Text.Json;
using TrackMatch.Infrastructure.Persistence;

namespace TrackMatch.App.Settings;

/// <summary>
/// Machine-specificなTrackMatch設定をLocalApplicationData配下のJSONへ保存する。
/// </summary>
public sealed class JsonAppSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly string _path;

    /// <summary>
    /// 標準設定Pathを使用するStoreを生成する。
    /// </summary>
    public JsonAppSettingsStore()
        : this(Path.Combine(Path.GetDirectoryName(TrackMatchDataPaths.DefaultDatabasePath)!, "settings.json"))
    {
    }

    /// <summary>
    /// 指定Pathを使用するStoreを生成する。
    /// </summary>
    /// <param name="path">設定JSONの保存Path</param>
    internal JsonAppSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    /// <summary>
    /// 保存済み設定を読み込み、不在時は初期設定を返す。
    /// </summary>
    public async Task<TrackMatchAppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return TrackMatchAppSettings.Default;
        }

        await using var stream = File.OpenRead(_path);
        var settings = await JsonSerializer.DeserializeAsync<TrackMatchAppSettings>(
            stream,
            SerializerOptions,
            cancellationToken);
        return Normalize(settings ?? TrackMatchAppSettings.Default);
    }

    /// <summary>
    /// 設定を一時Fileへ書き出してから置換し、途中終了でJSONが壊れにくい形で保存する。
    /// </summary>
    public async Task SaveAsync(TrackMatchAppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var normalized = Normalize(settings);
            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("設定Fileの親Directoryを特定できない。");
            Directory.CreateDirectory(directory);

            var temporaryPath = _path + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, normalized, SerializerOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static TrackMatchAppSettings Normalize(TrackMatchAppSettings settings)
        => settings with
        {
            SimilarityDisplayLowerBoundPercent = Math.Clamp(settings.SimilarityDisplayLowerBoundPercent, 0, 100),
            TrashRoot = string.IsNullOrWhiteSpace(settings.TrashRoot) ? null : settings.TrashRoot.Trim(),
        };
}
