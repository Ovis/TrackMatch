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
        ValidateAnalysis(analysis);

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
            ToParameters(analysis),
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task<bool> TryCompleteAnalyzingAsync(
        TrackQualityAnalysis analysis,
        DateTime analyzingStartedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateAnalysis(analysis);
        if (analysis.Status == QualityAnalysisStatus.Analyzing)
        {
            throw new ArgumentException("完了結果をAnalyzing状態として保存できません。", nameof(analysis));
        }

        const string sql = """
            UPDATE TrackQualityAnalyses
            SET AnalysisVersion = @AnalysisVersion,
                Status = @Status,
                IntegratedLoudnessLufs = @IntegratedLoudnessLufs,
                TruePeakDbtp = @TruePeakDbtp,
                LoudnessRangeLu = @LoudnessRangeLu,
                PeakToLoudnessRatioDb = @PeakToLoudnessRatioDb,
                PeakNearSampleCount = @PeakNearSampleCount,
                ClippingRunCount = @ClippingRunCount,
                ClippingTotalDurationTicks = @ClippingTotalDurationTicks,
                ClippingLongestDurationTicks = @ClippingLongestDurationTicks,
                LeftRightLevelDifferenceDb = @LeftRightLevelDifferenceDb,
                EffectiveUpperFrequencyHz = @EffectiveUpperFrequencyHz,
                HasHighFrequencyCutoff = @HasHighFrequencyCutoff,
                HighFrequencyCutoffHz = @HighFrequencyCutoffHz,
                HighFrequencyEnergyRatio = @HighFrequencyEnergyRatio,
                HighFrequencyConsistency = @HighFrequencyConsistency,
                AnalyzedAtUtcTicks = @AnalyzedAtUtcTicks,
                FailureReason = @FailureReason
            WHERE TrackId = @TrackId
              AND Status = 'Analyzing'
              AND AnalyzedAtUtcTicks = @ExpectedAnalyzingStartedAtUtcTicks;
            """;

        var parameters = ToParameters(analysis);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                parameters.TrackId,
                parameters.AnalysisVersion,
                parameters.Status,
                parameters.IntegratedLoudnessLufs,
                parameters.TruePeakDbtp,
                parameters.LoudnessRangeLu,
                parameters.PeakToLoudnessRatioDb,
                parameters.PeakNearSampleCount,
                parameters.ClippingRunCount,
                parameters.ClippingTotalDurationTicks,
                parameters.ClippingLongestDurationTicks,
                parameters.LeftRightLevelDifferenceDb,
                parameters.EffectiveUpperFrequencyHz,
                parameters.HasHighFrequencyCutoff,
                parameters.HighFrequencyCutoffHz,
                parameters.HighFrequencyEnergyRatio,
                parameters.HighFrequencyConsistency,
                parameters.AnalyzedAtUtcTicks,
                parameters.FailureReason,
                ExpectedAnalyzingStartedAtUtcTicks = analyzingStartedAtUtc.ToUniversalTime().Ticks,
            },
            cancellationToken: cancellationToken));
        return affected == 1;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAnalyzingAsync(
        long trackId,
        DateTime analyzingStartedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM TrackQualityAnalyses
            WHERE TrackId = @TrackId
              AND Status = 'Analyzing'
              AND AnalyzedAtUtcTicks = @ExpectedAnalyzingStartedAtUtcTicks;
            """,
            new
            {
                TrackId = trackId,
                ExpectedAnalyzingStartedAtUtcTicks = analyzingStartedAtUtc.ToUniversalTime().Ticks,
            },
            cancellationToken: cancellationToken));
        return affected == 1;
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

    private static void ValidateAnalysis(TrackQualityAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (analysis.TrackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(analysis));
        }
    }

    private static AnalysisParameters ToParameters(TrackQualityAnalysis analysis)
        => new(
            analysis.TrackId,
            analysis.AnalysisVersion,
            analysis.Status.ToString(),
            analysis.IntegratedLoudnessLufs,
            analysis.TruePeakDbtp,
            analysis.LoudnessRangeLu,
            analysis.PeakToLoudnessRatioDb,
            analysis.PeakNearSampleCount,
            analysis.ClippingRunCount,
            analysis.ClippingTotalDuration.Ticks,
            analysis.ClippingLongestDuration.Ticks,
            analysis.LeftRightLevelDifferenceDb,
            analysis.EffectiveUpperFrequencyHz,
            analysis.HasHighFrequencyCutoff is null ? null : analysis.HasHighFrequencyCutoff.Value ? 1L : 0L,
            analysis.HighFrequencyCutoffHz,
            analysis.HighFrequencyEnergyRatio,
            analysis.HighFrequencyConsistency,
            analysis.AnalyzedAtUtc?.ToUniversalTime().Ticks,
            analysis.FailureReason);

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

    private sealed record AnalysisParameters(
        long TrackId,
        int AnalysisVersion,
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
