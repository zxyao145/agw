"""Reject missing or skipped PostgreSQL correctness tests, even if dotnet exits zero."""
import sys
from pathlib import Path
import xml.etree.ElementTree as ET

required = (
    "Postgres_ExpiredLease_TakeoverRejectsOldInstanceWrites",
    "Postgres_ConcurrentCommits_AssignContiguousSequences",
    "Postgres_UpgradeActiveExecutions_PreservesStateAndAllowsRecovery",
    "Postgres_ConcurrentStarts_ReserveCapacityBeforeLeasesAndReleaseOnFailure",
)
results = [
    item
    for report in Path(sys.argv[1]).glob("*.trx")
    for item in ET.parse(report).iter()
    if item.tag.endswith("}UnitTestResult")
]
for name in required:
    matches = [item for item in results if name in item.get("testName", "")]
    if not matches or any(item.get("outcome") != "Passed" for item in matches):
        raise SystemExit(f"Required PostgreSQL test did not pass: {name}")
print("Required PostgreSQL correctness tests executed and passed.")
