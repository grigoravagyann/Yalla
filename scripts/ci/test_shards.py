#!/usr/bin/env python3
"""Splits the backend test suite into CI shards, and proves afterwards that the shards ran all of it.

Used by .github/workflows/backend.yml. Standard library only, so the runner needs nothing installed.

  plan          list file -> one --filter per shard, every test class in exactly one shard
  verify-shard  one shard's .trx files ran exactly the tests the plan gave that shard
  verify-all    the shards' .trx files together ran every listed test, and none twice
  timings       .trx files -> the per-class durations the next plan balances by

The unit of splitting is the test CLASS, never the method. Every SQL-backed class sits in the one
xUnit collection `SqlServerCollection`, whose fixture migrates one database and shares it across
the classes it holds; a class split across two shards would get two databases and two fixtures
for what its author wrote as one. Keeping a class whole keeps that unit intact. Each shard still
creates its own fixture and database, exactly as a local run of one class does.

The list comes from `dotnet test <dll> --ListFullyQualifiedTests`, one `Namespace.Class.Method`
per line - the same string the vstest filter and the .trx `TestMethod` element use, so the plan
and the check compare like with like. Theory rows collapse onto their method's name, which is why
every comparison here is between sets of method names rather than counts of results.

Assignment is deterministic: classes sorted by estimated duration (largest first, ties by name),
each placed on the currently lightest shard (ties to the lowest shard number). Same list, same
timings file, same shard count -> the same plan, on any machine.

Run `python3 scripts/ci/test_shards.py <command> --help` for the arguments.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

TRX_NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

# A class with no recorded duration is estimated from the per-test rate of the classes that have
# one: integration classes (a real database, a real host) run about a second per test, the plain
# unit tests a few milliseconds, and mixing the two rates would let one unknown integration class
# outweigh nothing at all. Used only when the timings file has no rate of its own to offer.
HEAVY_MARKER = ".Integration."
DEFAULT_HEAVY_SECONDS_PER_TEST = 1.5
DEFAULT_LIGHT_SECONDS_PER_TEST = 0.01


def fail(message: str) -> None:
    print(f"::error::{message}" if os.environ.get("GITHUB_ACTIONS") else f"error: {message}")
    sys.exit(1)


def class_of(test: str) -> str:
    # xUnit's fully qualified name is `<class full name>.<method>`. A method name has no dots; a
    # nested class is `Outer+Inner`, so it stays one class here as well.
    owner, _, method = test.rpartition(".")
    if not owner or not method:
        fail(f"'{test}' is not a fully qualified test name")
    return owner


def read_list(path: Path) -> list[str]:
    tests = sorted({line.strip() for line in path.read_text(encoding="utf-8-sig").splitlines() if line.strip()})
    if not tests:
        fail(f"{path} lists no tests - discovery found nothing, or the listing step wrote elsewhere")
    return tests


def read_trx(path: Path) -> tuple[set[str], dict[str, float], dict[str, int]]:
    """Method names a .trx ran, seconds per class, and its counters."""
    root = ET.parse(path).getroot()

    methods: dict[str, str] = {}
    for unit in root.iterfind(".//t:TestDefinitions/t:UnitTest", TRX_NS):
        method = unit.find("t:TestMethod", TRX_NS)
        if method is not None:
            methods[unit.get("id", "")] = f"{method.get('className')}.{method.get('name')}"

    ran: set[str] = set()
    seconds: dict[str, float] = defaultdict(float)
    for result in root.iterfind(".//t:Results/t:UnitTestResult", TRX_NS):
        test = methods.get(result.get("testId", ""))
        if test is None:
            fail(f"{path}: a result whose test has no definition ({result.get('testName')})")
        ran.add(test)
        seconds[class_of(test)] += duration_seconds(result.get("duration"))

    counters_element = root.find(".//t:ResultSummary/t:Counters", TRX_NS)
    counters = {k: int(v) for k, v in (counters_element.attrib.items() if counters_element is not None else [])
                if v.isdigit()}
    return ran, dict(seconds), counters


def duration_seconds(value: str | None) -> float:
    if not value:
        return 0.0
    hours, minutes, seconds = value.split(":")
    return int(hours) * 3600 + int(minutes) * 60 + float(seconds)


def trx_files(directory: Path) -> list[Path]:
    return sorted(directory.rglob("*.trx"))


# --------------------------------------------------------------------------------------- plan


def estimate(classes: dict[str, list[str]], timings_path: Path | None) -> dict[str, int]:
    """Estimated milliseconds per class. Integers, so the greedy split cannot vary by platform."""
    recorded: dict[str, dict] = {}
    if timings_path is not None and timings_path.exists():
        recorded = json.loads(timings_path.read_text(encoding="utf-8")).get("classes", {})

    def rate(heavy: bool, default: float) -> float:
        timed = [v for k, v in recorded.items() if (HEAVY_MARKER in k) == heavy and v.get("tests")]
        tests = sum(v["tests"] for v in timed)
        return sum(v["seconds"] for v in timed) / tests if tests else default

    heavy_rate = rate(True, DEFAULT_HEAVY_SECONDS_PER_TEST)
    light_rate = rate(False, DEFAULT_LIGHT_SECONDS_PER_TEST)

    weights = {}
    for name, tests in classes.items():
        entry = recorded.get(name)
        if entry and entry.get("tests"):
            # Per test, then times today's count: a class that has grown since it was timed is
            # estimated at its current size rather than its old one.
            per_test = entry["seconds"] / entry["tests"]
        else:
            per_test = heavy_rate if HEAVY_MARKER in name else light_rate
        weights[name] = max(1, round(per_test * len(tests) * 1000))
    return weights


def filter_for(classes: list[str]) -> str:
    # `~` is "contains". The trailing dot anchors the class name, so `FooTests.` cannot also pick
    # up `FooTestsMore.Method`. Nothing in a class's full name needs vstest-filter escaping
    # (letters, digits, `_`, `.`, `+`, and a generic arity's backtick), and verify-shard catches it
    # if that ever stops being true: it fails on any test that ran without being assigned.
    for name in classes:
        if any(ch in name for ch in "()&|=!~\\,"):
            fail(f"class '{name}' has a character the vstest filter would need escaped")
    return "|".join(f"FullyQualifiedName~{name}." for name in classes)


def cmd_plan(args: argparse.Namespace) -> None:
    tests = read_list(args.list)
    if args.shards < 1:
        fail("--shards must be at least 1")

    classes: dict[str, list[str]] = defaultdict(list)
    for test in tests:
        classes[class_of(test)].append(test)

    if len(classes) < args.shards:
        fail(f"{len(classes)} test classes cannot fill {args.shards} shards; lower the shard count")

    weights = estimate(classes, args.timings)
    shards: list[list[str]] = [[] for _ in range(args.shards)]
    load = [0] * args.shards
    for name in sorted(classes, key=lambda c: (-weights[c], c)):
        target = min(range(args.shards), key=lambda i: (load[i], i))
        shards[target].append(name)
        load[target] += weights[name]

    # The construction above cannot drop or duplicate a class, and this proves it rather than
    # trusting it: a plan that loses a class is the failure the whole check exists to stop.
    assigned = [c for shard in shards for c in shard]
    if sorted(assigned) != sorted(classes) or len(assigned) != len(set(assigned)):
        fail("internal: the plan does not place every class exactly once")
    if any(not shard for shard in shards):
        fail("internal: a shard was left empty")

    out: Path = args.out
    out.mkdir(parents=True, exist_ok=True)
    (out / "all-tests.txt").write_text("\n".join(tests) + "\n", encoding="utf-8", newline="\n")

    summary = {"shards": args.shards, "tests": len(tests), "classes": len(classes), "plan": []}
    for index, shard in enumerate(shards, start=1):
        shard_classes = sorted(shard)
        shard_tests = sorted(t for c in shard_classes for t in classes[c])
        (out / f"shard-{index}.tests").write_text("\n".join(shard_tests) + "\n", encoding="utf-8", newline="\n")
        (out / f"shard-{index}.filter").write_text(filter_for(shard_classes), encoding="utf-8", newline="\n")
        summary["plan"].append({
            "shard": index,
            "classes": shard_classes,
            "tests": len(shard_tests),
            "estimated_seconds": round(load[index - 1] / 1000),
        })
    (out / "plan.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8", newline="\n")

    print(f"{len(tests)} tests in {len(classes)} classes over {args.shards} shards"
          f" (timings: {args.timings if args.timings and args.timings.exists() else 'none, estimated'}):")
    for entry in summary["plan"]:
        print(f"  shard {entry['shard']}: {len(entry['classes'])} classes, {entry['tests']} tests,"
              f" ~{entry['estimated_seconds']} s")

    located = None
    if args.locate:
        located = next((e["shard"] for e in summary["plan"] if args.locate in e["classes"]), None)
        if located is None:
            fail(f"class '{args.locate}' is not in the test list - renamed or deleted? The workflow"
                 " routes an artifact by it, so update the workflow along with the class")
        print(f"  {args.locate} is in shard {located}")

    if args.github_output:
        with open(args.github_output, "a", encoding="utf-8") as output:
            output.write(f"shards={json.dumps(list(range(1, args.shards + 1)))}\n")
            if located is not None:
                output.write(f"located-shard={located}\n")


# -------------------------------------------------------------------------------------- verify


def report_difference(label: str, names: set[str], limit: int = 25) -> None:
    print(f"{label} ({len(names)}):")
    for name in sorted(names)[:limit]:
        print(f"    {name}")
    if len(names) > limit:
        print(f"    ... and {len(names) - limit} more")


def cmd_verify_shard(args: argparse.Namespace) -> None:
    expected = set(read_list(args.plan / f"shard-{args.shard}.tests"))
    files = trx_files(args.results)
    if not files:
        fail(f"shard {args.shard}: no .trx under {args.results}")

    ran: set[str] = set()
    for path in files:
        ran |= read_trx(path)[0]

    missing, unexpected = expected - ran, ran - expected
    print(f"shard {args.shard}: assigned {len(expected)} tests, ran {len(ran)}")
    if missing:
        report_difference("Assigned but never ran", missing)
    if unexpected:
        report_difference("Ran but not assigned to this shard (the filter matched too much)", unexpected)
    if missing or unexpected:
        fail(f"shard {args.shard} did not run exactly its assigned tests")


def cmd_verify_all(args: argparse.Namespace) -> None:
    listed = set(read_list(args.plan / "all-tests.txt"))
    # The shard count comes from the plan rather than an argument, so the gate cannot be told to
    # look for fewer shards than the build planned.
    shards = json.loads((args.plan / "plan.json").read_text(encoding="utf-8"))["shards"]
    seen_in: dict[str, list[int]] = defaultdict(list)
    executed = skipped = 0

    for shard in range(1, shards + 1):
        directory = args.results / f"test-results-{shard}"
        files = trx_files(directory)
        if not files:
            fail(f"shard {shard} left no .trx (expected under {directory}) - it never ran its tests")
        for path in files:
            ran, _, counters = read_trx(path)
            executed += counters.get("executed", 0)
            skipped += counters.get("notExecuted", 0)
            for test in ran:
                seen_in[test].append(shard)

    ran = set(seen_in)
    missing, unexpected = listed - ran, ran - listed
    twice = {t for t, shards in seen_in.items() if len(set(shards)) > 1}

    print(f"listed {len(listed)} tests; the {shards} shards ran {len(ran)} distinct"
          f" ({executed} results, {skipped} skipped)")
    if missing:
        report_difference("Listed but run by no shard", missing)
    if unexpected:
        report_difference("Run but never listed", unexpected)
    if twice:
        report_difference("Run by more than one shard", twice)
    if missing or unexpected or twice:
        fail("the shards did not run the listed suite exactly once")
    if skipped:
        fail(f"{skipped} test(s) were skipped across the shards - a shard did not reach its SQL Server")
    print("Every listed test ran in exactly one shard, and none was skipped.")


# ------------------------------------------------------------------------------------- timings


def cmd_timings(args: argparse.Namespace) -> None:
    seconds: dict[str, float] = defaultdict(float)
    tests: dict[str, set[str]] = defaultdict(set)
    files = [f for path in args.trx for f in (trx_files(path) if path.is_dir() else [path])]
    if not files:
        fail("no .trx files given")
    for path in files:
        ran, per_class, _ = read_trx(path)
        for name, value in per_class.items():
            seconds[name] += value
        for test in ran:
            tests[class_of(test)].add(test)

    document = {
        "comment": "Per-class test durations for scripts/ci/test_shards.py plan. Regenerate with"
                   " `python3 scripts/ci/test_shards.py timings <trx files or folders>` - the backend"
                   " workflow's gate uploads a fresh copy as the `test-timings` artifact on every run.",
        "classes": {name: {"seconds": round(seconds[name], 2), "tests": len(tests[name])}
                    for name in sorted(seconds)},
    }
    text = json.dumps(document, indent=2) + "\n"
    if args.out:
        args.out.write_text(text, encoding="utf-8", newline="\n")
    else:
        sys.stdout.write(text)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    commands = parser.add_subparsers(dest="command", required=True)

    plan = commands.add_parser("plan", help="split a test list into shard filters")
    plan.add_argument("--list", type=Path, required=True, help="output of --ListFullyQualifiedTests")
    plan.add_argument("--shards", type=int, required=True)
    plan.add_argument("--timings", type=Path, help="per-class durations (optional)")
    plan.add_argument("--out", type=Path, required=True, help="folder for the plan files")
    plan.add_argument("--locate", help="a class whose shard to report as `located-shard`")
    plan.add_argument("--github-output", help="append `shards` and `located-shard` to this file")
    plan.set_defaults(run=cmd_plan)

    shard = commands.add_parser("verify-shard", help="one shard ran exactly its tests")
    shard.add_argument("--plan", type=Path, required=True)
    shard.add_argument("--shard", type=int, required=True)
    shard.add_argument("--results", type=Path, required=True, help="folder holding the shard's .trx")
    shard.set_defaults(run=cmd_verify_shard)

    every = commands.add_parser("verify-all", help="the shards together ran every test once")
    every.add_argument("--plan", type=Path, required=True, help="the plan folder; its plan.json names the shard count")
    every.add_argument("--results", type=Path, required=True, help="folder holding test-results-<n>/")
    every.set_defaults(run=cmd_verify_all)

    timings = commands.add_parser("timings", help="per-class durations from .trx files")
    timings.add_argument("trx", type=Path, nargs="+", help=".trx files or folders of them")
    timings.add_argument("--out", type=Path)
    timings.set_defaults(run=cmd_timings)

    args = parser.parse_args()
    args.run(args)


if __name__ == "__main__":
    main()
