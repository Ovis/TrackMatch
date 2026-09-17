using Dapper;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>Track Content変更等で無効になったHuman VerdictをCurrentからHistoryへ原子的に退避する。</summary>
public sealed class SqliteHumanVerdictInvalidationRepository(SqliteDatabase database) : IHumanVerdictInvalidationRepository
{
    /// <inheritdoc />
    public async Task InvalidateByTrackAsync(long trackId, HumanVerdictInvalidationReason reason, CancellationToken cancellationToken = default)
    {
        if (trackId <= 0) throw new ArgumentOutOfRangeException(nameof(trackId));
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Currentを先に消すと履歴が欠落するため、同一Transaction内で必ずHistoryへコピーしてから削除する。
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO CandidateReviewHistory (
                TrackIdA, TrackIdB, Decision, PreferredTrackId, Note, SourceLibraryId, SourceLibraryNameSnapshot,
                ChangedAtUtcTicks, ChangeKind, InvalidationReason)
            SELECT TrackIdA, TrackIdB, Decision, PreferredTrackId, Note, SourceLibraryId, SourceLibraryNameSnapshot,
                   @ChangedAtUtcTicks, 'Invalidated', @Reason
            FROM CandidateReviews
            WHERE TrackIdA = @TrackId OR TrackIdB = @TrackId;
            """,
            new { TrackId = trackId, ChangedAtUtcTicks = DateTime.UtcNow.Ticks, Reason = reason.ToString() }, transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CandidateReviews WHERE TrackIdA = @TrackId OR TrackIdB = @TrackId;",
            new { TrackId = trackId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }
}
