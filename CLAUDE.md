# Netsoft.Jobs

Windows ワークステーション上で動作する、長時間実行 Job の共通実行基盤。
現在は開発サイクルの土台のみで、実装は入っていない。

## 開発サイクル

このリポジトリは以下のサイクルで進める。Claude はこの手順に従うこと。

```
1. 依頼        利用者が「追加したいこと」を言葉で伝える
2. タスク化    Claude が実装単位に分解し、内容を利用者に確認する
3. 実装        サブエージェントが 1 タスクを実装する
4. PR 作成     1 タスク = 1 PR。auto-merge ラベルを付けるかを判断する
5. CI と修正   CI の結果を見て、失敗なら修正。必要ならレビュー
6. マージ      CI 通過 + auto-merge ラベルで自動マージ
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

`auto-merge` ラベルを付けてよいのは、次をすべて満たす場合。

- 受け入れ条件を満たしている
- ローカルで `dotnet build` と `dotnet test` が通っている
- 設計上の判断が含まれない（含むなら `needs-review` を付けて人に見せる）

迷ったら付けない。ラベルの詳細は `.github/labels.md`。

### 5. CI と修正

CI が落ちたら、Claude が原因を診断して修正を push する。
CI の失敗を放置して次のタスクへ進まない。

CI が落ちた原因が自分の変更でない（main が壊れている）場合は、
その旨を PR にコメントして、main の修正を先に行う。

### 6. マージ

`.github/workflows/auto-merge.yml` が CI 成功を確認してから squash merge する。
GitHub ネイティブの auto-merge は使わない（理由はワークフローのコメント参照）。

## ビルド

```bash
dotnet build
dotnet test
dotnet format            # 整形。CI では --verify-no-changes で検査される
```

.NET 8 SDK が必要。

## 構成

```
Netsoft.Jobs.sln
Directory.Build.props            全プロジェクト共通のビルド設定
src/Netsoft.Jobs.Core/           実装（現在は空）
tests/Netsoft.Jobs.Core.Tests/   テスト
.github/workflows/ci.yml         build / test / format
.github/workflows/auto-merge.yml CI 成功後の自動マージ
```

## 規約

- `TreatWarningsAsErrors` を有効にしている。警告を残さない
- ファイルスコープ名前空間を使う
- テストは日本語のメソッド名で「何を保証するか」を書く
- コメントは「なぜそうしたか」を書く。何をしているかはコードを読めば分かる

## やらないこと

- デプロイ（未定。CI はビルドとテストのみ）
- `main` への直接 push。変更は必ず PR を通す
