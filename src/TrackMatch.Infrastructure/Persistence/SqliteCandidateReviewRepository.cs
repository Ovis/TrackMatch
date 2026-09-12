using Dapper;
using TrackMatch.Core.Candidates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// 人手で確定した候補レビューをSQLiteへ保存する。
/// </summary>
public sealed class SqliteCandidateReviewRepository(SqliteDatabase database) : ICandidateReviewRepository
{
    public async Task SaveAsync(CandidateReview review, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        review.Validate();

        const string reviewSql = """
            INSERT INTO CandidateReviews (
                TrackIdA, TrackIdB, Decision, Note, ReviewedAtUtcTicks)
            VALUES (@TrackIdA, @TrackIdB, @Decision, @Note, @ReviewedAtUtcTicks)
            ON CONFLICT(TrackIdA, TrackIdB) DO UPDATE SET
                Decision = excluded.Decision,
                Note = excluded.Note,
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
            review.KeepTrackId,
            ReviewedAtUtcTicks = DateTime.UtcNow.Ticks,
        };

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(reviewSql, parameters, transaction, cancellationToken: cancellationToken));

        var selectionCommand = review.Decision == CandidateReviewDecision.ConfirmedDuplicate
            ? selectionSql
            : deleteSelectionSql;
        await connection.ExecuteAsync(new CommandDefinition(selectionCommand, parameters, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 指定候補のレビューと保持Track選択を削除し、未レビュー状態へ戻す。
    /// </summary>
    public async Task DeleteAsync(CandidatePairKey pair, CancellationToken cancellationToken = default)
    {
        const string selectionSql = "DELETE FROM CandidateReviewSelections WHERE TrackIdA = @TrackIdA AND TrackIdB = @TrackIdB;";
        const string reviewSql = "DELETE FROM CandidateReviews WHERE TrackIdA = @TrackIdA AND TrackIdB = @TrackIdB;";
        var parameters = new { pair.TrackIdA, pair.TrackIdB };

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(selectionSql, parameters, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(reviewSql, parameters, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

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

    public async Task<IReadOnlySet<CandidatePairKey>> GetExcludedPairKeysAsync(CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT TrackIdA, TrackIdB FROM CandidateReviews;";
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ReviewPairRow>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return rows.Select(row => CandidatePairKey.Create(row.TrackIdA, row.TrackIdB)).ToHashSet();
    }

    private static CandidateReview ToReview(ReviewRow row)
    {
        if (!Enum.TryParse<CandidateReviewDecision>(row.Decision, out var decision))
        {
            throw new InvalidDataException($"未知の候補レビュー判定である: {row.Decision}");
        }

        var review = new CandidateReview(CandidatePairKey.Create(row.TrackIdA, row.TrackIdB), decision, row.Note, row.KeepTrackId);
        review.Validate();
        return review;
    }

    private sealed record ReviewPairRow(long TrackIdA, long TrackIdB);
    private sealed record ReviewRow(long TrackIdA, long TrackIdB, string Decision, string? Note, long? KeepTrackId);
}
