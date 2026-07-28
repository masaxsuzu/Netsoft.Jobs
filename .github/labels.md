# ラベル

| ラベル | 意味 | 誰が付けるか |
|---|---|---|
| `needs-review` | 人のレビューを待つ。auto-merge は有効にしない | Claude |
| `blocked` | 判断待ちで進められない | 誰でも |

## `auto-merge` ラベルは使わない

自動マージはリポジトリのルールセット（必須チェック）と GitHub ネイティブの
auto-merge で行う。マージしてよいという意思表示は
**PR の auto-merge を有効にすること自体**であり、GitHub が「自動マージ有効」と表示する。

ラベルを併用すると、同じ意図が「ラベル」と「auto-merge の有効状態」の 2 箇所に
現れて必ずズレる。片方だけ付いた PR が何を意味するのか誰にも分からなくなるので、
状態は 1 箇所に持たせる。

`auto-merge` ラベル自体はリポジトリに残っているが、どの仕組みも参照していない。
不要なら削除してよい。

```
gh label delete auto-merge
```

## 色と説明

API 経由で作成したため、色は既定のグレー、説明は空になっている。整えるなら次を実行する。

```
gh label edit needs-review --description "人のレビュー待ち"       --color FBCA04
gh label edit blocked      --description "判断待ちで進められない" --color B60205
```
