# 規約

## コード

- `TreatWarningsAsErrors`。警告を残さない
- ファイルスコープ名前空間を使う
- コード / テスト / コミットログ / コメントの書き分けは
  [CLAUDE.md の「どこに何を書くか」](../CLAUDE.md)。ここには繰り返さない

## 機能の足し方

機能ごとにフォルダを作り、`XxxEndpoint` と `XxxServiceCollectionExtensions` を置く。

- **エンドポイントに置くのは、ハンドラを呼んで結果を HTTP へ写す処理だけ。判断を書かない。**
  画面（Blazor）は HTTP を通らずハンドラを直接呼ぶので、ここにロジックがあると画面から使えない
- 登録の口を機能ごとに分けてあるのは、その機能が何を必要とするかを機能の側に置くため。
  **機能を足すときは 3 か所**（機能フォルダの 2 ファイル、`JobFeaturesServiceCollectionExtensions`、
  `JobFeaturesEndpointRouteBuilderExtensions`）
- まとめて入れたい側は `AddJobFeatures` / `MapJobFeatures` を呼ぶだけでよい

## 命名とプロジェクト

- プロジェクト名は `Domain` のように短く保つ。アセンブリ名の `Netsoft.Jobs.` prefix は
  `src` / `tests` 直下の `Directory.Build.props` が付ける。
  **各 csproj には書かない**（同名 csproj が複数あるため必ず片方がずれる）
