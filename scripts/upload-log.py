#!/usr/bin/env python3
"""Send a real log file to IncidentIQ.

Reads a log file the way an agent would, and posts it to the ingestion endpoint
in batches. Everything downstream is the real pipeline: normalisation,
fingerprinting, detection, correlation and analysis.

    ./scripts/upload-log.py app.log --service payments-api
    kubectl logs deploy/payments-api | ./scripts/upload-log.py --service payments-api
    ./scripts/upload-log.py app.log --service payments-api --watch

Two things make real logs harder than generated ones, and both are handled here:

- **Stack traces span many lines.** A continuation line belongs to the message
  above it, not to itself. Uploading them as separate events would produce one
  pattern per stack frame and bury the actual error.
- **Formats vary.** Structured JSON, Serilog's compact format, and plain text
  all appear in the same estate. Each is parsed; anything unrecognised still
  goes through as a message, because dropping a line silently is worse than
  classifying it imperfectly.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.request
import uuid
from dataclasses import dataclass, field
from datetime import UTC, datetime, timedelta
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

#: The endpoint caps a batch at 500 events.
BATCH_SIZE = 500

#: Events older than this are rejected by the ingestion validator, so an old
#: log file is shifted forward rather than silently losing most of its lines.
MAX_EVENT_AGE = timedelta(days=7)

#: The detector only counts occurrences inside its window, so a log older than
#: this will be stored and fingerprinted but will never open an incident.
#: Matches Detection__WindowMinutes on the processor.
DETECTION_WINDOW = timedelta(minutes=5)

LEVEL_WORDS = {
    "trace": "Trace", "verbose": "Trace", "debug": "Debug", "dbg": "Debug",
    "info": "Information", "information": "Information", "inf": "Information",
    "notice": "Information", "warn": "Warning", "warning": "Warning", "wrn": "Warning",
    "error": "Error", "err": "Error", "eror": "Error", "fatal": "Fatal",
    "critical": "Fatal", "crit": "Fatal", "ftl": "Fatal",
}

# Leading timestamp in the shapes that actually turn up in log files.
TIMESTAMP_PATTERNS = [
    re.compile(r"^\[?(?P<ts>\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?)\]?"),
    re.compile(r"^\[?(?P<ts>\d{2}/\w{3}/\d{4}[: ]\d{2}:\d{2}:\d{2})\]?"),
]

LEVEL_PATTERN = re.compile(
    r"\b(TRACE|VERBOSE|DEBUG|DBG|INFO|INFORMATION|INF|NOTICE|WARN|WARNING|WRN|ERROR|ERR|EROR|FATAL|CRITICAL|CRIT|FTL)\b",
    re.IGNORECASE,
)

# "System.TimeoutException: message" / "java.lang.NullPointerException: message"
EXCEPTION_PATTERN = re.compile(r"\b(?P<type>(?:[A-Za-z_][\w]*\.)+[A-Z]\w*(?:Exception|Error))\b\s*:?")

# A line that continues the one above it: an indented frame, "at Foo.Bar(...)",
# "Caused by:", or a chained "--->".
CONTINUATION_PATTERN = re.compile(r"^(\s+|at\s|Caused by:|\.\.\.\s|--->|\tat\s)")


@dataclass
class ParsedEvent:
    message: str
    severity: str = "Information"
    timestamp: datetime | None = None
    exception_type: str | None = None
    stack_lines: list[str] = field(default_factory=list)

    @property
    def stack_trace(self) -> str | None:
        return "\n".join(self.stack_lines) if self.stack_lines else None


def parse_timestamp(raw: str) -> datetime | None:
    cleaned = raw.strip().replace(",", ".")

    for candidate in (cleaned, cleaned.replace(" ", "T")):
        try:
            parsed = datetime.fromisoformat(candidate)
            return parsed if parsed.tzinfo else parsed.replace(tzinfo=UTC)
        except ValueError:
            continue

    for fmt in ("%d/%b/%Y:%H:%M:%S", "%d/%b/%Y %H:%M:%S"):
        try:
            return datetime.strptime(cleaned, fmt).replace(tzinfo=UTC)
        except ValueError:
            continue

    return None


def parse_structured(line: str) -> ParsedEvent | None:
    """Structured JSON, including Serilog's compact format (@t, @l, @m, @x)."""
    stripped = line.strip()
    if not (stripped.startswith("{") and stripped.endswith("}")):
        return None

    try:
        record = json.loads(stripped)
    except json.JSONDecodeError:
        return None

    if not isinstance(record, dict):
        return None

    def pick(*names: str) -> str | None:
        for name in names:
            value = record.get(name)
            if isinstance(value, str) and value.strip():
                return value
        return None

    message = pick("@m", "@mt", "message", "msg", "Message", "event", "log") or stripped
    raw_level = pick("@l", "level", "severity", "levelname", "Level") or "Information"
    raw_time = pick("@t", "timestamp", "time", "@timestamp", "Timestamp", "asctime")
    exception = pick("@x", "exception", "exc_info", "Exception", "stack_trace", "stackTrace")

    event = ParsedEvent(
        message=message,
        # Serilog writes "Warning"; Python's logging writes "WARNING". Both land
        # on the same canonical value.
        severity=LEVEL_WORDS.get(raw_level.strip().lower(), "Information"),
        timestamp=parse_timestamp(raw_time) if raw_time else None,
    )

    if exception:
        event.stack_lines = exception.splitlines()
        match = EXCEPTION_PATTERN.search(exception)
        if match:
            event.exception_type = match.group("type")

    if not event.exception_type:
        match = EXCEPTION_PATTERN.search(message)
        if match:
            event.exception_type = match.group("type")

    return event


