#!/usr/bin/env bash
#
# コミットした木で build / test / format を通し、通ったらマーカーを押す。
# push のフック（tools/gate-hook.sh）はこのマーカーを見て push を通す。
#
# 作業ツリーではなくコミットした木で通すのは、push されるのも CI が見るのも
# その木だから。作業ツリーで通してからコミットするまでの間に差し込まれた変更は、
# 誰にも見られないまま main へ向かうことになる。
#
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

# フックの置き場を repo の中に向ける。core.hooksPath は clone ごとの設定で、
# .git/hooks は git 管理下に置けないため、ゲートを通すたびに冪等に貼り直す。
# これで clone した誰でも、最初にゲートを通した時点で pre-push が入る。
git config core.hooksPath tools/git-hooks

if [ -n "$(git status --porcelain)" ]; then
    echo "gate: 未コミットの変更があります。先にコミットしてください。" >&2
    git status --short >&2
    exit 1
fi

dotnet build
tools/coverage.sh --no-build
dotnet format --verify-no-changes

git rev-parse 'HEAD^{tree}' >"$(git rev-parse --git-dir)/ship-gate-ok"
echo "gate: 通過しました（$(git rev-parse --short HEAD)）"
