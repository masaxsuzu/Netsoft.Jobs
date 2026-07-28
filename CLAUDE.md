# Netsoft.Jobs

Windows ワークステーション上で動作する、長時間実行 Job の共通実行基盤。
現在は開発サイクルの土台のみで、実装は入っていない。

## 開発サイクル

このリポジトリは以下のサイクルで進める。Claude はこの手順に従うこと。

```
1. 依頼        利用者が「追加したいこと」を言葉で伝える
2. タスク化    Claude が実装単位に分解し、内容を利用者に確認する
3. 実装        サブエージェントが 1 タスクを実装する
4. PR 作成     1 タスク = 1 PR。auto-merge を有効にするかを判断する
5. CI と修正   CI の結果を見て、失敗なら修正。必要ならレビュー
6. マージ      CI 通過後、ルールセット + auto-merge で自動マージ
```

### 1. 依頼

利用者は自然言語で依頼する。粒度は問わない。

### 2. タスク化

Claude が実装単位へ分解する。**分解した時点で一度利用者に見せ、着手前に合意を取る。**
勝手に実装を始めない。

タスクの粒度は「1 PR で完結し、CI が通る状態になる」こと。
これより大きいものは分割する。分割できないなら、その理由を述べて相談する。

分解の結果は TaskCreate で管理し、進捗を TaskUpdate で更新する。

### 3. 実装

サブエージェントに 1 タスクずつ渡す。渡すときは以下を明記する。

- 何を作るか（受け入れ条件）
- 触ってよいファイルの範囲
- 既存のどの規約に従うか
- **`dotnet build` と `dotnet test` を通すまでがタスク**であること

複数タスクを並行させるのは、ファイルが重ならない場合のみ。

### 4. PR 作成

1 タスク = 1 PR。`.github/pull_request_template.md` の項目を埋める。

PR を作ったら **auto-merge を有効にするかを判断する**。有効にしてよいのは次をすべて満たす場合。

- 受け入れ条件を満たしている
- ローカルで `dotnet build` と `dotnet test` が通っている
- 設計上の判断が含まれない（含むなら有効にせず `needs-review` を付けて人に見せる）

迷ったら有効にしない。人が見て問題なければ後から有効にできるが、
一度マージされたものは戻せない。非対称なので有効にしない側に倒す。

### 5. CI と修正

CI が落ちたら、Claude が原因を診断して修正を push する。
CI の失敗を放置して次のタスクへ進まない。

CI が落ちた原因が自分の変更でない（main が壊れている）場合は、
その旨を PR にコメントして、main の修正を先に行う。

### 6. マージ

リポジトリのルールセットが必須チェックとして CI を要求しており、
auto-merge を有効にした PR は CI 通過後に GitHub が squash merge する。

Claude 側の操作は `enable_pr_auto_merge`（MCP）または `gh pr merge --auto --squash`。
CI の完了を待ってから何かをする必要はない。有効にした時点で GitHub に委ねる。

以前は自前のワークフローでマージしていたが、ルールセットで必須チェックを設定した時点で
ネイティブ auto-merge が CI を待つようになり、二重の仕組みになるため廃止した。

## ビルド

```bash
dotnet build
dotnet test
dotnet format            # 整形。CI では --verify-no-changes で検査される
```

**.NET 10 SDK が必要。** ソリューションが `.slnx`（新形式）で、SDK 9.0.200 より前は読めない。
`global.json` で固定してあるので、古い SDK しか無い環境では `dotnet` 実行時に気づける。
プロジェクトのターゲットは `net8.0` のままなので、実行に必要なランタイムは .NET 8。

## 構成

```
Netsoft.Jobs.slnx                ソリューション（新形式。SDK 9.0.200 以降が必要）
global.json                      使用する SDK を固定する
Directory.Build.props            全プロジェクト共通のビルド設定
src/Directory.Build.props        src 配下に Netsoft.Jobs. の prefix を付ける
src/Core/                        実装（現在は空）→ Netsoft.Jobs.Core
tests/Directory.Build.props      tests 配下に Netsoft.Jobs. と .Tests を付ける
tests/Core/                      テスト → Netsoft.Jobs.Core.Tests
.github/workflows/ci.yml         build / test / format
```

プロジェクト名は `Core` のように短く保ち、`Netsoft.Jobs.` の prefix は
`src` / `tests` 直下の `Directory.Build.props` が付ける。
prefix を各 csproj に書くと、同名の `Core.csproj` が 2 つあるため必ず片方がずれる。
ディレクトリを増やすときも prefix を書かなくてよい。

## 規約

- `TreatWarningsAsErrors` を有効にしている。警告を残さない
- ファイルスコープ名前空間を使う
- テストは日本語のメソッド名で「何を保証するか」を書く
- コメントは「なぜそうしたか」を書く。何をしているかはコードを読めば分かる

## やらないこと

- デプロイ（未定。CI はビルドとテストのみ）
- `main` への直接 push。変更は必ず PR を通す（ルールセットで禁止されている）
- 自前のマージ処理。マージの判断は auto-merge を有効にするかどうかだけ
