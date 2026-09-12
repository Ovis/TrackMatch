using Dapper;
using TrackMatch.Core.Persistence;
using TrackMatch.Core.Quality;

namespace TrackMatch.Infrastructure.Persistence;

/// <summary>
/// Track単体の音質解析キャッシュをSQLiteへ保存する。
/// </summary>
public sealed class SqliteTrackQualityAnalysisRepository(SqliteDatabase database) : ITrackQualityAnalysisRepository
{
    /// <inheritdoc />
    public async Task<TrackQualityAnalysis?> GetAsync(long trackId, CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        const string sql = """
            SELECT TrackId, AnalysisVersion, Status, IntegratedLoudnessLufs, TruePeakDbtp,
                   LoudnessRangeLu, PeakToLoudnessRatioDb, PeakNearSampleCount, ClippingRunCount,
                   ClippingTotalDurationTicks, ClippingLongestDurationTicks, LeftRightLevelDifferenceDb,
                   EffectiveUpperFrequencyHz, HasHighFrequencyCutoff, HighFrequencyCutoffHz,
                   HighFrequencyEnergyRatio, HighFrequencyConsistency, AnalyzedAtUtcTicks, FailureReason
            FROM TrackQualityAnalyses
            WHERE TrackId = @TrackId;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            sql,
            new { TrackId = trackId },
            cancellationToken: cancellationToken));
        return row is null ? null : ToDomain(row);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(TrackQualityAnalysis analysis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.TrackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(analysis));
        }

        const string sql = """
            INSERT INTO TrackQualityAnalyses (
                TrackId, AnalysisVersion, Status, IntegratedLoudnessLufs, TruePeakDbtp,
                LoudnessRangeLu, PeakToLoudnessRatioDb, PeakNearSampleCount, ClippingRunCount,
                ClippingTotalDurationTicks, ClippingLongestDurationTicks, LeftRightLevelDifferenceDb,
                EffectiveUpperFrequencyHz, HasHighFrequencyCutoff, HighFrequencyCutoffHz,
                HighFrequencyEnergyRatio, HighFrequencyConsistency, AnalyzedAtUtcTicks, FailureReason)
            VALUES (
                @TrackId, @AnalysisVersion, @Status, @IntegratedLoudnessLufs, @TruePeakDbtp,
                @LoudnessRangeLu, @PeakToLoudnessRatioDb, @PeakNearSampleCount, @ClippingRunCount,
                @ClippingTotalDurationTicks, @ClippingLongestDurationTicks, @LeftRightLevelDifferenceDb,
                @EffectiveUpperFrequencyHz, @HasHighFrequencyCutoff, @HighFrequencyCutoffHz,
                @HighFrequencyEnergyRatio, @HighFrequencyConsistency, @AnalyzedAtUtcTicks, @FailureReason)
            ON CONFLICT(TrackId) DO UPDATE SET
                AnalysisVersion = excluded.AnalysisVersion,
                Status = excluded.Status,
                IntegratedLoudnessLufs = excluded.IntegratedLoudnessLufs,
                TruePeakDbtp = excluded.TruePeakDbtp,
                LoudnessRangeLu = excluded.LoudnessRangeLu,
                PeakToLoudnessRatioDb = excluded.PeakToLoudnessRatioDb,
                PeakNearSampleCount = excluded.PeakNearSampleCount,
                ClippingRunCount = excluded.ClippingRunCount,
                ClippingTotalDurationTicks = excluded.ClippingTotalDurationTicks,
                ClippingLongestDurationTicks = excluded.ClippingLongestDurationTicks,
                LeftRightLevelDifferenceDb = excluded.LeftRightLevelDifferenceDb,
                EffectiveUpperFrequencyHz = excluded.EffectiveUpperFrequencyHz,
                HasHighFrequencyCutoff = excluded.HasHighFrequencyCutoff,
                HighFrequencyCutoffHz = excluded.HighFrequencyCutoffHz,
                HighFrequencyEnergyRatio = excluded.HighFrequencyEnergyRatio,
                HighFrequencyConsistency = excluded.HighFrequencyConsistency,
                AnalyzedAtUtcTicks = excluded.AnalyzedAtUtcTicks,
                FailureReason = excluded.FailureReason;
            """;

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                analysis.TrackId,
                analysis.AnalysisVersion,
                Status = analysis.Status.ToString(),
                analysis.IntegratedLoudnessLufs,
                analysis.TruePeakDbtp,
                analysis.LoudnessRangeLu,
                analysis.PeakToLoudnessRatioDb,
                analysis.PeakNearSampleCount,
                analysis.ClippingRunCount,
                ClippingTotalDurationTicks = analysis.ClippingTotalDuration.Ticks,
                ClippingLongestDurationTicks = analysis.ClippingLongestDuration.Ticks,
                analysis.LeftRightLevelDifferenceDb,
                analysis.EffectiveUpperFrequencyHz,
                HasHighFrequencyCutoff = analysis.HasHighFrequencyCutoff is null ? (long?)null : analysis.HasHighFrequencyCutoff.Value ? 1L : 0L,
                analysis.HighFrequencyCutoffHz,
                analysis.HighFrequencyEnergyRatio,
                analysis.HighFrequencyConsistency,
                AnalyzedAtUtcTicks = analysis.AnalyzedAtUtc?.Ticks,
                analysis.FailureReason,
            },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task DeleteAsync(long trackId, CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM TrackQualityAnalyses WHERE TrackId = @TrackId;",
            new { TrackId = trackId },
            cancellationToken: cancellationToken));
    }

    private static TrackQualityAnalysis ToDomain(Row row)
        => new(
            row.TrackId,
            checked((int)row.AnalysisVersion),
            Enum.Parse<QualityAnalysisStatus>(row.Status, ignoreCase: false),
            row.IntegratedLoudnessLufs,
            row.TruePeakDbtp,
            row.LoudnessRangeLu,
            row.PeakToLoudnessRatioDb,
            row.PeakNearSampleCount,
            row.ClippingRunCount,
            TimeSpan.FromTicks(row.ClippingTotalDurationTicks),
            TimeSpan.FromTicks(row.ClippingLongestDurationTicks),
            row.LeftRightLevelDifferenceDb,
            row.EffectiveUpperFrequencyHz,
            row.HasHighFrequencyCutoff is null ? null : row.HasHighFrequencyCutoff != 0,
            row.HighFrequencyCutoffHz,
            row.HighFrequencyEnergyRatio,
            row.HighFrequencyConsistency,
            row.AnalyzedAtUtcTicks is null ? null : new DateTime(row.AnalyzedAtUtcTicks.Value, DateTimeKind.Utc),
            row.FailureReason);

    private sealed record Row(
        long TrackId,
        long AnalysisVersion,
        string Status,
        double? IntegratedLoudnessLufs,
        double? TruePeakDbtp,
        double? LoudnessRangeLu,
        double? PeakToLoudnessRatioDb,
        long PeakNearSampleCount,
        long ClippingRunCount,
        long ClippingTotalDurationTicks,
        long ClippingLongestDurationTicks,
        double? LeftRightLevelDifferenceDb,
        double? EffectiveUpperFrequencyHz,
        long? HasHighFrequencyCutoff,
        double? HighFrequencyCutoffHz,
        double? HighFrequencyEnergyRatio,
        double? HighFrequencyConsistency,
        long? AnalyzedAtUtcTicks,
        string? FailureReason);
}
