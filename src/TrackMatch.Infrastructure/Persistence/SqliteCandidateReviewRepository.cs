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

        const string sql = """
            INSERT INTO CandidateReviews (
                TrackIdA, TrackIdB, Decision, Note, ReviewedAtUtcTicks)
            VALUES (@TrackIdA, @TrackIdB, @Decision, @Note, @ReviewedAtUtcTicks)
            ON CONFLICT(TrackIdA, TrackIdB) DO UPDATE SET
                Decision = excluded.Decision,
                Note = excluded.Note,
                ReviewedAtUtcTicks = excluded.ReviewedAtUtcTicks;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                review.Pair.TrackIdA,
                review.Pair.TrackIdB,
                Decision = review.Decision.ToString(),
                review.Note,
                ReviewedAtUtcTicks = DateTime.UtcNow.Ticks,
            },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlySet<CandidatePairKey>> GetExcludedPairKeysAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TrackIdA, TrackIdB
            FROM CandidateReviews
            WHERE Decision = @Decision;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ReviewPairRow>(new CommandDefinition(
            sql,
            new { Decision = CandidateReviewDecision.NotDuplicate.ToString() },
            cancellationToken: cancellationToken));
        return rows
            .Select(row => CandidatePairKey.Create(row.TrackIdA, row.TrackIdB))
            .ToHashSet();
    }

    private sealed record ReviewPairRow(long TrackIdA, long TrackIdB);
}
