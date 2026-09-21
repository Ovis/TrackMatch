namespace TrackMatch.Core.Duplicates;

/// <summary>
/// Global ConfirmedDuplicate Graphの連結成分を、指定Libraryへ投影した重複グループを表す。
/// </summary>
/// <param name="Id">Global Duplicate Group ID</param>
/// <param name="LibraryId">このProjectionを表示・処置するLibrary ID</param>
/// <param name="KeepTrackId">Library固有の派生Keep。候補複数・Human Verdict矛盾・Missingの場合はnull</param>
/// <param name="KeepStatus">Library固有Keepの現在状態</param>
/// <param name="TrackIds">現在LibraryのMembershipに含まれる構成Track ID</param>
/// <param name="GlobalTrackIds">Global Group全体の構成Track ID</param>
public sealed record DuplicateGroup(
    long Id,
    long LibraryId,
    long? KeepTrackId,
    DuplicateGroupKeepStatus KeepStatus,
    IReadOnlyList<long> TrackIds,
    IReadOnlyList<long> GlobalTrackIds)
{
    /// <summary>
    /// Projectionが表示・削除判断へ利用できる構造か検証する。
    /// </summary>
    public void Validate()
    {
        if (Id <= 0)
        {
            throw new InvalidDataException("重複グループIDが不正です。");
        }

        if (LibraryId <= 0)
        {
            throw new InvalidDataException("重複グループのLibrary IDが不正です。");
        }

        if (GlobalTrackIds.Count < 2
            || GlobalTrackIds.Any(trackId => trackId <= 0)
            || GlobalTrackIds.Distinct().Count() != GlobalTrackIds.Count)
        {
            throw new InvalidDataException("Global重複グループには重複のない2件以上のTrackが必要です。");
        }

        if (TrackIds.Any(trackId => trackId <= 0)
            || TrackIds.Distinct().Count() != TrackIds.Count
            || TrackIds.Any(trackId => !GlobalTrackIds.Contains(trackId)))
        {
            throw new InvalidDataException("Library ProjectionのTrack構成がGlobal Groupと一致しません。");
        }

        if (KeepStatus == DuplicateGroupKeepStatus.Selected)
        {
            if (KeepTrackId is null || !GlobalTrackIds.Contains(KeepTrackId.Value))
            {
                throw new InvalidDataException("選択済みKeepはGlobal Groupの構成Trackである必要があります。");
            }
        }
        else if (KeepTrackId is not null)
        {
            throw new InvalidDataException("Keepが確定していない状態ではCurrent KeepTrackIdを保持できません。");
        }
    }

    /// <summary>
    /// 現在Library外にも続くGlobal Groupかどうかを取得する。
    /// </summary>
    public bool HasExternalTracks => GlobalTrackIds.Count > TrackIds.Count;
}

/// <summary>
/// Library固有KeepのCurrent Stateを表す。
/// </summary>
public enum DuplicateGroupKeepStatus
{
    /// <summary>現在有効なPreferenceからKeepが一意に導出済み。</summary>
    Selected,

    /// <summary>Keep候補が複数残っており、追加レビューが必要。</summary>
    Unselected,

    /// <summary>同一ConfirmedDuplicate連結成分内のNotDuplicateによりHuman Verdictが矛盾している。</summary>
    Conflict,

    /// <summary>選択済みKeepがMissingとなり、再確認が必要。</summary>
    Missing,
}
