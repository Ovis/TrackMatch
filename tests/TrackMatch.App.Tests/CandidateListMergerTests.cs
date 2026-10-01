using TrackMatch.Core.Candidates;
using TrackMatch.Core.Classification;
using Xunit;

namespace TrackMatch.App.Tests;

public sealed class CandidateListMergerTests
{
    [Fact]
    public void Merge_AppliesAddReplaceAndRemoveWithoutResortingUnaffectedItems()
    {
        var replacedBefore = CreateItem(1, 2, 0.9);
        var removed = CreateItem(3, 4, 0.7);
        var unchanged = CreateItem(5, 6, 0.5);
        var added = CreateItem(7, 8, 0.8);
        var replacedAfter = CreateItem(1, 2, 0.4);

        var merged = CandidateListMerger.Merge(
            [replacedBefore, removed, unchanged],
            new HashSet<CandidatePairKey>
            {
                CandidatePairKey.Create(1, 2),
                CandidatePairKey.Create(3, 4),
                CandidatePairKey.Create(7, 8),
            },
            [added, replacedAfter],
            Compare);

        Assert.Equal(
            [CandidatePairKey.Create(7, 8), CandidatePairKey.Create(5, 6), CandidatePairKey.Create(1, 2)],
            merged.Select(GetPair).ToArray());
        Assert.Same(unchanged, merged[1]);
        Assert.Same(replacedAfter, merged[2]);
    }

    [Fact]
    public void Resolve_WhenPairRemains_SelectsNewViewModelByPairKey()
    {
        var previous = CreateItem(1, 2, 0.9);
        var replacement = CreateItem(1, 2, 0.8);
        var other = CreateItem(3, 4, 0.7);
        var pair = GetPair(previous);

        var selected = CandidateSelectionResolver.Resolve(
            new Dictionary<CandidatePairKey, CandidateReviewItemViewModel> { [pair] = replacement },
            [replacement, other],
            pair,
            previousDisplayIndex: 1);

        Assert.Same(replacement, selected);
    }

    [Fact]
    public void Resolve_WhenPairDisappears_SelectsNearestOldDisplayIndex()
    {
        var first = CreateItem(3, 4, 0.8);
        var second = CreateItem(5, 6, 0.7);

        var selected = CandidateSelectionResolver.Resolve(
            new Dictionary<CandidatePairKey, CandidateReviewItemViewModel>
            {
                [GetPair(first)] = first,
                [GetPair(second)] = second,
            },
            [first, second],
            CandidatePairKey.Create(1, 2),
            previousDisplayIndex: 1);

        Assert.Same(second, selected);
    }

    private static int Compare(CandidateReviewItemViewModel left, CandidateReviewItemViewModel right)
    {
        var similarity = right.Row.Similarity.CompareTo(left.Row.Similarity);
        if (similarity != 0)
        {
            return similarity;
        }

        var trackA = left.TrackIdA.CompareTo(right.TrackIdA);
        return trackA != 0 ? trackA : left.TrackIdB.CompareTo(right.TrackIdB);
    }

    private static CandidatePairKey GetPair(CandidateReviewItemViewModel item)
        => CandidatePairKey.Create(item.TrackIdA, item.TrackIdB);

    private static CandidateReviewItemViewModel CreateItem(long trackIdA, long trackIdB, double similarity)
        => new(new CandidateReviewReportRow(
            trackIdA, trackIdB, AudioRelationshipKind.DuplicateCandidate, null,
            similarity, 1, 1, 1,
            TimeSpan.Zero, TimeSpan.FromMinutes(3),
            $"{trackIdA}.flac", $"{trackIdB}.flac", ["Artist"], ["Artist"],
            $"Track {trackIdA}", $"Track {trackIdB}", "Album", "Album", [], [],
            TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3), 100, 100,
            "FLAC", "FLAC", "FLAC", "FLAC", 900, 900, 44100, 44100, 16, 16, 2, 2,
            ReviewDecision: null));
}
