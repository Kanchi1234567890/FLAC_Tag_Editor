# ADR 0004: 左ペイン DataGrid への独自タグ動的列追加・複数値展開ルールと列設定保持／クリーンアップ

## Status
Accepted

## Date
2026-11-23

## Context (背景と課題)
- ADR 0003 で導入した独自タグ定義カタログ（`custom_tags.toml`）により、右ペインおよび検索・置換で独自タグを統一的に扱えるようになった一方、左ペインのトラック一覧 DataGrid では標準列（固定 13 列）のみが表示可能であり、独自タグの一覧比較・インライン編集・ソート・TSV 一括貼り付けができなかった。
- 従来の列識別は `TrackGridColumnKind`（enum）のみで管理されており、実行時に増減する独自タグ列を識別・永続化できなかった。また Vorbis Comment のキー名はスペースや記号を含み得るため、文字列パスベースのバインディングでは構文衝突のリスクがある。
- ADR 0003 の flatten ルールでは、定義済み独自タグを編集すると複数値が単一値へ集約される仕様だったが、`"; "` 区切りで複数値を直接入力・編集・貼り付けしたいという要件がある。
- 独自タグ定義を一時的に削除したあとに再登録した場合、以前調整した列の表示状態・並び順・幅が消えてしまうのは不便である一方、不要な削除済み設定が `settings.toml` に溜まり続けるのを整理できる手段も求められる。

## Decision (決定事項)
- **列識別体系と永続化キー（`Models/TrackGridColumnKind.cs`）**:
  - `TrackGridColumnKind.CustomTag` および論理列 ID `TrackGridColumnId(Kind, CustomTagKey)`（独自タグキーは `OrdinalIgnoreCase` で等価比較）を導入する。
  - `settings.toml` の列キーは、標準列は既存のキー（`"Title"`, `"Artist"` 等）を維持し、独自タグ列は `"Custom:{KEY}"` プレフィックス形式で永続化する。新規登録時の既定状態は非表示・幅 150px・末尾配置とする。
  - `TsvPasteRequest.OrderedColumns` および `TrackItemViewModel.ApplyGridCellValue` を `TrackGridColumnId` ベースへ拡張し、独自タグ列を含む TSV 貼り付けに対応する。
- **動的列の生成・バインディング・ソート（`Views/DataGridColumnSettingsBehavior.cs`）**:
  - 固定 13 列は XAML に残し、独自タグ列は `DataGridColumnSettingsBehavior` 内で動的に `DataGridTextColumn` を生成・ヘッダ更新（Label 未設定時は Key）・破棄する。
  - バインディングには `CompiledBindingPathBuilder` + `ClrPropertyInfo`（`TrackItemViewModel.GetCustomTagValue` / `SetCustomTagValue` を直接呼び出す TwoWay バインディング）を採用し、Vorbis Comment キーの文字種に依存しない型安全・AOT 安全な双方向同期を実現する。変更通知は仮想プロパティ名 `CustomTagColumn:{KEY_UPPER}` 経由で左右ペインおよび検索バーとリアルタイム連動させる。
  - ソートは `DataGridColumn.CustomSortComparer`（`StringComparer.CurrentCultureIgnoreCase`）で行い、列削除時は `DataGridComparerSortDescription` / `PropertyPath` の双方を確認してソート条件を安全に除去する。
- **独自タグ複数値の編集・保存ルール（ADR 0003 の flatten ルール拡張）**:
  - `CustomTagFieldViewModel.Flatten()` において、未編集（読込時の `"; "` 連結値と一致）の場合は元のエントリ群を非破壊維持し、空欄は削除とする。
  - 編集後の値が `"; "`（セミコロン＋半角スペース）を含む場合は分割（空要素除外・前後空白トリム）して複数の `CustomTagEntry` として保存する。`"; "` を含まない場合は単一値として保存する。右ペインにはリアルタイムで展開件数を示す複数値バッジを表示する。
- **削除済み独自タグ列設定の保持とクリーンアップ（`TrackGridColumnSettingsViewModel` / `CustomTagManagerDialog`）**:
  - 独自タグ定義がカタログから削除されても、`settings.toml` 内の `Custom:{KEY}` 設定は退避・保持し、同じキーを再登録した際に前回の表示状態・並び順・幅を復元できるようにする。
  - 独自タグ管理ダイアログ（`CustomTagManagerDialog`）に、現在の定義一覧に含まれない未使用の独自タグ列設定件数をリアルタイム表示し、「確定時に未使用の列設定をクリーンアップ」チェックボックスで `settings.toml` から明示的に削除できる機能を提供する。

## Considered Options (検討して却下した選択肢)
- インデクサ文字列バインディング（例: `CustomTags[KEY]`）で動的列を構成する案: Vorbis Comment キーは `]` や `.` やスペースなどを含む可能性があり、バインディングパスパーサとの衝突リスクがあるため却下。
- 独自タグ定義の削除時に `settings.toml` の列設定を自動削除する案: 定義の一時削除・再登録時に列幅や配置を再設定する手間が発生するため却下（保持＋明示クリーンアップ方式を採用）。
- すべての `;`（スペースなし含む）で複数値分割する案: 単一値内に含まれるセミコロンを意図せず分割してしまうため、表示時の連結区切りと一致する `"; "` のみを複数値区切りとした。

## Consequences (結果・影響)
- 良い点: 独自タグを左ペイン DataGrid 上で標準タグ同様に一覧閲覧・インライン編集・ソート・TSV 一括貼り付けでき、複数値（`"; "` 区切り）の直接編集にも対応する。削除済み列設定の復元性と、`settings.toml` の肥大化防止（クリーンアップ）を両立できる。
- 注意点: 単一の値そのものに `"; "` を含めて新規入力・編集したい場合は複数値として分割される（未編集の既存単一値のみ非破壊保持される）。起動時は `CustomTagCatalog.LoadAsync()` を `ColumnSettings.LoadAsync()` より先に完了させる順序を維持すること。
