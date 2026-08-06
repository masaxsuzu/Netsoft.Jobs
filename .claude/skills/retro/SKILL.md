---
name: retro
description: ループを測って規則を直す。PR を数え、前回の記録と比べ、効いていない規則を見つけて PR にする。10 PR ごと、または規則を変えたくなったときに使う。
---

# 測ってから直す

**規則を意見で変えない。** #68 は「`needs-review` が 46% に付いていて、GitHub 上で
レビューが付いたものは 1 件も無い」と数えてから基準を書き直した。#69 も #70 も同じ形で、
数えずに決めたものは 1 つも無い。

回すのは **10 PR ごと**。それ以外に、規則を変えたくなったときはいつでも。

## 1. ダンプを取る

このセッションから GitHub の API を直接叩くことはできない（`curl` は 403、MCP のみ）。
MCP の結果は**大きいとファイルに落ちる**ので、そのパスをそのまま使う。

```
mcp__github__list_pull_requests   owner/repo, state=all, perPage=100, sort=created, direction=asc
mcp__github__search_issues        repo:<owner>/<repo> is:pr label:needs-review
```

どちらも「result exceeds maximum allowed tokens」で保存されるので、
`/root/.claude/projects/.../tool-results/` のパスを控える。落ちなかったら自分で書き出す。

## 2. 数える

```bash
tools/loop-stats/loop-stats.py --prs <prs> --labels <labeled> --window 20
tools/loop-stats/loop-stats.py --self-test        # 数え方だけ先に確かめたいとき
```

`--window` で「全体」と「直近」が並ぶ。**見るのはこの 2 列の差**で、規則を変えた後に
数字が動いたかどうかがそこに出る。

## 3. 前回と比べる

[docs/loop.md](../../../docs/loop.md) に前回の表がある。git の履歴がそのまま推移になる。

**動いていない数字を探す。** 規則を変えたのに直近が全体と同じなら、その規則は効いていない。
効いていない規則は、書き足すのではなく**消すか、機械で強制する**（#71 でやった）。

ただし **0 のまま動かない指標は、規則より先に指標を疑う。** 初回は
「`needs-review` の的中（GitHub 上のレビュー）」が 0/33 で、ラベルを消す候補に見えた。
実際にはラベルに GitHub のレビューを呼ぶ働きが無く、**測る場所を間違えていた**。
効き方（auto-merge を保留する）で測り直すと、中央値 8 分 対 2 分ではっきり効いていた。

## 4. 直す

見つけた分だけ直す。**指標を良く見せるための変更はしない。** 数字は状態の説明であって
目標ではない（docs/build.md のカバレッジと同じ考え方）。

`docs/loop.md` を今回の表で置き換え、規則を変えたならその差分も同じ PR に入れる。

規則を新しく決め直しているので、**この PR は `needs-review`** になることが多い
（CLAUDE.md「auto-merge と needs-review」の 1 つ目と 2 つ目に当たるため）。

## 測れないもの

- **チャットでの差し戻し**。実際の押し戻しはほとんどここで起きていて、GitHub には残らない。
  `needs-review` については、止まった事実がマージまでの時間に出るのでそちらで代用している
- **CI の初回成功率**。run と PR の突き合わせが要る。必要になったら足す
- **`/ship` を通したか**。マーカーは手元にしか無い。代わりに「テンプレート充足」と
  「実測の記載」で、手順の後半を通ったかを見ている
