# Netsoft.Jobs

Windows ワークステーション上で動作する、長時間実行 Job の共通実行基盤。
Job を登録し、状態を監視し、キャンセルできる。同時実行数は 1。

## 動かす

2 つのプロセスを立てる。

```bash
cd src/Web && dotnet run   # API + 実行エンジン（先に立てる。既定 :5000）
cd src/Ui  && dotnet run   # 画面（既定 :5100）
```

ブラウザで <http://localhost:5100> を開く。既定値どうしが噛み合っているので、これだけで動く。

- **画面（Ui）を落としても実行中の Job は死なない。** それがプロセスを分けている理由。
  逆に Web を落とすと Job も止まる（次の起動で `Failed` として閉じられる）
- Ui を先に立てると API が居ない間はエラーが出るが、API が上がれば自動的に復旧する
- DB は Web 側だけが持つ（既定 `src/Web/jobs.db`。`Jobs__DatabasePath` で変更）
- ポートは `ASPNETCORE_URLS` か `--urls` で、Ui の向き先は `Ui__ApiBaseUrl` で上書きする

## ビルド

```bash
dotnet build
dotnet test
```

**.NET 10 SDK が必要**（`global.json` で固定）。カバレッジ基準や CI の詳細は [docs/build.md](./docs/build.md)。

## 開発

開発サイクル・規約は [docs/](./docs/) にある。入口は [CLAUDE.md](./CLAUDE.md)。
