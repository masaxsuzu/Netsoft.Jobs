#!/usr/bin/env bash
# テストをカバレッジ計測つきで実行し、基準を下回ったら失敗する。
# CI の Test ステップと手元の確認で同じものを使う。
#
# 基準: 行 95% / ブランチ 80%
# 除外の方針は coverage.runsettings のコメントを参照。
set -euo pipefail
cd "$(dirname "$0")/.."

LINE_THRESHOLD=95
BRANCH_THRESHOLD=80

rm -rf TestResults
dotnet test -c Release --no-build \
  --collect:"XPlat Code Coverage" \
  --settings coverage.runsettings \
  --logger "trx;LogFileName=test-results.trx" \
  --results-directory TestResults

dotnet tool restore > /dev/null
dotnet reportgenerator \
  -reports:"TestResults/*/coverage.cobertura.xml" \
  -targetdir:TestResults/coverage \
  -reporttypes:"TextSummary;JsonSummary" > /dev/null

cat TestResults/coverage/Summary.txt

python3 - "$LINE_THRESHOLD" "$BRANCH_THRESHOLD" <<'PY'
import json, sys
line_min, branch_min = float(sys.argv[1]), float(sys.argv[2])
s = json.load(open('TestResults/coverage/Summary.json'))['summary']
line, branch = s['linecoverage'], s['branchcoverage']
print(f"\nカバレッジ判定: line {line}% (基準 {line_min}%) / branch {branch}% (基準 {branch_min}%)")
failed = []
if line < line_min:
    failed.append(f"行カバレッジ {line}% が基準 {line_min}% を下回っています")
if branch < branch_min:
    failed.append(f"ブランチカバレッジ {branch}% が基準 {branch_min}% を下回っています")
if failed:
    print("\n".join(f"NG: {f}" for f in failed))
    sys.exit(1)
print("OK")
PY
