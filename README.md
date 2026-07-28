# Netsoft.Jobs

Windows ワークステーション上で動作する、長時間実行 Job の共通実行基盤。

現在は開発サイクルの土台のみ。実装は入っていない。

## ビルド

```bash
dotnet build
dotnet test
```

.NET 8 SDK が必要。

## 開発サイクル

依頼 → タスク化 → 実装 → PR → CI → 自動マージ。
詳細は [CLAUDE.md](./CLAUDE.md) を参照。

| ワークフロー | 内容 |
|---|---|
| [`ci.yml`](.github/workflows/ci.yml) | build / test / format 検査 |
| [`auto-merge.yml`](.github/workflows/auto-merge.yml) | CI 成功 + `auto-merge` ラベルで squash merge |

`auto-merge` ラベルが付いていない PR は自動マージされない。
ラベルの意味は [.github/labels.md](.github/labels.md)。
