# ビルド・テスト・カバレッジ

```bash
dotnet build
dotnet test
dotnet format            # CI では --verify-no-changes で検査される
dotnet test -p:CollectCoverage=true   # カバレッジ計測 + 基準判定。CI の Test はこれ
```

**.NET 10 SDK が必要**（`global.json` で固定）。ターゲットは `net10.0`、C# 14。

## カバレッジの規則

- 基準は**行 90% / ブランチ 80%（全体）**。下回ると CI が落ちる
- 全体で測るのは、テストが層をまたいで書かれているため（Web の結合テストが Features の
  ハンドラを、Features のテストが Domain を通す）。プロジェクト単位で測ると、
  実際には検証されているコードが未カバーに見える
- 全プロジェクトの結果を 1 ファイルにマージし、**最後に走る tests/Web が判定する**。
  順序は `-m:1`（直列）で `.slnx` の並び順に固定してある。並列だと完了順でマージされ、
  どれかのテストが遅くなるだけで順序が入れ替わり、不完全なデータで判定される（実際に起きた）
- **テストプロジェクトを増やすときは `.slnx` で tests/Web より前に置くこと**
- 設定は `tests/Directory.Build.props`（計測と除外）と `tests/Web/Web.csproj`（基準）
- 除外してよいのは「E2E が実プロセスで検証しているが coverlet が計測できないもの」だけ。
  テストを書けるものを除外で隠さない
