namespace TrackMatch.Core.Trash;

/// <summary>
/// Reject Trackのファイル操作を抽象化する。
/// </summary>
public interface ITrackFileOperations
{
    bool FileExists(string path);

    void Move(string sourcePath, string destinationPath);
}
