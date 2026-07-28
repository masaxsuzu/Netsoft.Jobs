# ラベル

開発サイクルで意味を持つラベル。3 種とも作成済み。

| ラベル | 意味 | 誰が付けるか |
|---|---|---|
| `auto-merge` | CI が通ったら自動でマージしてよい | タスクを起票した人 / Claude |
| `needs-review` | 人のレビューを待つ。`auto-merge` は付けない | Claude |
| `blocked` | 判断待ちで進められない | 誰でも |

`auto-merge` が無い PR は、CI が通っても自動マージされない。
これは「黙って入る」ことを防ぐための既定値であり、意図的に手間を残している。

`auto-merge` を付けてよいのは次をすべて満たす場合。迷ったら付けない。

- 受け入れ条件を満たしている
- ローカルで `dotnet build` と `dotnet test` が通っている
- 設計上の判断が含まれない（含むなら `needs-review`）

## 色と説明

API 経由で作成したため、色は既定のグレー、説明は空になっている。
見た目を整えるなら次を実行する（ラベル自体は既に存在するので `edit`）。

```
gh label edit auto-merge   --description "CI 通過後に自動マージする" --color 0E8A16
gh label edit needs-review --description "人のレビュー待ち"         --color FBCA04
gh label edit blocked      --description "判断待ちで進められない"   --color B60205
```
