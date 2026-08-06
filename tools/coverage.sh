#!/usr/bin/env bash
#
# テストをカバレッジ付きで走らせ、マージして、基準を当てる。
# ゲート（tools/gate.sh）と CI の両方がこれを呼ぶ。同じ手が両方で走ることを崩さない。
#
# -m:1 は要らない。各テストプロジェクトが自分のファイルに書くので、並列でも結果が
# 変わらない（coverage.runsettings の注記を参照）。実測で 44〜52 秒 → 23〜28 秒。
#
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

# 前回の結果が残っていると、消えたはずのテストの計測を拾ってしまう。
rm -rf TestResults tests/*/TestResults

dotnet test --settings coverage.runsettings --collect:"XPlat Code Coverage" "$@"

dotnet tool restore >/dev/null
dotnet reportgenerator \
    -reports:"tests/*/TestResults/*/coverage.cobertura.xml" \
    -targetdir:TestResults/coverage \
    -reporttypes:"JsonSummary;TextSummary" \
    >/dev/null

python3 tools/coverage-check.py
