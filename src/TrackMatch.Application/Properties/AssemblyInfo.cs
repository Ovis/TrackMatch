using System.Runtime.CompilerServices;

// 品質解析の世代所有権などApplication内部の並行制御を、公開APIへ露出させず回帰テストできるようにする
[assembly: InternalsVisibleTo("TrackMatch.App.Tests")]
