namespace TrackMatch.Application;

/// <summary>
/// 同一TrackMatchプロセス内のCanonical/Projection変更を直列化する共通Gate。
/// </summary>
public sealed class GlobalMutationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>アプリ全体で共有するGateを取得する。</summary>
    public static GlobalMutationGate Shared { get; } = new();

    /// <summary>
    /// Review用にGateを即時取得する。競合中は待機せずnullを返す。
    /// </summary>
    public async Task<IDisposable?> TryEnterAsync(CancellationToken cancellationToken = default)
    {
        if (!await _semaphore.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        return new Lease(this);
    }

    /// <summary>
    /// Structural Mutation用にGateを取得できるまで待機する。
    /// </summary>
    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Lease(this);
    }

    private void Exit() => _semaphore.Release();

    private sealed class Lease(GlobalMutationGate owner) : IDisposable
    {
        private GlobalMutationGate? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Exit();
        }
    }
}
