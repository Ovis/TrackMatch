using TrackMatch.Core.Candidates;
using TrackMatch.Core.Duplicates;
using TrackMatch.Infrastructure.Persistence;

namespace TrackMatch.App;

public sealed partial class MainWindowViewModel
{
    private ReviewUndoSnapshot? _lastReviewUndo;
    private bool _isUndoingReview;

    /// <summary>
    /// 現在のLibraryで直前のCandidateレビュー操作を取り消せるかを返す。
    /// </summary>
    public bool CanUndoLastReview
        => !_isUndoingReview
            && _lastReviewUndo is { } snapshot
            && SelectedLibrary?.Id == snapshot.LibraryId;

    /// <summary>
    /// Candidateレビュー操作の前後Human Verdictを比較し、実際に正本が変わった場合だけ1段Undoへ登録する。
    /// </summary>
    /// <param name="action">既存の確認処理を含めたレビュー操作</param>
    public async Task ExecuteReviewWithUndoAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var selected = SelectedCandidate;
        var library = SelectedLibrary;
        if (selected is null || library is null)
        {
            await action();
            return;
        }

        var pair = CandidatePairKey.Create(selected.TrackIdA, selected.TrackIdB);
        var before = await CaptureReviewUndoSnapshotAsync(library.Id, pair);

        // 確認Dialogのキャンセルや保存失敗で既存Undoを失わないよう、操作完了後にだけ差分を判定する。
        await action();
        var after = await CaptureReviewUndoSnapshotAsync(library.Id, pair);
        if (!before.HasSameReviewState(after))
        {
            _lastReviewUndo = before;
            OnPropertyChanged(nameof(CanUndoLastReview));
        }
    }

    /// <summary>
    /// アプリ起動中に行った直前のCandidateレビュー操作を1件だけ元に戻す。
    /// </summary>
    public async Task UndoLastReviewAsync()
    {
        if (!CanUndoLastReview || _lastReviewUndo is not { } snapshot)
        {
            return;
        }

        _isUndoingReview = true;
        OnPropertyChanged(nameof(CanUndoLastReview));
        try
        {
            var database = new SqliteDatabase(DatabasePath);
            await database.InitializeAsync();
            var reviews = new SqliteCandidateReviewRepository(database);
            var tracks = new SqliteTrackLookupRepository(database);
            var groups = new SqliteDuplicateGroupRepository(database);
            var service = new DuplicateGroupService(reviews, tracks, groups);

            // Undoの正本はHuman Verdictだけである。Keepやレビュー省略は復元後のVerdict集合から再導出する。
            if (snapshot.Review is null)
            {
                await service.DeleteReviewAsync(snapshot.LibraryId, snapshot.Pair);
            }
            else
            {
                await service.SaveReviewAsync(snapshot.LibraryId, snapshot.Review);
            }

            // UndoでKeep候補が再び複数になった場合も、通常レビュー後と同じく仕分けを完遂できる比較手段を補完する。
            await EnsureSupplementalCandidatesAsync(database, snapshot.LibraryId);

            // Undo自体を再Undoする履歴は持たない。復元に成功してから1段履歴を消費する。
            _lastReviewUndo = null;
            await ReloadCandidatesPreservingPairAsync(snapshot.Pair.TrackIdA, snapshot.Pair.TrackIdB);
            await LoadDuplicateGroupsForSelectionAsync(SelectedCandidate);
        }
        finally
        {
            _isUndoingReview = false;
            OnPropertyChanged(nameof(CanUndoLastReview));
        }
    }

    private async Task<ReviewUndoSnapshot> CaptureReviewUndoSnapshotAsync(long libraryId, CandidatePairKey pair)
    {
        var database = new SqliteDatabase(DatabasePath);
        await database.InitializeAsync();
        var reviews = new SqliteCandidateReviewRepository(database);
        var review = (await reviews.GetAllAsync()).SingleOrDefault(item => item.Pair == pair);
        return new ReviewUndoSnapshot(libraryId, pair, review);
    }

    /// <summary>
    /// 直前レビュー操作の復元に必要なGlobal Human Verdictを保持する。
    /// </summary>
    private sealed record ReviewUndoSnapshot(
        long LibraryId,
        CandidatePairKey Pair,
        CandidateReview? Review)
    {
        /// <summary>レビュー操作によってHuman Verdictが実際に変化したかを判定する。</summary>
        public bool HasSameReviewState(ReviewUndoSnapshot other) => Equals(Review, other.Review);
    }
}
