using TrackMatch.Core.Comparison;
using TrackMatch.Core.Fingerprinting;

namespace TrackMatch.Core.Probe;

/// <summary>
/// 既知の音源ペアを順次比較し、しきい値調整用の測定結果を生成する。
/// </summary>
public sealed class ProbeRunner(
    IFingerprintExtractor fingerprintExtractor,
    FingerprintComparer fingerprintComparer)
{
    private readonly IFingerprintExtractor _fingerprintExtractor = fingerprintExtractor ?? throw new ArgumentNullException(nameof(fingerprintExtractor));
    private readonly FingerprintComparer _fingerprintComparer = fingerprintComparer ?? throw new ArgumentNullException(nameof(fingerprintComparer));

    public async IAsyncEnumerable<ProbeResult> RunAsync(
        IEnumerable<ProbePair> pairs,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        // 同じ音源を複数ペアで使うことが多いため、1回のProbe実行中は抽出結果を再利用する。
        var fingerprintTasks = new Dictionary<string, Task<AudioFingerprint>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fingerprintATask = GetOrCreateFingerprintTask(pair.FileA, fingerprintTasks, cancellationToken);
            var fingerprintBTask = GetOrCreateFingerprintTask(pair.FileB, fingerprintTasks, cancellationToken);
            await Task.WhenAll(fingerprintATask, fingerprintBTask).ConfigureAwait(false);

            var fingerprintA = await fingerprintATask.ConfigureAwait(false);
            var fingerprintB = await fingerprintBTask.ConfigureAwait(false);
            var comparison = _fingerprintComparer.Compare(fingerprintA, fingerprintB);

            yield return new ProbeResult(pair, fingerprintA, fingerprintB, comparison);
        }
    }

    private Task<AudioFingerprint> GetOrCreateFingerprintTask(
        string path,
        Dictionary<string, Task<AudioFingerprint>> cache,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!cache.TryGetValue(fullPath, out var task))
        {
            task = _fingerprintExtractor.ExtractAsync(fullPath, cancellationToken);
            cache.Add(fullPath, task);
        }

        return task;
    }
}
