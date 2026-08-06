---
name: ship
description: 変更をゲートに通して PR にする。ブランチの取り直し・build/test/format・新規テストの反復・変異検査・PR・auto-merge の判断・subscribe まで。作業が出来上がったときに使う。
---

# PR を出すまで

出来上がった変更を PR にするまでの手順。**順序に意味がある**ので飛ばさない。

## 1. ブランチを取り直す

```bash
git fetch origin main -q && git remote prune origin && git checkout -B <branch> origin/main
```

前の PR がマージ済みなのに、そのブランチの上に積むと**差分が二重になる**（#58 で実際にやった。
squash merge 済みのコミットを含んだまま出して `mergeable_state: behind` になった）。

`prune` が要るのは、マージでリモートブランチが消えると `--force-with-lease` が
`stale info` で弾かれるため（これも実際に踏んだ）。

作業を先に始めてしまっていたら、`git stash push -u` してから取り直して pop する。

## 2. ゲートを通す

3 つとも通す。**1 つでも落ちたら PR を出さない。**

```bash
dotnet build
dotnet test -m:1 -p:CollectCoverage=true   # 分岐 80% を src の各プロジェクトが満たすこと
dotnet format --verify-no-changes
```

`-m:1` は外さない。並列にするとカバレッジのマージ順が崩れて不完全なデータで判定される
（docs/build.md）。

## 3. 新しいテストは 5〜10 回連続で回す

**1 回通っただけでは足りない。** #63 では 5 回中 2 回落ちて、テスト側のバグ
（3 件を並行登録して「配列の 0 番目が Running になる」と決めつけていた。エンジンが拾うのは
登録が最も古いもの）が見つかった。1 回で出していれば CI でフレークしていた。

```bash
for i in $(seq 10); do
  dotnet test tests/<Project>/<Project>.csproj -m:1 --no-build 2>&1 | grep -E "^(Passed!|Failed!)"
done
```

実プロセスを起こすテスト（tests/Resilience、tests/E2E）は特に必ず回す。

## 4. 変異を入れて網が効くことを確かめる

振る舞いを変えた、または新しいテストを足したなら、**そのテストが本当に欠陥を捕まえるか**を見る。
直した defect を製品コードに戻して、対応するテストだけが落ちることを確認し、必ず元へ戻す。

```bash
cp src/<Path>/<File>.cs /tmp/keep          # git checkout で戻すと自分の変更まで消える（実際に消した）
# ここで変異を入れる
dotnet test <proj> --filter "FullyQualifiedName~<TestName>"   # 落ちること
cp /tmp/keep src/<Path>/<File>.cs
```

落ちなければ、そのテストは何も守っていない。

## 5. コミット

コミットログには **Why**（なぜ変えたか。差分からは読めない、変更を必要とした事情）を書く。
What はテスト名が、How はコードが持っている（CLAUDE.md「どこに何を書くか」）。

実測した数字があるなら本文に入れる。後から「なぜこの判断か」を再現できる。

## 6. push

```bash
git push -u origin <branch>
```

失敗したらネットワークの失敗のときだけ 4 回まで指数バックオフ（2s, 4s, 8s, 16s）。

## 7. PR

`.github/pull_request_template.md` を埋める。「確認したこと」には**実測**を書く
（何件通ったか、何回連続で通したか、変異で落ちたか）。「レビューで見てほしい点」には
判断に迷った箇所を書く。無いなら「特になし」。

## 8. auto-merge の判断

有効にしてよいのは、受け入れ条件を満たし、ローカルでゲートが通り、
**設計上の判断を含まない**場合だけ。含むなら有効にせず `needs-review` を付ける。

**迷ったら有効にしない**（マージは戻せない。レビューは後からでもできる）。

## 9. subscribe

`subscribe_pr_activity` を張る。CI が落ちたら診断して修正を push する。
放置して次のタスクへ進まない。

## やらないこと

- `main` への直接 push
- 自前のマージ処理
- 利用者への通知（`send_later` 等）。報告はチャットで行う
