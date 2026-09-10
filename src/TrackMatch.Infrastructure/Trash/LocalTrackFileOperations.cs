using TrackMatch.Core.Trash;

namespace TrackMatch.Infrastructure.Trash;

/// <summary>
/// ローカルファイルシステム上でReject TrackをTrashへ移動する。
/// </summary>
public sealed class LocalTrackFileOperations : ITrackFileOperations
{
    public bool FileExists(string path) => File.Exists(path);

    public void Move(string sourcePath, string destinationPath)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Trash移動先ディレクトリを解決できない。");
        Directory.CreateDirectory(destinationDirectory);

        // 既存ファイルは上書きしない。事前確認後に競合が発生した場合もFile.Move側で失敗させる。
        File.Move(sourcePath, destinationPath, overwrite: false);
    }
}
