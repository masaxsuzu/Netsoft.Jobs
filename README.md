# Netsoft.Jobs

Windows ワークステーション上で動作する、長時間実行 Job の共通実行基盤。

現在は開発サイクルの土台のみ。実装は入っていない。

## ビルド

```bash
dotnet build
dotnet test
```

**.NET 10 SDK が必要。** ソリューションが `.slnx`（新形式）のため。
プロジェクトのターゲットは `net8.0` なので、実行に必要なランタイムは .NET 8。

## 開発サイクル

依頼 → タスク化 → 実装 → PR → CI → 自動マージ。
詳細は [CLAUDE.md](./CLAUDE.md) を参照。

| 仕組み | 内容 |
|---|---|
| [`ci.yml`](.github/workflows/ci.yml) | build / test / format 検査 |
| ルールセット | `main` への直接 push を禁止し、CI を必須チェックにする |
| auto-merge | 有効にした PR を、CI 通過後に GitHub が squash merge する |

auto-merge を有効にしていない PR は自動マージされない。
