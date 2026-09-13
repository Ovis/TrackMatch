# Global Track / Library比較スコープ再設計 実装メモ

本ブランチでは、2026-09-14のdig確定仕様に基づき、TrackをLibrary所有から切り離して正規化物理Path単位のGlobal Trackへ移行する。

## 実装原則

- Global TrackのIdentityは正規化物理Path
- Library所属は`LibraryTracks` Membershipとして永続化
- Membershipは正常なRoot Scanで確立する
- MissingはGlobal Track状態であり、Membershipは維持する
- 異なるLibrary間のRoot完全一致・包含は許可し、同一Library内だけ禁止する
- Fingerprint / Quality / Machine Comparison / Human Duplicate VerdictはGlobalに共有する
- KeepはLibrary固有状態として扱う
- 旧DB Migration/Compatibilityは実装しない
- Schema Version 2を新Baselineとする

この文書は実装補助であり、Product Semanticsの正本はdig決定記録とする。
