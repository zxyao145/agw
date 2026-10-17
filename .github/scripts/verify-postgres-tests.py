"""Reject missing or skipped PostgreSQL correctness tests, even if dotnet exits zero."""
import argparse
from pathlib import Path
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("directory", type=Path)
parser.add_argument("--suite", choices=("all", "agents", "jobs"), default="all")
args = parser.parse_args()

agent_tests = (
    "Postgres_ExpiredLease_TakeoverRejectsOldInstanceWrites",
    "Postgres_ConcurrentCommits_AssignContiguousSequences",
    "Postgres_UpgradeActiveExecutions_PreservesStateAndAllowsRecovery",
    "Postgres_ConcurrentStarts_ReserveCapacityBeforeLeasesAndReleaseOnFailure",
)
job_tests = (
    "AcquireAsync_PostgresLeaseLoss_CancelsHeldOperation",
    "RecoverAsync_ActiveTurnChangesAfterSnapshot_DoesNotRecordStaleOutcome",
    "Delete_ConcurrentAttemptClaim_PreservesJobLogs(deleteByProject: False)",
    "Delete_ConcurrentAttemptClaim_PreservesJobLogs(deleteByProject: True)",
    "ExecuteAsync_PostgresLeaseLost_CancelsDatabaseOperation(durableRecovery: False)",
    "ExecuteAsync_PostgresLeaseLost_CancelsDatabaseOperation(durableRecovery: True)",
    "TryAcquireAsync_PostgresContention_ReturnsNullUntilLeaseReleased",
    "RecoverAsync_PostgresLeaseLost_CancelsInProcessRecovery",
    "Delete_ConcurrentMutableUpdate_ReturnsModificationConflictAndPreservesJobLogs",
    "Delete_ConcurrentProjectDeletion_ReturnsSameMissingResult",
)
required = agent_tests + job_tests if args.suite == "all" else agent_tests if args.suite == "agents" else job_tests
results = [
    item
    for report in args.directory.glob("*.trx")
    for item in ET.parse(report).iter()
    if item.tag.endswith("}UnitTestResult")
]
for name in required:
    matches = [item for item in results if name in item.get("testName", "")]
    if not matches or any(item.get("outcome") != "Passed" for item in matches):
        raise SystemExit(f"Required PostgreSQL test did not pass: {name}")
print("Required PostgreSQL correctness tests executed and passed.")
