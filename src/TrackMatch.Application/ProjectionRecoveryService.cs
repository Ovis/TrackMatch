using TrackMatch.Core.Duplicates;
using TrackMatch.Core.Persistence;

namespace TrackMatch.Application;

/// <summary>
/// 永続Dirty Stateを検出し、Current Human VerdictからProjectionを再構築する。
/// </summary>
public sealed class ProjectionRecoveryService(
    IProjectionStateRepository projectionStateRepository,
    IDuplicateGroupRepository groupRepository,
    DuplicateGroupService duplicateGroupService,
    SupplementalCandidateReconciliationService supplementalReconciliationService,
    GlobalMutationGate? mutationGate = null)
{
    private readonly GlobalMutationGate _mutationGate = mutationGate ?? GlobalMutationGate.Shared;

    /// <summary>
    /// Globalまたは指定LibraryのDirty Stateを検出し、Candidate表示前に必要なRecoveryを完了する。
    /// </summary>
    public async Task<ProjectionRecoveryResult> RecoverIfNeededAsync(
        long libraryId,
        CancellationToken cancellationToken = default)
    {
        using var gate = await _mutationGate.EnterAsync(cancellationToken);
        var globalDirty = await projectionStateRepository.IsGlobalGroupsDirtyAsync(cancellationToken);
        var libraryDirty = await projectionStateRepository.IsLibraryKeepProjectionDirtyAsync(
            libraryId,
            cancellationToken);
        if (!globalDirty && !libraryDirty)
        {
            return new ProjectionRecoveryResult(false, false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (globalDirty)
        {
            await duplicateGroupService.SynchronizeGlobalForLibraryAsync(libraryId, CancellationToken.None);
            var groups = await groupRepository.GetByLibraryIdAsync(libraryId, CancellationToken.None);
            await supplementalReconciliationService.ReconcileAsync(
                groups,
                groups.SelectMany(group => group.GlobalTrackIds).Distinct().ToArray(),
                CancellationToken.None);

            // Dirty発生時のClosureは永続化しないため、他Libraryは保守的にDirty化して選択時に局所Recoveryする。
            foreach (var otherLibraryId in (await groupRepository.GetLibraryIdsAsync(CancellationToken.None))
                         .Where(id => id != libraryId))
            {
                await projectionStateRepository.SetLibraryKeepProjectionDirtyAsync(
                    otherLibraryId,
                    isDirty: true,
                    CancellationToken.None);
            }

            await projectionStateRepository.SetLibraryKeepProjectionDirtyAsync(
                libraryId,
                isDirty: false,
                CancellationToken.None);
            await projectionStateRepository.SetGlobalGroupsDirtyAsync(
                isDirty: false,
                CancellationToken.None);
            return new ProjectionRecoveryResult(true, true);
        }

        await duplicateGroupService.SynchronizeLibraryKeepAsync(libraryId, CancellationToken.None);
        await projectionStateRepository.SetLibraryKeepProjectionDirtyAsync(
            libraryId,
            isDirty: false,
            CancellationToken.None);
        return new ProjectionRecoveryResult(true, false);
    }
}

/// <summary>Library Load前に実施したProjection Recoveryの有無を表す。</summary>
public sealed record ProjectionRecoveryResult(bool RecoveryPerformed, bool GlobalRecoveryPerformed);
