namespace TrackMatch.Core.Persistence;

/// <summary>
/// Global TrackをMembershipとして参照するLibraryの表示用最小情報を表す。
/// </summary>
public sealed record TrackLibraryReference(long Id, string Name);
