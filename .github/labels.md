# ラベル

開発サイクルで意味を持つラベル。GitHub 上で事前に作成しておくこと。

| ラベル | 意味 | 誰が付けるか |
|---|---|---|
| `auto-merge` | CI が通ったら自動でマージしてよい | タスクを起票した人 / Claude |
| `needs-review` | 人のレビューを待つ。`auto-merge` は付けない | Claude |
| `blocked` | 判断待ちで進められない | 誰でも |

`auto-merge` が無い PR は、CI が通っても自動マージされない。
これは「黙って入る」ことを防ぐための既定値であり、意図的に手間を残している。

作成コマンド:

```
gh label create auto-merge   --description "CI 通過後に自動マージする" --color 0E8A16
gh label create needs-review --description "人のレビュー待ち"         --color FBCA04
gh label create blocked      --description "判断待ちで進められない"   --color B60205
```
