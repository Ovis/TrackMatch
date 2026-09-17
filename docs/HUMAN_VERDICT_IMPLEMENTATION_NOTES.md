# Human Verdict 再設計 実装メモ

このブランチでは添付仕様書を正本とし、Global Track Pair に対する人手判断を Human Verdict として扱う。

## 正本モデル

- `NotDuplicate`: `PreferredTrackId = null`
- `ConfirmedDuplicate`: `PreferredTrackId` は Pair A/B のいずれか必須
- Human Verdict は機械解析や派生 Duplicate Group より優先する
- `ConfirmedDuplicate` の推移関係と `NotDuplicate` が矛盾しても Verdict 自体は破棄しない
- 矛盾は `HumanVerdictConflictDetector` で派生 Conflict として検出する

## DB

Schema Version 3 を新Baselineとする。開発中の旧DB Migrationは提供せず、旧DBは削除して再作成する。
`CandidateReviews` / `CandidateReviewHistory` に `PreferredTrackId` を保持する。

## 互換コード

旧3引数 `CandidateReview` コンストラクタは段階移行用であり、新規コードでは `CandidateReviewFactory` または4引数コンストラクタを使用する。
最終的に旧呼び出し箇所をすべて明示Preferredへ切り替えた後、互換コンストラクタを削除する。
