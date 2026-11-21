# ADR 0001: [決定事項の短いタイトル]

## Status
Accepted (または Proposed, Deprecated など)

## Date
YYYY-MM-DD

## Context (背景と課題)
- 何が問題だったのか、どのような技術的制約があったのかを簡潔に記載。
- [例: LauncherItemがUIの座標やAvaloniaのIImageに依存しており、保守性とシリアライズに問題が生じていた。]

## Decision (決定事項)
- どのように解決することにしたのかを記載。
- [例: LauncherItemを純粋なPOCOに変更し、UIの関心事は新設するLauncherItemViewModelにカプセル化する。]

## Considered Options (検討して却下した選択肢) ※ここがAIへの重要なガードレール
- [例: 既存のクラスにDTO（Data Transfer Object）を追加する案: 変換処理が冗長になるため却下。]

## Consequences (結果・影響)
- この決定によってどのような影響（良い点・悪い点）が生じるか。
- [例: View層とのバインディングはすべてViewModel経由に書き換える必要がある。Modelのテストは容易になる。]
