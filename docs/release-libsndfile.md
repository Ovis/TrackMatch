# libsndfile Release同梱確認

TrackMatchのWindows x64 Releaseでは、`NAudio.SoundFile`が必要とするlibsndfileを利用者へ別途インストールさせず、Release ZIPへ同梱する。

## 配布物

- libsndfile 1.2.2 公式Windows x64配布物を使用する
- `sndfile.dll` をTrackMatch本体と同じディレクトリへ配置する
- 取得したZIPはSHA-256を検証する
- `libsndfile-LGPL-2.1.txt` をRelease ZIPへ含める
- 対応する `libsndfile-1.2.2.tar.xz` を同じGitHub Releaseへ添付する

## Release workflowでの検証

Release archive作成前に、少なくとも次のファイルの存在を検証する。

- `TrackMatch.App.exe`
- `TrackMatch.Scanner.exe`
- `sndfile.dll`
- `libsndfile-LGPL-2.1.txt`
- `THIRD_PARTY_NOTICES.md`
- `fpcalc/fpcalc.exe`
- `fpcalc/LICENSE.md`
- `fpcalc/LGPL-2.1.txt`

これにより、開発環境にはlibsndfileが存在するが配布ZIPには含まれない、という回帰をRelease作成前に検出する。
