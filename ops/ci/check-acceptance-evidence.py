#!/usr/bin/env python3
import argparse
import csv
import hashlib
import json
from pathlib import Path
import re
import shlex
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
SUITES = ("UnitTests", "IntegrationTests", "ContractTests")


def catalog_rows(root=ROOT):
    spec = (root / "specs/001-multibrand-redesign/spec.md").read_text()
    criteria = {m[1]: hashlib.sha256(m[2].encode()).hexdigest() for m in re.finditer(r"^- \*\*(SC-\d{3})\*\*: ([^\n]+)", spec, re.M)}
    expected = {f"SC-{i:03d}" for i in range(1, 54)}
    if set(criteria) != expected:
        raise ValueError("Expected exactly the approved 53 success criteria.")
    with (root / "ops/acceptance-scenarios.tsv").open(newline="") as source:
        rows = list(csv.DictReader(source, delimiter="\t"))
    if len(rows) != 53 or {r["criterion"] for r in rows} != expected:
        raise ValueError("Acceptance catalog has missing or duplicate criteria.")
    for row in rows:
        if row["criterion_sha256"] != criteria[row["criterion"]] or not row["remaining_evidence"].strip():
            raise ValueError("Acceptance catalog is stale or lacks its evidence limit.")
        row["witnesses"] = row["witnesses"].split(";") if row["witnesses"] else []
        if len(set(row["witnesses"])) != len(row["witnesses"]):
            raise ValueError("Duplicate scenario witness.")
        for witness in row["witnesses"]:
            if not re.fullmatch(r"Platform\.(?:UnitTests|IntegrationTests|ContractTests)\.[A-Za-z0-9_]+\.[A-Za-z0-9_]+", witness):
                raise ValueError("Invalid test witness.")
            project, cls, method = witness.rsplit(".", 2)
            source = root / "tests" / project / (cls + ".cs")
            text = source.read_text()
            pattern = r"\[(?:Fact|Theory)[^\]]*\]\s*(?:\[[^\]]*\]\s*)*public\s+(?:async\s+)?(?:Task(?:<[^>]+>)?|void)\s+" + re.escape(method) + r"\("
            if not re.search(pattern, text):
                raise ValueError("Witness is not a declared executable test.")
    return rows


def read_trx(path):
    document = ET.parse(path)
    counters = document.find(".//{*}Counters")
    summary = document.find(".//{*}ResultSummary")
    if counters is None or summary is None or summary.get("outcome") not in ("Completed", "Passed"):
        raise ValueError("Missing or unsuccessful test result summary.")
    counts = {k: int(counters.get(k, "0")) for k in ("total", "executed", "passed", "failed", "error", "timeout", "aborted", "inconclusive", "notExecuted", "disconnected", "notRunnable", "warning", "pending", "inProgress", "passedButRunAborted")}
    if counts["total"] <= 0 or counts["total"] != counts["executed"] or counts["total"] != counts["passed"] or any(v for k, v in counts.items() if k not in ("total", "executed", "passed")):
        raise ValueError("Tests are missing, skipped, failed or incomplete.")
    definitions = {}
    for unit in document.findall(".//{*}UnitTest"):
        method = unit.find("{*}TestMethod")
        if method is not None:
            definitions[unit.get("id")] = method.get("className", "").split(",")[0] + "." + method.get("name", "").split("(")[0]
    results = document.findall(".//{*}UnitTestResult")
    if len(results) != counts["total"] or any(result.get("outcome") != "Passed" for result in results):
        raise ValueError("Test result rows do not prove the summary.")
    identities = [r.get("executionId") for r in results]
    if None in identities or len(set(identities)) != len(identities):
        raise ValueError("Test execution identities are absent or repeated.")
    passed = set()
    for result in results:
        identity = definitions.get(result.get("testId"))
        if not identity:
            raise ValueError("Test definition is missing.")
        passed.add(identity)
    return counts, passed


def observe(rows, suite_results):
    if set(suite_results) != set(SUITES):
        raise ValueError("All three test suites are required.")
    passed = set().union(*(r[1] for r in suite_results.values()))
    scenarios = []
    for row in rows:
        if any(w not in passed for w in row["witnesses"]):
            raise ValueError("A required scenario witness was not observed passing.")
        scenarios.append({"criterion": row["criterion"], "criterionSha256": row["criterion_sha256"], "state": "observed-synthetic-subset" if row["witnesses"] else "planned-unexecuted", "witnesses": row["witnesses"], "remainingEvidence": row["remaining_evidence"]})
    return {"version": 1, "scope": "synthetic-test-witnesses-only", "releaseReady": False, "fullSuccessCriteriaVerified": False, "suites": {k: v[0] for k, v in suite_results.items()}, "scenarios": scenarios}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--catalog-only", action="store_true")
    parser.add_argument("--print-command", choices=[f"SC-{i:03d}" for i in range(1, 54)])
    parser.add_argument("--results-root", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    rows = catalog_rows()
    if args.print_command:
        if args.catalog_only or args.results_root or args.output:
            parser.error("Command rendering cannot produce observed evidence.")
        row = next(r for r in rows if r["criterion"] == args.print_command)
        if not row["witnesses"]:
            print("PLANNED/UNEXECUTED: " + row["remaining_evidence"])
            return
        for suite in SUITES:
            witnesses = [w for w in row["witnesses"] if w.startswith("Platform." + suite + ".")]
            if witnesses:
                print("dotnet test tests/Platform." + suite + "/Platform." + suite + ".csproj -c Release --no-build --no-restore --filter " + shlex.quote("|".join("FullyQualifiedName=" + w for w in witnesses)))
        return
    if args.catalog_only:
        if args.results_root or args.output:
            parser.error("Catalog-only mode cannot produce observed evidence.")
        print("53 acceptance criteria have current, bounded witness mappings; no execution claimed.")
        return
    if args.results_root is None or args.output is None:
        parser.error("Observed evidence requires actual test results and an output path.")
    report = observe(rows, {suite: read_trx(args.results_root / suite / "qualification.trx") for suite in SUITES})
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    print("Acceptance witnesses verified against all three passing TRX suites; full criteria/release remain unqualified.")


if __name__ == "__main__":
    main()
