namespace TrackMatch.Application;

/// <summary>
/// バックグラウンド品質解析へ渡すTrackを表す。
/// </summary>
/// <param name="TrackId">解析対象Trackの永続化ID</param>
/// <param name="Path">解析対象音声ファイルのフルパス</param>
public sealed record TrackQualityAnalysisRequest(long TrackId, string Path);
