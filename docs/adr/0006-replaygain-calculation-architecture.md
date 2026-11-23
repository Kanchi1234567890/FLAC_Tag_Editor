# ADR 0006: FFmpeg 外部プロセス連携による ReplayGain 計算・埋め込みアーキテクチャ

## Status
Accepted

## Date
2026-11-23

## Context (背景と課題)
- Phase 5 Step 5-2 として、FFmpeg 外部プロセスを用いてトラック単位およびアルバム単位の ReplayGain（`REPLAYGAIN_TRACK_GAIN` / `REPLAYGAIN_TRACK_PEAK` / `REPLAYGAIN_ALBUM_GAIN` / `REPLAYGAIN_ALBUM_PEAK`）を算出し、FLAC タグへ埋め込む機能を実装する必要がある。
- FFmpeg 標準組み込みの `replaygain` フィルタは単一のオーディオストリームに対して `track_gain` と `track_peak` のみを出力するため、複数トラックで構成されるアルバム単位の `album_gain` / `album_peak` を安全かつ大量ファイルや異フォーマット混在にも対応できる形で計算する設計が必要である。
- FFmpeg 実行ファイル（`ffmpeg.exe`）の配置はユーザー環境依存であるため、自動検出とカスタムパス設定（`settings.toml` 永続化）・事前検証の仕組みが必要である。
- 本アプリでは既存の独自タグ定義・動的列（`CustomTags`）および「編集結果はまずメモリ上の未保存変更として反映し、確認後に一括保存・破棄できる」編集フローと、保存時の再生ファイルロック自動退避／復元機構を採用しており、これらと矛盾なく統合する必要がある。

## Decision (決定事項)
- **解析サービスの抽象化と FFmpeg 実行方式 (`IReplayGainService` / `ReplayGainService`)**:
  - `IReplayGainService` インターフェースを定義し、DI コンテナへ Singleton 登録する。外部プロセス実行には .NET 標準の `System.Diagnostics.Process`（`ArgumentList` 使用・stdout/stderr 並列非同期読み取り・`CancellationToken` 連動による `Kill(entireProcessTree: true)`）を使用する。
  - 解析フィルタには FFmpeg 標準組み込みの **`-af replaygain`** を採用する（`-hide_banner -nostdin -nostats -loglevel info -vn -sn -dn` で音声のみ解析し、stderr の `track_gain` / `track_peak` をコンパイル済み正規表現で抽出）。
  - **アルバム計算方式**:
    - グループ内の成功トラックが 1 曲のみの場合はトラック解析結果をそのままアルバム値として再利用し、追加プロセスを起動しない。
    - 2 曲以上の場合は、全トラックの `SampleRate` / `Channels` / `BitsPerSample` が一致していれば一時 `ffconcat` リストファイルを用いた `-f concat -safe 0` 連結で `replaygain` フィルタを実行する（Windows のコマンドライン引数長制限とファイルハンドル枯渇を回避）。
    - グループ内に異フォーマット（サンプルレートやチャンネル数の違い等）が混在している場合、または `concat` デマルチプレクサが失敗した場合は、一時ファイルによる `-filter_complex_script`（各ストリームを `aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo` で正規化してから `concat` → `replaygain`）へ自動フォールバックする。
    - `album_peak` はリサンプルによるピーク変動を避けるため、グループ内全トラックの個別 `track_peak` の最大値を採用する。
  - 作成した一時ファイル（`ffconcat` リストおよびフィルタスクリプト）は `finally` で確実に削除する。
- **計算モードとアルバムグルーピング基準**:
  - 計算モードは「トラック + アルバム（既定）」「トラックのみ」の 2 種類を提供する。
  - アルバムグルーピング基準は以下の 3 種類から選択可能とする。
    1. タグ基準（`Album` + `AlbumArtist`、ただし `Album` が空のトラックはフォルダ単位へフォールバック・既定）
    2. フォルダ単位（同一ディレクトリを 1 アルバムとみなす）
    3. 単一アルバム扱い（対象トラックすべてを 1 アルバムとして計算）
