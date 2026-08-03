# Netsoft.Jobs

Windows ワークステーション上で動作する、長時間実行 Job の共通実行基盤。

Job を登録し、状態を監視し、キャンセルできる。同時実行数は 1。

## 動かす

**2 つのプロセスを立てる。** ターミナルを 2 つ開いて、それぞれで実行する。

```bash
# 1. API + 実行エンジン（先に立てる）
cd src/Web && dotnet run

# 2. 画面
cd src/Ui && dotnet run
```

ブラウザで <http://localhost:5100> を開く。

| | 役割 | 既定のポート |
|---|---|---|
| `src/Web` | HTTP API・実行エンジン・SQLite | 5000 |
| `src/Ui` | 画面（Blazor Server） | 5100 |

Ui は `Ui:ApiBaseUrl`（既定 `http://localhost:5000`）で API を見つける。
素の既定値どうしが噛み合っているので、上の 2 コマンドだけで動く。

ポートを変えるときは `ASPNETCORE_URLS` か `--urls` で上書きする。
Ui の向き先を変えるときは `Ui__ApiBaseUrl` を渡す。

```bash
cd src/Ui && Ui__ApiBaseUrl=http://localhost:6000 dotnet run --urls=http://localhost:6100
```

### なぜ 2 つに分かれているか

**長時間実行 Job を走らせたまま、画面を再起動・更新できるようにするため。**
Job を実行しているのは `src/Web` だけなので、`src/Ui` を落としても実行中の Job は死なない。

逆に `src/Web` を落とすと Job も止まる（次の起動で、走っていた Job は結果が
分からないものとして `Failed` で閉じられる）。実行を所有するプロセスは 1 つだけ、
という前提はこの分け方でも保たれている。

### 起動の順序

**API（`src/Web`）を先に立てる。** 逆でも壊れないが、画面を先に立てると
API が居ない間は一覧の取得に失敗し、画面にエラーが出る。API が上がれば
変更通知の再接続が成功して自動的に復旧するので、待てば直る。

### データの置き場

DB は API 側のプロセスだけが持つ。`Jobs:DatabasePath`（既定 `jobs.db`）は
コンテンツルート基準なので、上の手順なら `src/Web/jobs.db` にできる。
`dotnet run` をどこから叩いても同じ場所になる（カレントディレクトリ基準にすると
叩いた場所ごとに別の DB ができてしまうため）。

## ビルド

```bash
dotnet build
dotnet test
```

**.NET 10 SDK が必要。** ターゲットは `net10.0`、言語バージョンは C# 14。

## 開発サイクル

依頼 → タスク化 → 実装 → PR → CI → 自動マージ。
詳細は [CLAUDE.md](./CLAUDE.md) を参照。

| 仕組み | 内容 |
|---|---|
| [`ci.yml`](.github/workflows/ci.yml) | build / test / format 検査 |
| ルールセット | `main` への直接 push を禁止し、CI を必須チェックにする |
| auto-merge | 有効にした PR を、CI 通過後に GitHub が squash merge する |

auto-merge を有効にしていない PR は自動マージされない。
