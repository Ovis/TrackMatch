using Dapper;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Global Human VerdictのCurrent StateとHistoryをSQLiteへ保存する。
/// </summary>
public sealed class SqliteCandidateReviewRepository(
    SqliteDatabase database,
    long? sourceLibraryId = null) : ICandidateReviewMutationRepository
{
    /// <inheritdoc />
    public async Task SaveAsync(CandidateReview review, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        review.Validate();
        ValidateSourceLibraryId();

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var existing = await connection.QuerySingleOrDefaultAsync<CurrentReviewRow>(new CommandDefinition(
            """
            SELECT r.TrackIdA, r.TrackIdB, r.Decision, r.Note, r.SourceLibraryId,
                   r.ReviewedAtUtcTicks, s.KeepTrackId
            FROM CandidateReviews r
            LEFT JOIN CandidateReviewSelections s
                ON s.TrackIdA = r.TrackIdA AND s.TrackIdB = r.TrackIdB
            WHERE r.TrackIdA = @TrackIdA AND r.TrackIdB = @TrackIdB;
            """,
            new { review.Pair.TrackIdA, review.Pair.TrackIdB },
            transaction,
            cancellationToken: cancellationToken));
        if (existing is not null)
        {
            await ArchiveAsync(
                connection,
                transaction,
                existing,
                changeKind: "UserChanged",
                invalidationReason: null,
                cancellationToken);
        }

        const string reviewSql = """
            INSERT INTO CandidateReviews (
                TrackIdA, TrackIdB, Decision, Note, SourceLibraryId, ReviewedAtUtcTicks)
            VALUES (@TrackIdA, @TrackIdB, @Decision, @Note, @SourceLibraryId, @ReviewedAtUtcTicks)
            ON CONFLICT(TrackIdA, TrackIdB) DO UPDATE SET
                Decision = excluded.Decision,
                Note = excluded.Note,
                SourceLibraryId = excluded.SourceLibraryId,
                ReviewedAtUtcTicks = excluded.ReviewedAtUtcTicks;
            """;
        const string selectionSql = """
            INSERT INTO CandidateReviewSelections (TrackIdA, TrackIdB, KeepTrackId)
            VALUES (@TrackIdA, @TrackIdB, @KeepTrackId)
            ON CONFLICT(TrackIdA, TrackIdB) DO UPDATE SET
                KeepTrackId = excluded.KeepTrackId;
            """;
        const string deleteSelectionSql = """
            DELETE FROM CandidateReviewSelections
            WHERE TrackIdA = @TrackIdA AND TrackIdB = @TrackIdB;
            """;

        var parameters = new
        {
            review.Pair.TrackIdA,
            review.Pair.TrackIdB,
            Decision = review.Decision.ToString(),
            review.Note,
            SourceLibraryId = sourceLibraryId,
            review.KeepTrackId,
            ReviewedAtUtcTicks = DateTime.UtcNow.Ticks,
        };

        await connection.ExecuteAsync(new CommandDefinition(reviewSql, parameters, transaction, cancellationToken: cancellationToken));
        var selectionCommand = review.Decision == CandidateReviewDecision.ConfirmedDuplicate
            ? selectionSql
            : deleteSelectionSql;
        await connection.ExecuteAsync(new CommandDefinition(selectionCommand, parameters, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(CandidatePairKey pair, CancellationToken cancellationToken = default)
    {
        ValidateSourceLibraryId();
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var existing = await connection.QuerySingleOrDefaultAsync<CurrentReviewRow>(new CommandDefinition(
            """
            SELECT r.TrackIdA, r.TrackIdB, r.Decision, r.Note, r.SourceLibraryId,
                   r.ReviewedAtUtcTicks, s.KeepTrackId
            FROM CandidateReviews r
            LEFT JOIN CandidateReviewSelections s
                ON s.TrackIdA = r.TrackIdA AND s.TrackIdB = r.TrackIdB
            WHERE r.TrackIdA = @TrackIdA AND r.TrackIdB = @TrackIdB;
            """,
            new { pair.TrackIdA, pair.TrackIdB },
            transaction,
            cancellationToken: cancellationToken));
        if (existing is not null)
        {
            await ArchiveAsync(
                connection,
                transaction,
                existing,
                changeKind: "UserCleared",
                invalidationReason: null,
                cancellationToken);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CandidateReviews WHERE TrackIdA = @TrackIdA AND TrackIdB = @TrackIdB;",
            new { pair.TrackIdA, pair.TrackIdB },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CandidateReview>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT r.TrackIdA, r.TrackIdB, r.Decision, r.Note, s.KeepTrackId
            FROM CandidateReviews r
            LEFT JOIN CandidateReviewSelections s
                ON s.TrackIdA = r.TrackIdA AND s.TrackIdB = r.TrackIdB
            ORDER BY r.TrackIdA, r.TrackIdB;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ReviewRow>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return rows.Select(ToReview).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<CandidatePairKey>> GetExcludedPairKeysAsync(CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT TrackIdA, TrackIdB FROM CandidateReviews;";
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ReviewPairRow>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return rows.Select(row => CandidatePairKey.Create(row.TrackIdA, row.TrackIdB)).ToHashSet();
    }

    private async Task ArchiveAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        CurrentReviewRow current,
        string changeKind,
        string? invalidationReason,
        CancellationToken cancellationToken)
    {
        string? sourceLibraryName = null;
        if (sourceLibraryId is { } operationLibraryId)
        {
            sourceLibraryName = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                "SELECT Name FROM Libraries WHERE Id = @LibraryId;",
                new { LibraryId = operationLibraryId },
                transaction,
                cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO CandidateReviewHistory (
                TrackIdA, TrackIdB, Decision, Note, SourceLibraryId, SourceLibraryNameSnapshot,
                ChangedAtUtcTicks, ChangeKind, InvalidationReason)
            VALUES (
                @TrackIdA, @TrackIdB, @Decision, @Note, @SourceLibraryId, @SourceLibraryNameSnapshot,
                @ChangedAtUtcTicks, @ChangeKind, @InvalidationReason);
            """,
            new
            {
                current.TrackIdA,
                current.TrackIdB,
                current.Decision,
                current.Note,
                SourceLibraryId = sourceLibraryName is null ? (long?)null : sourceLibraryId,
                SourceLibraryNameSnapshot = sourceLibraryName,
                ChangedAtUtcTicks = DateTime.UtcNow.Ticks,
                ChangeKind = changeKind,
                InvalidationReason = invalidationReason,
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private void ValidateSourceLibraryId()
    {
        if (sourceLibraryId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceLibraryId));
        }
    }

    private static CandidateReview ToReview(ReviewRow row)
    {
        if (!Enum.TryParse<CandidateReviewDecision>(row.Decision, out var decision))
        {
            throw new InvalidDataException($"未知の候補レビュー判定です: {row.Decision}");
        }

        var review = new CandidateReview(CandidatePairKey.Create(row.TrackIdA, row.TrackIdB), decision, row.Note, row.KeepTrackId);
        review.Validate();
        return review;
    }

    private sealed record ReviewPairRow(long TrackIdA, long TrackIdB);
    private sealed record ReviewRow(long TrackIdA, long TrackIdB, string Decision, string? Note, long? KeepTrackId);
    private sealed record CurrentReviewRow(
        long TrackIdA,
        long TrackIdB,
        string Decision,
        string? Note,
        long? SourceLibraryId,
        long ReviewedAtUtcTicks,
        long? KeepTrackId);
}