- **タグ格納方式と適用・保存フロー**:
  - `REPLAYGAIN_TRACK_GAIN` / `REPLAYGAIN_TRACK_PEAK` / `REPLAYGAIN_ALBUM_GAIN` / `REPLAYGAIN_ALBUM_PEAK` は標準予約キーへ変更せず、既存の **Vorbis Comment 独自タグ基盤 (`CustomTags`)** へ標準書式（Gain: `+0.00 dB` / `-6.42 dB`、Peak: `0.987654`）で書き込む。
  - 対象キーが `custom_tags.toml` に定義済みであれば該当フィールド（および DataGrid 動的列）を更新し、未登録であれば「未登録の独自タグ」として更新／追加する。
  - 専用モーダルダイアログ（`ReplayGainDialog`）上で FFmpeg 検出・オプション設定・進捗表示・キャンセル・計算結果プレビューを提供し、確定時は既定で「未保存変更として適用」、任意チェックで「適用後に自動保存（既存 `MainWindowViewModel.SaveTracksCoreAsync` 経由により再生中ファイルのロック退避／復元も自動連携）」を選択可能とする。
  - ReplayGain タグのクリア機能は `REPLAYGAIN_` プレフィックス一致（大文字小文字無視）の Vorbis Comment キーを対象とし、定義済み独自タグは定義を維持したまま空欄化（保存時削除）、未登録独自タグは項目ごと削除する。ツールバー `SplitButton` ドロップダウン（選択中／全トラック）、DataGrid 右クリックメニュー（選択中）、および `ReplayGainDialog` 内（即時保存オプション連動）から操作可能とする。
- **FFmpeg パス検出と設定永続化**:
  - `AppSettings.ReplayGain`（`ReplayGainSettings.FfmpegPath`）を `settings.toml` に永続化する。
  - 明示パス未設定時は、アプリ実行フォルダ配下（`ffmpeg.exe`、`ffmpeg/ffmpeg.exe`、`ffmpeg/bin/ffmpeg.exe`）→ 環境変数 `PATH` の順で自動検出し、`ffmpeg -version`（タイムアウト付き）で動作検証を行う。

## Considered Options (検討して却下した選択肢)
- `ebur128` フィルタ（ReplayGain 2.0 / EBU R128）を初期実装にする案: アルバム統合ラウドネス計算にも同様に連結が必要であり、サマリー出力形式のパースがバージョン差異の影響を受けやすい。まずは FFmpeg で広く安定している `replaygain` フィルタを採用し、必要であれば将来アルゴリズム選択肢として拡張する。
- `REPLAYGAIN_*` キーを `FlacTrack` の専用標準プロパティおよび予約キーへ昇格させる案: すでに `custom_tags.toml` で ReplayGain タグを動的列定義している運用と競合し、列設定・検索・TSV 貼り付け等への影響範囲が広すぎるため却下。
- 計算完了と同時に確認なしで FLAC ファイルへ即時上書き保存する案: 本アプリの「変更はまずメモリ上へ反映し、確認後に保存／破棄できる」安全思想に反するため却下。プレビュー確認後に適用または適用＋保存を選べる方式を採用。
- 外部プロセス実行ライブラリ（`CliWrap` 等）を追加導入する案: .NET 10 標準の `System.Diagnostics.Process` と `WaitForExitAsync` で十分に非同期制御・キャンセル・プロセスツリー強制終了を実現できるため、依存関係最小化のため却下。

## Consequences (結果・影響)
- 良い点: 追加の NuGet パッケージなしで軽量に実装でき、既存の独自タグ・動的列・未保存状態管理・再生中ファイルロック回避機構とシームレスに連携する。大量ファイルや異フォーマット混在アルバムでも安定してトラック／アルバム ReplayGain を算出できる。
- 注意点: 実行にはユーザー環境に `ffmpeg.exe` が必要となる（未検出時はダイアログ内に状態と参照ボタンを表示して誘導する）。アルバム計算を含む場合は最大 2 パス（トラック解析＋アルバム連結解析）でファイルを読み取るため、進捗表示とキャンセル機能により操作性を確保する。
