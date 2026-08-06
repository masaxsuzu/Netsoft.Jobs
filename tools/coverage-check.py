#!/usr/bin/env python3
"""マージ済みのカバレッジに基準を当てる。tools/coverage.sh から呼ばれる。

基準は **C1（分岐）80% を src の各プロジェクトが個別に**満たすこと（docs/build.md）。

判定はここ一箇所だけで、全テストが終わってから一度だけ走る。テストプロジェクト側に
基準を置くと、カバレッジがプロジェクトをまたいで積み上がるせいで中間状態が偽の赤になる
（Contracts は tests/Web が走るまで分岐 0%。その分岐は Web の結合テストでしか通らない）。

**期待するアセンブリを src/ から数える**のが要点。レポートに現れなかったものは
「0% だった」ではなく「誰も通していない」なので、欠けていること自体を落とす。
以前の coverlet の判定はここが素通りだった。
"""

import argparse
import json
import pathlib
import sys

SUMMARY = "TestResults/coverage/Summary.json"
PREFIX = "Netsoft.Jobs."


def expected_assemblies(root):
    """src/<Name>/<Name>.csproj から Netsoft.Jobs.<Name> を作る（src/Directory.Build.props と同じ規則）。"""
    return {
        PREFIX + path.parent.name
        for path in sorted((root / "src").glob("*/*.csproj"))
    }


def measured(summary_path):
    with open(summary_path, encoding="utf-8") as handle:
        data = json.load(handle)
    return {
        assembly["name"]: assembly["branchcoverage"]
        for assembly in data["coverage"]["assemblies"]
    }


def judge(expected, actual, threshold):
    rows, failures = [], []
    for name in sorted(expected):
        branch = actual.get(name)
        if branch is None:
            rows.append((name, "計測なし", "NG"))
            failures.append(f"{name}: レポートに現れていない（誰も通していない）")
        elif branch < threshold:
            rows.append((name, f"{branch:.1f}%", "NG"))
            failures.append(f"{name}: 分岐 {branch:.1f}% < {threshold}%")
        else:
            rows.append((name, f"{branch:.1f}%", "ok"))

    for name in sorted(set(actual) - expected):
        rows.append((name, f"{actual[name]:.1f}%", "対象外"))

    return rows, failures


def self_test():
    expected = {"Netsoft.Jobs.A", "Netsoft.Jobs.B", "Netsoft.Jobs.C"}
    actual = {"Netsoft.Jobs.A": 100.0, "Netsoft.Jobs.B": 79.9, "Netsoft.Jobs.X": 50.0}
    rows, failures = judge(expected, actual, 80)
    checks = {
        "満たすものは ok": ("Netsoft.Jobs.A", "100.0%", "ok") in rows,
        "下回るものは NG": ("Netsoft.Jobs.B", "79.9%", "NG") in rows,
        "欠けているものも NG": ("Netsoft.Jobs.C", "計測なし", "NG") in rows,
        "src に無いものは対象外": ("Netsoft.Jobs.X", "50.0%", "対象外") in rows,
        "失敗は 2 件": len(failures) == 2,
    }
    for name, ok in checks.items():
        print(f"  {'ok' if ok else 'NG'}  {name}")
    return 0 if all(checks.values()) else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--summary", default=SUMMARY)
    parser.add_argument("--threshold", type=float, default=80)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    root = pathlib.Path(__file__).resolve().parent.parent
    summary_path = pathlib.Path(args.summary)
    if not summary_path.is_absolute():
        summary_path = root / summary_path

    if not summary_path.exists():
        print(f"カバレッジの集計が無い: {summary_path}", file=sys.stderr)
        return 1

    rows, failures = judge(expected_assemblies(root), measured(summary_path), args.threshold)

    width = max(len(name) for name, _, _ in rows)
    for name, branch, verdict in rows:
        print(f"  {verdict:6s} {name:{width}s}  分岐 {branch}")

    if failures:
        print(f"\nC1 {args.threshold:.0f}% を満たしていない:", file=sys.stderr)
        for line in failures:
            print(f"  - {line}", file=sys.stderr)
        return 1

    print(f"\nC1 {args.threshold:.0f}%: src の {len(rows) - sum(1 for r in rows if r[2] == '対象外')} プロジェクトすべて満たしている")
    return 0


if __name__ == "__main__":
    sys.exit(main())
