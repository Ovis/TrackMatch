# Third-party notices

TrackMatchのWindows向けGitHub Releaseには、AcoustIDプロジェクトが配布するChromaprint 1.6.1の`fpcalc.exe`を、改変せずに同梱する。

## Chromaprint / fpcalc

- Component: Chromaprint / fpcalc 1.6.1
- Upstream: https://github.com/acoustid/chromaprint
- Binary asset: `chromaprint-fpcalc-1.6.1-windows-x86_64.zip`
- Binary asset SHA-256: `735d6182b38e9f364b84ce6f4ccd682c75e2851de89735711d6b762d12b92a4e`
- TrackMatchによる変更: なし

Chromaprint自身のソースコードはMIT Licenseで提供されるが、Chromaprint 1.6.1の公式`LICENSE.md`では、含まれるFFmpeg由来コードのためプロジェクト全体をLGPL 2.1として扱うよう案内されている。

TrackMatchは`fpcalc.exe`をTrackMatch本体へリンクせず、独立した外部プロセスとして起動する。TrackMatch本体と`fpcalc.exe`は別個の実行ファイルとして配布する。

## 公式Windowsバイナリのビルド構成

Chromaprint 1.6.1の公式Windows x86-64パッケージは、Chromaprintの`package/build.sh`により次の構成で生成されている。

- Chromaprint: 1.6.1
- FFmpeg: 8.0
- FFmpeg build tag: `acoustid/ffmpeg-build` の `v8.0-1`
- Target: `x86_64-w64-mingw32`
- Chromaprint: `BUILD_SHARED_LIBS=OFF`
- FFmpeg: static build (`--disable-shared --enable-static`)
- FFmpegのGPL専用・nonfreeコンポーネントを有効にする`--enable-gpl` / `--enable-nonfree`は使用されていない

この構成ではFFmpegが`fpcalc.exe`へ静的リンクされるため、TrackMatchのReleaseではLGPL 2.1に基づく再配布条件を満たせるよう、対応するソースコードとビルドスクリプトも同じReleaseから取得できるようにする。

## Releaseに含めるライセンスと対応ソース

Windows向けTrackMatch ZIPには次を収録する。

- `THIRD_PARTY_NOTICES.md`（この文書）
- `fpcalc/LICENSE.md`（Chromaprint 1.6.1公式ライセンス文書）
- `fpcalc/LGPL-2.1.txt`（GNU LGPL 2.1全文）

さらに、同じGitHub Releaseへ次の対応ソースを別ファイルとして添付する。

- `chromaprint-1.6.1.tar.gz` — 同梱`fpcalc.exe`に対応するChromaprintソース。`fpcalc`自身のソースと公式パッケージ生成スクリプトを含む
- `ffmpeg-8.0.tar.xz` — 静的リンクされたFFmpeg 8.0のソース
- `ffmpeg-build-v8.0-1.tar.gz` — AcoustIDがFFmpeg 8.0バイナリを生成するために使用したビルドスクリプト

これらは、TrackMatchが再配布する公式`fpcalc.exe`に対応するソースとビルド情報を、バイナリと同じ配布場所から取得可能にするためのものである。

TrackMatchはこれら第三者コンポーネントを改変していない。各コンポーネントにはそれぞれのライセンス条件が適用され、TrackMatch本体のライセンスをそれらへ置き換えるものではない。
