#!/usr/bin/env python3
"""Run fixed Release workloads or compare reports. No wall-clock performance gates."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys


def compare(before, after):
    for field in ("schema", "workload", "corpusSha256", "harnessSha256", "environment", "parameters"):
        if before[field] != after[field] or (field == "harnessSha256" and not before[field]):
            raise ValueError(f"Incompatible reports: {field} differs or is missing")
    old = {item["id"]: item["samples"] for item in before["scenarios"]}
    new = {item["id"]: item["samples"] for item in after["scenarios"]}
    if old.keys() != new.keys():
        raise ValueError("Incompatible scenario sets")
    lines = [f"Before: {before['revision']}", f"After:  {after['revision']}",
             "Medians per fixed workload (timings/allocations are observations, not gates):"]
    for name, samples in old.items():
        candidate = new[name]
        expected = before["parameters"]["samples"]
        if len(samples) != expected or len(candidate) != expected or expected < 1:
            raise ValueError(f"Incomplete samples: {name}")
        if any(item["work"].keys() != samples[0]["work"].keys() for item in samples + candidate):
            raise ValueError(f"Incompatible work counters: {name}")
        lines.append(name)
        for metric in ("milliseconds", "allocatedBytes"):
            left = statistics.median(item[metric] for item in samples)
            right = statistics.median(item[metric] for item in candidate)
            delta = f"{(right / left - 1) * 100:+.1f}%" if left else "n/a (zero baseline)"
            lines.append(f"  {metric}: {left:,.3f} -> {right:,.3f} ({delta})")
        for counter in samples[0]["work"]:
            def values(items):
                numbers = {item["work"][counter] for item in items}
                known = sorted(value for value in numbers if value is not None)
                return str(known) + (" (unavailable in some/all samples)" if None in numbers else "")
            left = values(samples)
            right = values(candidate)
            lines.append(f"  {counter}: {left} -> {right}")
    return "\n".join(lines)


def run(args):
    repo = args.repo.resolve()
    output = args.output.resolve()
    log = output.with_suffix(".log")
    if output == log or output.exists() or log.exists():
        raise ValueError("Choose a new .json output path; existing reports/logs are never overwritten")
    output.parent.mkdir(parents=True, exist_ok=True)
    fixture = repo / "tests/LiveMarkdown.Avalonia.Tests/RepeatableRenderingBenchmarks.cs"
    host = repo / "tests/LiveMarkdown.Avalonia.Tests/MarkdownPointerSelectionTests.cs"
    session = repo / "tests/LiveMarkdown.Avalonia.Tests/HeadlessSession.cs"
    digest = hashlib.sha256(b"\0".join(path.read_bytes() for path in (fixture, host, session))).hexdigest()
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip()
    dirty = subprocess.check_output(["git", "status", "--porcelain"], cwd=repo, text=True)
    env = os.environ.copy()
    env.update(LM_BENCHMARK_OUTPUT=str(output), LM_BENCHMARK_HARNESS_SHA256=digest,
               LM_BENCHMARK_REVISION=revision + (" (dirty)" if dirty else ""))
    command = ["dotnet", "test", "tests/LiveMarkdown.Avalonia.Tests/LiveMarkdown.Avalonia.Tests.csproj",
               "-c", "Release", "--filter", "FullyQualifiedName~RepeatableRenderingBenchmarks.Fixed_rendering_workloads",
               "--logger", "console;verbosity=detailed"]
    command += [f"-p:{value}" for value in args.msbuild_property]
    print(f"Running six workloads only; full output: {log}", flush=True)
    with log.open("x") as stream:
        result = subprocess.run(command, cwd=repo, env=env, stdout=stream, stderr=subprocess.STDOUT)
    if result.returncode or not output.exists():
        raise ValueError(f"Benchmark failed (exit {result.returncode}); inspect {log}")
    report = json.loads(output.read_text())
    # Prove the runner got the intended fixture rather than a successful zero-test invocation.
    if report["harnessSha256"] != digest or len(report["scenarios"]) != 6:
        raise ValueError(f"Unexpected benchmark report; inspect {log}")
    print(f"Report: {output}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    runner = commands.add_parser("run")
    runner.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[1])
    runner.add_argument("--output", type=Path, required=True)
    runner.add_argument("--msbuild-property", action="append", default=[], metavar="NAME=VALUE")
    comparison = commands.add_parser("compare")
    comparison.add_argument("before", type=Path)
    comparison.add_argument("after", type=Path)
    args = parser.parse_args()
    try:
        if args.command == "run":
            run(args)
        else:
            print(compare(json.loads(args.before.read_text()), json.loads(args.after.read_text())))
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
