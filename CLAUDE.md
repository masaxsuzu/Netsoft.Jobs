# Netsoft.Jobs

Windows ワークステーション上で動作する、長時間実行 Job の共通実行基盤。
Job の登録・監視・キャンセルができる。動かし方は [README](./README.md) を参照。

## 開発サイクル

1. **依頼** — 利用者が自然言語で伝える。粒度は問わない
2. **タスク化** — Claude が実装単位に分解し、**着手前に利用者の合意を取る**。勝手に始めない。
   粒度は「1 PR で完結し CI が通る」。TaskCreate / TaskUpdate で管理する
3. **実装** — サブエージェントに 1 タスクずつ渡す。受け入れ条件・触ってよいファイルの範囲・
   従う規約・**build と test を通すまでがタスク**であることを明記する。
   並行させるのはファイルが重ならない場合のみ
4. **PR** — 1 タスク = 1 PR。`.github/pull_request_template.md` を埋める
5. **CI と修正** — 落ちたら診断して修正を push。放置して次のタスクへ進まない。
   原因が main 側なら PR にコメントして main を先に直す
6. **マージ** — ルールセットが CI を必須チェックにしており、auto-merge を有効にした PR は
   CI 通過後に GitHub が squash merge する

auto-merge を有効にしてよいのは、受け入れ条件を満たし、ローカルで build / test が通り、
**設計上の判断を含まない**場合だけ。含むなら有効にせず `needs-review` を付けて人に見せる。
**迷ったら有効にしない**（マージは戻せない。レビューは後からでもできる）。

## ビルド

```bash
dotnet build
dotnet test
dotnet format            # CI では --verify-no-changes で検査される
dotnet test -p:CollectCoverage=true   # カバレッジ計測 + 基準判定。CI の Test はこれ
```

- **.NET 10 SDK が必要**（`global.json` で固定）。ターゲットは `net10.0`、C# 14
- カバレッジ基準は**行 90% / ブランチ 80%（全体）**。テストが層をまたぐため全体で測る。
  全プロジェクトの結果を 1 ファイルにマージし、**最後に走る tests/Web が判定する**。
  順序は `-m:1`（直列）で `.slnx` の並び順に固定してある
- **テストプロジェクトを増やすときは `.slnx` で tests/Web より前に置くこと**
- カバレッジの設定は `tests/Directory.Build.props`（計測と除外）と `tests/Web/Web.csproj`（基準）
- 除外してよいのは「E2E が実プロセスで検証しているが coverlet が計測できないもの」だけ。
  テストを書けるものを除外で隠さない

## 構成

- `src/`: `Domain`（依存ゼロ）/ `Contracts`（Domain のみ。プロセスをまたぐ契約）/
  `Features` / `Infrastructure` / `Web`（API + 実行エンジン）/ `Ui`（画面）
- `tests/`: src と対応。`E2E` は Playwright で 2 プロセスを実起動する
- アセンブリ名の `Netsoft.Jobs.` prefix は `src` / `tests` 直下の `Directory.Build.props` が
  付ける。**各 csproj には書かない**（同名 csproj が複数あるため必ず片方がずれる）

## 規約

- `TreatWarningsAsErrors`。警告を残さない
- ファイルスコープ名前空間を使う
- テストは日本語のメソッド名で「何を保証するか」を書く
- コメントは「なぜそうしたか」を書く。何をしているかはコードを読めば分かる

## やらないこと

- `main` への直接 push。変更は必ず PR を通す（ルールセットで禁止されている）
- 自前のマージ処理。マージの判断は auto-merge を有効にするかどうかだけ
- 利用者への通知（send_later 等）。報告はチャットで行う
- デプロイ（未定。CI はビルドとテストのみ）
