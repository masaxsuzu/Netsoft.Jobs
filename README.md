# Netsoft.Jobs

Windows ワークステーション上で動作する、長時間実行 Job の共通実行基盤。
Job を登録し、状態を監視し、一時停止・再開・編集・キャンセルできる。

**同時に走る Job は 1 つ**（実行エンジンのループが 1 本）。
**同じ DB を使う実行ホスト（Web）も 1 つだけ** ── 別々の制約なので
[docs/operating.md](./docs/operating.md) を読むこと。

## 動かす

2 つのプロセスを立てる。

```bash
cd src/Web && dotnet run   # API + 実行エンジン（先に立てる。既定 :5000）
cd src/Ui  && dotnet run   # 画面（既定 :5100）
```

ブラウザで <http://localhost:5100> を開く。既定値どうしが噛み合っているので、これだけで動く。

- **同じ DB を使う Web は 1 つだけ立てる。** 2 つ目を立てると、その起動時復旧が
  1 つ目の実行中の Job を「前回の死骸」とみなして `Failed` で閉じる（[docs/operating.md](./docs/operating.md)）
- **画面（Ui）を落としても実行中の Job は死なない。** それがプロセスを分けている理由。
  逆に Web を落とすと Job も止まる（次の起動で `Failed` として閉じられる）
- Ui を先に立てると API が居ない間はエラーが出るが、API が上がれば自動的に復旧する
- DB は Web 側だけが持つ（既定 `src/Web/jobs.db`。`Jobs__DatabasePath` で変更）
- ポートは `ASPNETCORE_URLS` か `--urls` で、Ui の向き先は `Ui__ApiBaseUrl` で上書きする
- Ui は何個立ててもよい（DB を触らないため）

## ビルド

```bash
dotnet build
dotnet test
```

**.NET 10 SDK が必要**（`global.json` で固定）。カバレッジ基準や CI の詳細は [docs/build.md](./docs/build.md)。

## 開発

開発サイクル・規約は [docs/](./docs/) にある。入口は [CLAUDE.md](./CLAUDE.md)。
