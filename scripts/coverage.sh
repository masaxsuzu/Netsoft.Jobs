#!/usr/bin/env bash
#
# テストを実行し、カバレッジが基準を下回ったら失敗する。CI の Test ステップと手元で同じものを使う。
#
# 基準の判定は coverlet の /p:Threshold に任せ、自前で数字を比較しない。
#
# 基準を「全体（マージ後）」に置いているのは、テストが層をまたいで書かれているため。
# Web の結合テストが Features のハンドラを通し、Features のテストが Domain を通す。
# プロジェクト単位で測ると、実際には検証されているコードが未カバーに見える
# （実測: Features 単独 73.7%、Web 単独のブランチ 70%）。だから順に実行して
# 1 つのファイルにマージし、最後の 1 つでまとめて判定する。
#
# 除外は「E2E が実プロセスで検証しているが、coverlet が別プロセスを計測できないもの」だけ。
#   *.razor                    … イベントハンドラは回路 (WebSocket) 経由でしか動かない
#   JobExecutionHostedService  … 結合テストは決定性のためエンジンを止めている（属性で除外）
# テストを書けるのに書いていないものを除外で隠さないこと。
#
set -euo pipefail
cd "$(dirname "$0")/.."

# 行, ブランチ。カンマは MSBuild が引数の区切りと解釈するので %2C で渡す。
THRESHOLD='95%2C80'
THRESHOLD_TYPE='line%2Cbranch'

# 既定は Minimum（モジュール単位の最小値）で、これだと小さなモジュール 1 つで落ちる。
# 基準は全体に対するものなので Total を明示する。
THRESHOLD_STAT='Total'

MERGE_FILE="$PWD/TestResults/coverage.json"

rm -rf TestResults
mkdir -p TestResults

coverage_args=(
  -c Release --no-build
  /p:CollectCoverage=true
  "/p:CoverletOutput=$MERGE_FILE"
  "/p:MergeWith=$MERGE_FILE"
  '/p:Exclude=[*.Tests]*'
  '/p:ExcludeByFile=**/*.razor'
)

# 順に実行してマージする。最後の Web で全体を判定するので、
# テストプロジェクトを足すときは Web より前に入れること。
for project in Domain Infrastructure Features; do
  dotnet test "tests/$project" "${coverage_args[@]}"
done

dotnet test tests/Web "${coverage_args[@]}" \
  "/p:Threshold=$THRESHOLD" \
  "/p:ThresholdType=$THRESHOLD_TYPE" \
  "/p:ThresholdStat=$THRESHOLD_STAT"

# E2E はアプリを別プロセスで起動するため coverlet の計測に乗らない。
# 計測から外して普通に実行する。
dotnet test tests/E2E -c Release --no-build
