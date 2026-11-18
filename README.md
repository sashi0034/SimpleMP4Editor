# Simple MP4 Editor

MP4・WebM の映像範囲と時間区間を選び、1本の MP4 に保存する Windows x64 用の WPF アプリです。

## 起動

GitHub の **Releases** でリリースを開き、**Assets** から **SimpleMP4Editor-win-x64.zip** をダウンロードします。ZIP をフォルダーに展開し、SimpleMP4Editor.exe を起動してください。.NET、FFmpeg、ffprobe は配布物に含まれます。

## 操作

1. MP4 または WebM をウィンドウにドロップします。両形式を混ぜて追加でき、リストの上から順につながります。WebM の初回再生時は、再生用の一時動画を準備します。
2. 残したい範囲に合わせてプレビューの辺または角をつまみます。枠内をドラッグすると移動、枠外からドラッグすると再選択できます。選択した辺は矢印で1ピクセル、Shift＋矢印で10ピクセル調整できます。「対称に調整」をオンにすると、向かい合う辺が同じ距離だけ動きます。
3. シークバーの両端をドラッグして残す区間を選びます。現在位置を始点にするには I、終点にするには O を押します。Space で選択区間を再生・一時停止します。
4. 同じ動画の別区間を使うには「複製」します。↑・↓で順番を変え、「削除」でリストから外せます。クリップ間は自動で詰まります。
5. 「仕上がりを表示」で選択中のクリップの出力画角を確認します。結合サイズは先頭の切り抜き範囲から決まり、「画面を埋める」は中央を基準に拡大縮小、「全体を収める」は黒帯で調整します。
6. 「書き出す…」で保存します。出力は MP4（H.264、音声がある場合は AAC）。互換性のため縦横を偶数にそろえ、各区間の長さを先頭素材のフレームレートに合わせます。

## ローカル開発

Visual Studio 2026 の「.NET デスクトップ開発」と .NET 10 SDK を使います。SimpleMP4Editor.slnx を開き、SimpleMP4Editor をスタートアッププロジェクトにして F5 で起動します。初回ビルド時に固定版 FFmpeg をダウンロードし、SHA-256 を照合します。以降は作業フォルダー内のキャッシュを使います。

~~~powershell
dotnet build SimpleMP4Editor.slnx -c Release
dotnet run --project tests/EditorChecks -c Release --no-build
dotnet publish src/SimpleMP4Editor -c Release -r win-x64 --self-contained true -o .artifacts/publish
~~~

各 push でビルド・テストを実行し、成功すると配布用 ZIP を Actions の Artifacts に保存します。デフォルトブランチへの push では、同じ ZIP を GitHub Releases にも公開します。タグは `build-<実行番号>` の形式で自動作成されます。

FFmpeg の配布元、ソースコード情報、ライセンスは同梱の tools/README.txt と tools/LICENSE.txt にあります。