def parse_plain(line: str) -> ParsedEvent:
    """Plain text: pull off a leading timestamp and a level word, keep the rest."""
    remainder = line.rstrip()
    timestamp = None

    for pattern in TIMESTAMP_PATTERNS:
        match = pattern.match(remainder)
        if match:
            timestamp = parse_timestamp(match.group("ts"))
            remainder = remainder[match.end():].lstrip(" -\t|")
            break

    severity = "Information"
    level_match = LEVEL_PATTERN.search(remainder[:60])
    if level_match:
        severity = LEVEL_WORDS.get(level_match.group(1).lower(), "Information")
        # Only strip the level when it sits at the front; a level word inside
        # the message is part of the message.
        if level_match.start() < 20:
            remainder = (remainder[:level_match.start()] + remainder[level_match.end():]).lstrip(" -\t|:[]")

    exception_type = None
    exception_match = EXCEPTION_PATTERN.search(remainder)
    if exception_match:
        exception_type = exception_match.group("type")

    return ParsedEvent(
        message=remainder.strip() or line.strip(),
        severity=severity,
        timestamp=timestamp,
        exception_type=exception_type,
    )


def parse_log(lines: list[str]) -> list[ParsedEvent]:
    events: list[ParsedEvent] = []

    for raw in lines:
        if not raw.strip():
            continue

        # A continuation belongs to the event above it. Treating it as its own
        # event would create one pattern per stack frame.
        if events and CONTINUATION_PATTERN.match(raw) and not raw.strip().startswith("{"):
            events[-1].stack_lines.append(raw.rstrip())

            if not events[-1].exception_type:
                match = EXCEPTION_PATTERN.search(raw)
                if match:
                    events[-1].exception_type = match.group("type")
            continue

        events.append(parse_structured(raw) or parse_plain(raw))

    return events


def to_api_events(
    parsed: list[ParsedEvent], service: str, environment: str, shift: timedelta
) -> list[dict]:
    payload = []

    for event in parsed:
        when = (event.timestamp + shift) if event.timestamp else datetime.now(UTC)

        item = {
            "eventId": str(uuid.uuid4()),
            "service": service,
            "environment": environment,
            "timestamp": when.isoformat(),
            "severity": event.severity,
            "message": event.message[:8000],
            "metadata": {"uploadedBy": "upload-log.py"},
        }

        if event.exception_type:
            item["exceptionType"] = event.exception_type[:500]
        if event.stack_trace:
            item["stackTrace"] = event.stack_trace[:32000]

        payload.append(item)

    return payload


def post_batch(url: str, api_key: str, events: list[dict]) -> dict:
    request = urllib.request.Request(
        url,
        data=json.dumps({"events": events}).encode(),
        headers={"Content-Type": "application/json", "X-Api-Key": api_key},
        method="POST",
    )

    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.loads(response.read())
    except urllib.error.HTTPError as error:
        body = error.read().decode(errors="replace")
        raise SystemExit(f"Ingestion rejected the batch ({error.code}): {body}") from error
    except urllib.error.URLError as error:
        raise SystemExit(
            f"Could not reach ingestion at {url}: {error.reason}\n"
            "Is the stack running? Try: make up"
        ) from error


def _describe(delta: timedelta) -> str:
    minutes = abs(delta.total_seconds()) / 60
    if minutes < 90:
        return f"{minutes:.0f} minute(s)"
    hours = minutes / 60
    return f"{hours:.1f} hour(s)" if hours < 48 else f"{hours / 24:.1f} day(s)"


