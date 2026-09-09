using System.Diagnostics;
using System.Text.Json;
using TrackMatch.Core.Fingerprinting;

namespace TrackMatch.Infrastructure.Chromaprint;

/// <summary>
/// 外部fpcalcプロセスを使ってChromaprint raw fingerprintを生成する。
/// </summary>
public sealed class FpcalcFingerprintExtractor(string fpcalcPath = "fpcalc") : IFingerprintExtractor
{
    private readonly string _fpcalcPath = string.IsNullOrWhiteSpace(fpcalcPath)
        ? throw new ArgumentException("fpcalcのパスを指定する必要がある。", nameof(fpcalcPath))
        : fpcalcPath;

    public async Task<AudioFingerprint> ExtractAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Fingerprint生成対象のファイルが存在しない。", path);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _fpcalcPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // 既定の120秒制限を解除して全曲を処理する。Algorithm 2を明示して比較条件を固定する。
        startInfo.ArgumentList.Add("-raw");
        startInfo.ArgumentList.Add("-json");
        startInfo.ArgumentList.Add("-length");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-algorithm");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add(path);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("fpcalcプロセスを開始できなかった。");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"fpcalcを起動できない。PATHまたは指定パスを確認する: {_fpcalcPath}",
                exception);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"fpcalcが終了コード{process.ExitCode}で失敗した: {stderr.Trim()}");
        }

        FpcalcJsonOutput? output;
        try
        {
            output = JsonSerializer.Deserialize<FpcalcJsonOutput>(stdout, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("fpcalcのJSON出力を解析できなかった。", exception);
        }

        if (output?.Fingerprint is null || output.Fingerprint.Length == 0)
        {
            throw new InvalidDataException("fpcalcから空のFingerprintが返された。");
        }

        return new AudioFingerprint(path, TimeSpan.FromSeconds(output.Duration), output.Fingerprint);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class FpcalcJsonOutput
    {
        public double Duration { get; init; }

        public uint[]? Fingerprint { get; init; }
    }
}
