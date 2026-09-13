namespace TrackMatch.Core.Duplicates;

/// <summary>
/// 人手で重複と確定したTrackの連結成分と、グループ全体で残すTrackを表す。
/// </summary>
public sealed record DuplicateGroup(
    long Id,
    long LibraryId,
    long KeepTrackId,
    IReadOnlyList<long> TrackIds)
{
    /// <summary>
    /// 永続化されたグループが削除判断に利用できる状態か検証する。
    /// </summary>
    public void Validate()
    {
        if (Id <= 0)
        {
            throw new InvalidDataException("重複グループIDが不正である。");
        }

        if (LibraryId <= 0)
        {
            throw new InvalidDataException("重複グループのLibrary IDが不正である。");
        }

        if (TrackIds.Count < 2)
        {
            throw new InvalidDataException("重複グループには2件以上のTrackが必要である。");
        }

        if (TrackIds.Any(trackId => trackId <= 0) || TrackIds.Distinct().Count() != TrackIds.Count)
        {
            throw new InvalidDataException("重複グループのTrack IDが不正である。");
        }

        if (!TrackIds.Contains(KeepTrackId))
        {
            throw new InvalidDataException("残すTrackは重複グループの構成Trackである必要がある。");
        }
    }
}
