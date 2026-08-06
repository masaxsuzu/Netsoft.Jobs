#!/usr/bin/env python3
"""PR のダンプからループの実測を出す。/retro が使う。

取得はしない。GitHub の API はこのセッションから直接叩けず（MCP 経由のみ）、
取得まで抱えると環境が変わるたびに壊れるため、**食う側だけ**に閉じてある。
ダンプの作り方は .claude/skills/retro/SKILL.md にある。

    tools/loop-stats/loop-stats.py --prs prs.json [--labels needs-review.json]
                                   [--window 20] [--detail]
"""

import argparse
import json
import re
import statistics
import sys
from datetime import datetime

TEMPLATE_HEADINGS = ("確認したこと", "レビューで見てほしい点")


def load(path):
    """MCP のダンプは素の配列だったり {"pull_requests": [...]} だったりする。

    形を呼び出し側に合わせさせない。ツールの版が変わるたびに手順書を直すことになる。
    """
    with open(path, encoding="utf-8") as handle:
        data = json.load(handle)

    while isinstance(data, dict):
        lists = [value for value in data.values() if isinstance(value, list)]
        if not lists:
            nested = [value for value in data.values() if isinstance(value, dict)]
            if not nested:
                return []
            data = nested[0]
            continue
        data = max(lists, key=len)

    return [item for item in data if isinstance(item, dict) and "number" in item]


def parse_time(value):
    if not value:
        return None
    return datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ")


def merge_minutes(pr):
    created, merged = parse_time(pr.get("created_at")), parse_time(pr.get("merged_at"))
    if not created or not merged:
        return None
    return (merged - created).total_seconds() / 60


def has_template(pr):
    body = pr.get("body") or ""
    return all(heading in body for heading in TEMPLATE_HEADINGS)


def has_measurement(pr):
    """「確認したこと」に数字が書かれているか。

    /ship は実測を書けと言っている。書かれた数字は後から読み直せるが、
    「通りました」だけの報告は何も残さない。
    """
    body = pr.get("body") or ""
    start = body.find(TEMPLATE_HEADINGS[0])
    if start < 0:
        return False
    section = body[start : start + 800]
    return bool(re.search(r"\d+\s*(件|回|分|秒|%|ms|MB)", section))


def summarize(prs, labeled):
    merged = [pr for pr in prs if pr.get("merged_at")]
    minutes = [m for m in (merge_minutes(pr) for pr in merged) if m is not None]
    numbers = {pr["number"] for pr in prs}
    tagged = numbers & labeled

    # needs-review が効いているかは「レビューが付いたか」では測れない。
    # このラベルは auto-merge を保留するもので、GitHub 上のレビューを呼ぶ仕組みは
    # どこにも無い。実際 #1〜#71 で GitHub のレビューは 0 件だが、マージまでの
    # 中央値は 7.6 分（付いていないものは 1.5 分）で、**人の手は止まっていた**。
    # 測るべきは止まったかどうかで、止まった証拠はマージまでの時間の側にある。
    held = [m for m in (merge_minutes(pr) for pr in prs if pr["number"] in tagged) if m is not None]
    rest = [m for m in (merge_minutes(pr) for pr in prs if pr["number"] not in tagged) if m is not None]

    return [
        ("PR", f"{len(prs)} 件"),
        ("マージ済み", pct(len(merged), len(prs))),
        ("マージまでの中央値", human(statistics.median(minutes)) if minutes else "-"),
        ("マージまでの最大", human(max(minutes)) if minutes else "-"),
        ("needs-review", pct(len(tagged), len(prs))),
        ("　… その中央値", human(statistics.median(held)) if held else "-"),
        ("　… 以外の中央値", human(statistics.median(rest)) if rest else "-"),
        ("テンプレート充足", pct(sum(map(has_template, prs)), len(prs))),
        ("実測の記載", pct(sum(map(has_measurement, prs)), len(prs))),
    ]


def pct(part, whole):
    if not whole:
        return "-"
    return f"{part} / {whole} ({part / whole * 100:.0f}%)"


