namespace TrackMatch.Core.Candidates;

/// <summary>Human Verdictを確定した操作元Libraryの監査情報を表す。</summary>
public sealed record HumanVerdictSource(long? LibraryId, string? LibraryNameSnapshot)
{
    /// <summary>削除済みLibraryでもSnapshotが残れば出所表示できる。</summary>
    public bool HasDisplaySource => !string.IsNullOrWhiteSpace(LibraryNameSnapshot);
}
