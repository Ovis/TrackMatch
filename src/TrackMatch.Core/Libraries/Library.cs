namespace TrackMatch.Core.Libraries;

/// <summary>
/// 1つ以上の音楽ライブラリRootを束ねる論理Libraryを表す。
/// </summary>
public sealed record Library(
    long Id,
    string Name,
    IReadOnlyList<LibraryRoot> Roots);
