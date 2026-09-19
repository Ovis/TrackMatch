using Dapper;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Global Human VerdictのCurrent Stateと、再確認表示に必要なユーザー操作履歴をSQLiteへ保存する。
/// </summary>
public sealed class SqliteCandidateReviewRepository(SqliteDatabase database, long? sourceLibraryId = null) : ICandidateReviewMutationRepository
{
    /// <inheritdoc />
    public async Task SaveAsync(CandidateReview review, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        review.Validate();
        ValidateSourceLibraryId();
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existing = await GetCurrentAsync(connection, transaction, review.Pair, cancellationToken);
        if (existing is not null)
        {
            await ArchiveAsync(connection, transaction, existing, "UserChanged", cancellationToken);
        }

        string? sourceLibraryNameSnapshot = null;
        if (sourceLibraryId is { } sourceId)
        {
            sourceLibraryNameSnapshot = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                "SELECT Name FROM Libraries WHERE Id = @LibraryId;", new { LibraryId = sourceId }, transaction,
                cancellationToken: cancellationToken));
            if (sourceLibraryNameSnapshot is null) throw new InvalidOperationException("操作元Libraryが存在しません。");
        }

        const string sql = """
            INSERT INTO CandidateReviews (TrackIdA, TrackIdB, Decision, PreferredTrackId, Note, SourceLibraryId, SourceLibraryNameSnapshot, ReviewedAtUtcTicks)
            VALUES (@TrackIdA, @TrackIdB, @Decision, @PreferredTrackId, @Note, @SourceLibraryId, @SourceLibraryNameSnapshot, @ReviewedAtUtcTicks)
            ON CONFLICT(TrackIdA, TrackIdB) DO UPDATE SET
                Decision = excluded.Decision,
                PreferredTrackId = excluded.PreferredTrackId,
                Note = excluded.Note,
                SourceLibraryId = excluded.SourceLibraryId,
                SourceLibraryNameSnapshot = excluded.SourceLibraryNameSnapshot,
                ReviewedAtUtcTicks = excluded.ReviewedAtUtcTicks;
            """;
        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            review.Pair.TrackIdA,
            review.Pair.TrackIdB,
            Decision = review.Decision.ToString(),
            review.PreferredTrackId,
            review.Note,
            SourceLibraryId = sourceLibraryId,
            SourceLibraryNameSnapshot = sourceLibraryNameSnapshot,
            ReviewedAtUtcTicks = DateTime.UtcNow.Ticks,
        }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task SaveAsync(CandidateReview review, long sourceLibraryId, CancellationToken cancellationToken = default)
        => new SqliteCandidateReviewRepository(database, sourceLibraryId).SaveAsync(review, cancellationToken);

    /// <inheritdoc />
    public async Task DeleteAsync(CandidatePairKey pair, CancellationToken cancellationToken = default)
    {
        ValidateSourceLibraryId();
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existing = await GetCurrentAsync(connection, transaction, pair, cancellationToken);
        if (existing is not null) await ArchiveAsync(connection, transaction, existing, "UserCleared", cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CandidateReviews WHERE TrackIdA = @TrackIdA AND TrackIdB = @TrackIdB;",
            new { pair.TrackIdA, pair.TrackIdB }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(CandidatePairKey pair, long sourceLibraryId, CancellationToken cancellationToken = default)
        => new SqliteCandidateReviewRepository(database, sourceLibraryId).DeleteAsync(pair, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<CandidateReview>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT TrackIdA, TrackIdB, Decision, PreferredTrackId, Note FROM CandidateReviews ORDER BY TrackIdA, TrackIdB;";
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ReviewRow>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return rows.Select(ToReview).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<CandidatePairKey>> GetExcludedPairKeysAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ReviewPairRow>(new CommandDefinition("SELECT TrackIdA, TrackIdB FROM CandidateReviews;", cancellationToken: cancellationToken));
        return rows.Select(row => CandidatePairKey.Create(row.TrackIdA, row.TrackIdB)).ToHashSet();
    }

    private static Task<CurrentReviewRow?> GetCurrentAsync(Microsoft.Data.Sqlite.SqliteConnection connection, System.Data.Common.DbTransaction transaction, CandidatePairKey pair, CancellationToken cancellationToken)
        => connection.QuerySingleOrDefaultAsync<CurrentReviewRow>(new CommandDefinition(
            "SELECT TrackIdA, TrackIdB, Decision, PreferredTrackId, Note, SourceLibraryId, SourceLibraryNameSnapshot, ReviewedAtUtcTicks FROM CandidateReviews WHERE TrackIdA = @TrackIdA AND TrackIdB = @TrackIdB;",
            new { pair.TrackIdA, pair.TrackIdB }, transaction, cancellationToken: cancellationToken));

    private static async Task ArchiveAsync(Microsoft.Data.Sqlite.SqliteConnection connection, System.Data.Common.DbTransaction transaction, CurrentReviewRow current, string changeKind, CancellationToken cancellationToken)
    {
        // Historyは再確認表示など既存機能から参照されるためユーザー操作履歴だけ維持する。
        // Content Changeによる機械的な無効化はここを通さず、仕様どおり監査履歴を増やさない。
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO CandidateReviewHistory (TrackIdA, TrackIdB, Decision, PreferredTrackId, Note, SourceLibraryId, SourceLibraryNameSnapshot, ChangedAtUtcTicks, ChangeKind)
            VALUES (@TrackIdA, @TrackIdB, @Decision, @PreferredTrackId, @Note, @SourceLibraryId, @SourceLibraryNameSnapshot, @ChangedAtUtcTicks, @ChangeKind);
            """,
            new { current.TrackIdA, current.TrackIdB, current.Decision, current.PreferredTrackId, current.Note, current.SourceLibraryId, current.SourceLibraryNameSnapshot, ChangedAtUtcTicks = DateTime.UtcNow.Ticks, ChangeKind = changeKind },
            transaction, cancellationToken: cancellationToken));
    }

    private void ValidateSourceLibraryId()
    {
        if (sourceLibraryId is <= 0) throw new ArgumentOutOfRangeException(nameof(sourceLibraryId));
    }

    private static CandidateReview ToReview(ReviewRow row)
    {
        if (!Enum.TryParse<CandidateReviewDecision>(row.Decision, out var decision)) throw new InvalidDataException($"未知の候補レビュー判定です: {row.Decision}");
        var review = new CandidateReview(CandidatePairKey.Create(row.TrackIdA, row.TrackIdB), decision, row.PreferredTrackId, row.Note);
        review.Validate();
        return review;
    }

    private sealed record ReviewPairRow(long TrackIdA, long TrackIdB);
    private sealed record ReviewRow(long TrackIdA, long TrackIdB, string Decision, long? PreferredTrackId, string? Note);
    private sealed record CurrentReviewRow(long TrackIdA, long TrackIdB, string Decision, long? PreferredTrackId, string? Note, long? SourceLibraryId, string? SourceLibraryNameSnapshot, long ReviewedAtUtcTicks);
}