def load_env() -> dict[str, str]:
    env_file = REPO_ROOT / "infrastructure" / "docker" / ".env"
    values: dict[str, str] = {}

    if env_file.exists():
        for line in env_file.read_text().splitlines():
            if "=" in line and not line.strip().startswith("#"):
                key, _, value = line.partition("=")
                values[key.strip()] = value.strip()

    return values


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Send a real log file to IncidentIQ.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument("logfile", nargs="?", help="Log file to upload. Omit to read stdin.")
    parser.add_argument("--service", default="payments-api", help="Service the log came from.")
    parser.add_argument("--environment", default="production")
    parser.add_argument("--errors-only", action="store_true",
                        help="Send only Error and Fatal lines.")
    parser.add_argument("--replay-as-now", action="store_true",
                        help="Shift every timestamp so the log ends now, preserving the "
                             "intervals between lines. Detection only looks at a short "
                             "recent window, so a historical log needs this to open an "
                             "incident.")
    parser.add_argument("--dry-run", action="store_true",
                        help="Show what would be sent without sending it.")
    parser.add_argument("--watch", action="store_true",
                        help="Wait for the analysis and print it.")
    args = parser.parse_args()

    raw = Path(args.logfile).read_text(errors="replace").splitlines() if args.logfile \
        else sys.stdin.read().splitlines()

    if not raw:
        print("Nothing to upload: the input was empty.", file=sys.stderr)
        return 1

    events = parse_log(raw)

    if args.errors_only:
        events = [e for e in events if e.severity in ("Error", "Fatal")]

    if not events:
        print("No events to send after parsing.", file=sys.stderr)
        return 1

    shift = timedelta(0)
    dated = [e.timestamp for e in events if e.timestamp]

    if dated:
        oldest, newest = min(dated), max(dated)
        now = datetime.now(UTC)

        if args.replay_as_now:
            # Land the last line just before now and pull everything else with
            # it, so the shape of the burst is preserved and the detector sees
            # it as current traffic.
            shift = now - newest - timedelta(seconds=5)
            print(f"Replaying as now: shifting timestamps forward by {_describe(shift)}.")
        elif now - oldest > MAX_EVENT_AGE:
            # Older than the ingestion validator accepts; without a shift most
            # of the file would simply be rejected.
            shift = (now - oldest) - timedelta(hours=1)
            print(f"Log is older than the {MAX_EVENT_AGE.days}-day ingestion window; "
                  f"shifting forward by {_describe(shift)} so it is accepted.")
        elif now - newest > DETECTION_WINDOW:
            # The important one. Everything will be stored and fingerprinted,
            # and no incident will open, which looks like a broken pipeline
            # unless it is said out loud.
            print(
                f"\n  Note: the newest line is {_describe(now - newest)} old, and detection "
                f"only\n  examines the last {int(DETECTION_WINDOW.total_seconds() // 60)} minutes. "
                f"Patterns will be recorded but no incident\n  will open. Re-run with "
                f"--replay-as-now to see detection and analysis.\n"
            )

    by_severity: dict[str, int] = {}
    for event in events:
        by_severity[event.severity] = by_severity.get(event.severity, 0) + 1

    stacks = sum(1 for e in events if e.stack_lines)
    print(f"Parsed {len(raw)} line(s) into {len(events)} event(s) "
          f"({stacks} with a stack trace attached)")
    print("  " + "  ".join(f"{level}={count}" for level, count in sorted(by_severity.items())))

    payload = to_api_events(events, args.service, args.environment, shift)

    if args.dry_run:
        print("\n--- first event as it would be sent ---")
        print(json.dumps(payload[0], indent=2))
        return 0

    env = load_env()
    api_key = os.environ.get("INGESTION_API_KEY") or env.get("INGESTION_API_KEY") \
        or "iiq_dev_0123456789abcdef"
    port = os.environ.get("INGESTION_HOST_PORT") or env.get("INGESTION_HOST_PORT") or "5081"
    url = f"http://localhost:{port}/api/v1/logs/batch"

    accepted = rejected = 0
    for start in range(0, len(payload), BATCH_SIZE):
        result = post_batch(url, api_key, payload[start:start + BATCH_SIZE])
        accepted += result.get("accepted", 0)
        rejected += result.get("rejected", 0)

        for error in result.get("errors", [])[:3]:
            print(f"  rejected #{error['index']}: {error['field']} - {error['message']}")

    print(f"\nAccepted {accepted}, rejected {rejected}.")

    if args.watch:
        print("\nWaiting for detection and analysis...\n")
        os.execv("/bin/sh", ["/bin/sh", "-c",
                             f"cd {REPO_ROOT} && ./scripts/show-analysis.sh --watch"])
    else:
        print("Now run: ./scripts/show-analysis.sh --watch")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
