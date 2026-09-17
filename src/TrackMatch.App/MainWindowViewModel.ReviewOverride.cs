using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Infrastructure.Persistence;

namespace TrackMatch.App;

public sealed partial class MainWindowViewModel
{
    /// <summary>選択中CandidateのHuman Verdictを正本として保存する。</summary>
    private async Task SaveHumanVerdictAsync(CandidateReviewDecision decision, long? preferredTrackId)
    {
        var selected = SelectedCandidate;
        var library = SelectedLibrary;
        if (selected is null || library is null || !CanReview) return;

        StopPlayback();
        IsLoading = true;
        try
        {
            var database = new SqliteDatabase(DatabasePath);
            await database.InitializeAsync();
            var service = new DuplicateGroupService(
                new SqliteCandidateReviewRepository(database),
                new SqliteTrackLookupRepository(database),
                new SqliteDuplicateGroupRepository(database));
            await service.SaveReviewAsync(library.Id, CreateHumanVerdict(decision, preferredTrackId));
            await ReloadCandidatesPreservingPairAsync(selected.TrackIdA, selected.TrackIdB);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