def human(minutes):
    if minutes < 60:
        return f"{minutes:.0f} 分"
    if minutes < 60 * 24:
        return f"{minutes / 60:.1f} 時間"
    return f"{minutes / 60 / 24:.1f} 日"


def render(title, left, right, window):
    lines = [f"## {title}", ""]
    if right is None:
        lines += ["| 指標 | 値 |", "|---|---|"]
        lines += [f"| {name} | {value} |" for name, value in left]
    else:
        lines += [f"| 指標 | 全体 | 直近 {window} 件 |", "|---|---|---|"]
        for (name, value), (_, recent) in zip(left, right):
            lines.append(f"| {name} | {value} | {recent} |")
    return "\n".join(lines)


def detail(prs, labeled):
    lines = ["", "| PR | 分 | ラベル | 型 | 実測 | タイトル |", "|---|---|---|---|---|---|"]
    for pr in prs:
        minutes = merge_minutes(pr)
        lines.append(
            "| #{} | {} | {} | {} | {} | {} |".format(
                pr["number"],
                f"{minutes:.0f}" if minutes is not None else "-",
                "needs-review" if pr["number"] in labeled else "",
                "o" if has_template(pr) else "x",
                "o" if has_measurement(pr) else "x",
                (pr.get("title") or "").replace("|", "/")[:48],
            )
        )
    return "\n".join(lines)


def self_test():
    """合成データで数え方だけを確かめる。ダンプの形は load が吸収する。"""
    sample = [
        {
            "number": 1,
            "created_at": "2026-01-01T00:00:00Z",
            "merged_at": "2026-01-01T00:10:00Z",
            "body": "## 確認したこと\n- 593 件通った\n## レビューで見てほしい点\nなし",
            "title": "a",
        },
        {
            "number": 2,
            "created_at": "2026-01-01T00:00:00Z",
            "merged_at": "2026-01-01T02:00:00Z",
            "body": "本文なし",
            "title": "b",
        },
        {"number": 3, "created_at": "2026-01-01T00:00:00Z", "body": "", "title": "c"},
    ]
    got = dict(summarize(sample, labeled={2}))
    expect = {
        "PR": "3 件",
        "マージ済み": "2 / 3 (67%)",
        # 65 分は「分」ではなく「時間」で出る（human の境界が 60 分）。
        "マージまでの中央値": "1.1 時間",
        "マージまでの最大": "2.0 時間",
        "needs-review": "1 / 3 (33%)",
        # #2 だけが付いている（120 分）。付いていない側は #1 の 10 分だけが対象。
        "　… その中央値": "2.0 時間",
        "　… 以外の中央値": "10 分",
        "テンプレート充足": "1 / 3 (33%)",
        "実測の記載": "1 / 3 (33%)",
    }
    bad = {k: (v, expect[k]) for k, v in got.items() if v != expect[k]}
    if bad:
        for key, (actual, wanted) in bad.items():
            print(f"NG {key}: {actual!r} != {wanted!r}", file=sys.stderr)
        return 1
    print(f"self-test: {len(expect)} 項目すべて一致")
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prs", help="PR 一覧のダンプ")
    parser.add_argument("--labels", help="needs-review が付いた PR のダンプ")
    parser.add_argument("--window", type=int, default=20, help="直近何件を別立てで見るか")
    parser.add_argument("--detail", action="store_true", help="PR ごとの表も出す")
    parser.add_argument("--self-test", action="store_true", help="数え方を自己検査する")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    if not args.prs:
        parser.error("--prs が要る（--self-test を除く）")

    prs = sorted(load(args.prs), key=lambda pr: pr["number"])
    if not prs:
        print("PR が 1 件も読めなかった。ダンプの中身を確認すること。", file=sys.stderr)
        return 1

    labeled = {pr["number"] for pr in load(args.labels)} if args.labels else set()

    recent = prs[-args.window :] if len(prs) > args.window else None
    title = "ループの実測  #{}〜#{}".format(prs[0]["number"], prs[-1]["number"])
    print(
        render(
            title,
            summarize(prs, labeled),
            summarize(recent, labeled) if recent else None,
            args.window,
        )
    )
    if args.detail:
        print(detail(prs, labeled))
    return 0


if __name__ == "__main__":
    sys.exit(main())
