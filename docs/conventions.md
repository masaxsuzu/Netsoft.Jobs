# 規約

## コード

- `TreatWarningsAsErrors`。警告を残さない
- ファイルスコープ名前空間を使う
- テストは日本語のメソッド名で「何を保証するか」を書く
- コメントは「なぜそうしたか」を書く。何をしているかはコードを読めば分かる

## 命名とプロジェクト

- プロジェクト名は `Domain` のように短く保つ。アセンブリ名の `Netsoft.Jobs.` prefix は
  `src` / `tests` 直下の `Directory.Build.props` が付ける。
  **各 csproj には書かない**（同名 csproj が複数あるため必ず片方がずれる）
