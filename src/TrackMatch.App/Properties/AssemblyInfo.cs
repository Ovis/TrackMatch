using System.Runtime.CompilerServices;

// レビュー省略などApp内部の派生表示ロジックを公開APIへ露出させず回帰テストできるようにする
[assembly: InternalsVisibleTo("TrackMatch.App.Tests")]
