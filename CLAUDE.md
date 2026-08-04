# Netsoft.Jobs

Windows ワークステーション上で動作する、長時間実行 Job の共通実行基盤。
動かし方は [README](./README.md)。開発の決めごとは `docs/` にあり、**作業を始める前に読むこと**。

## ディレクトリ構造

- `src/Domain` — エンティティと状態遷移。依存ゼロ
- `src/Contracts` — プロセスをまたぐ契約。Domain のみ参照
- `src/Features` — コマンドとエンドポイント。機能ごとのフォルダ
- `src/Infrastructure` — SQLite
- `src/Web` — API + 実行エンジンのホスト
- `src/Ui` — 画面（Blazor Server）のホスト
- `tests/` — src と対応。`E2E` は 2 プロセスを実起動する
- `docs/` — 開発の決めごと

## docs

- [development-cycle.md](./docs/development-cycle.md) — 開発サイクル・auto-merge の判断・やらないこと
- [build.md](./docs/build.md) — ビルド・テスト・カバレッジの規則
- [conventions.md](./docs/conventions.md) — コーディング規約と命名
