#!/usr/bin/env python3
"""Accumulate anonymous website statistics in private, persistent SQLite state."""

import argparse
from collections import Counter
from contextlib import contextmanager, closing
from datetime import datetime, timezone
import errno
import glob
import gzip
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import secrets
import sqlite3
import sys
import tempfile
import time
from urllib.parse import urlsplit
import zlib


LOG_PATTERN = re.compile(
    r'^(?P<ip>\S+)\s+\S+\s+\S+\s+\[(?P<time>[^]]+)]\s+'
    r'"(?P<request>[^"]*)"\s+'
    r'(?P<status>\d{3})\s+\S+'
)
PAGE_PATHS = {"/", "/index.html", "/privacy.html"}
DOWNLOAD_PATHS = {
    "/download/McGo.exe": "stable",
    "/download/McGo-CSharp-Preview-x64-Setup.exe": "preview",
    "/download/McGo-CSharp-Preview-x64.zip": "legacy_preview",
}
BUCKETS = ("page_views", "stable", "preview", "legacy_preview")


class StatsError(Exception):
    pass


def default_state(output):
    root = os.environ.get("LOCALAPPDATA") if os.name == "nt" else os.environ.get("XDG_STATE_HOME")
    root = Path(root) if root else Path.home() / ".local" / "state"
    name = hashlib.sha256(os.fsencode(os.path.normcase(str(Path(output).resolve())))).hexdigest()[:24]
    return root / "mcgo" / "stats" / (name + ".sqlite3")


def private_state_path(state, output):
    state, output = Path(state).resolve(), Path(output).resolve()
    public_roots = [output.parent]
    public_roots.extend(p for p in output.parents if p.name.lower() in {"public", "www", "wwwroot", "htdocs"})
    if any(state == root or root in state.parents for root in public_roots):
        raise StatsError("--state must be outside the output directory and public web roots")
    if output == Path(str(state) + ".lock"):
        raise StatsError("Output must not overwrite the state lock")
    return state, output


@contextmanager
def state_lock(state, timeout=60):
    """Hold a process lock through scan, SQLite commit, and JSON publication."""
    state.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    lock_path = Path(str(state) + ".lock")
    fd = os.open(lock_path, os.O_CREAT | os.O_RDWR, 0o600)
    with os.fdopen(fd, "r+b", buffering=0) as lock:
        os.chmod(lock_path, 0o600)
        deadline = time.monotonic() + timeout
        while True:
            try:
                lock.seek(0)
                if os.name == "nt":
                    import msvcrt
                    msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except OSError as exc:
                if exc.errno not in (errno.EACCES, errno.EAGAIN, errno.EDEADLK):
                    raise
                if time.monotonic() >= deadline:
                    raise StatsError("Timed out waiting for the statistics state lock") from exc
                time.sleep(0.05)
        try:
            yield
        finally:
            lock.seek(0)
            if os.name == "nt":
                msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(lock.fileno(), fcntl.LOCK_UN)


def fingerprint(key, kind, value):
    return hmac.new(key, kind + b"\0" + value.encode("utf-8"), hashlib.sha256).hexdigest()


def earlier(left, right):
    if not left:
        return right
    if not right:
        return left
    return min((left, right), key=datetime.fromisoformat)


def check_snapshot(path, before, after):
    compressed = path.endswith(".gz")
    if ((before.st_dev, before.st_ino) != (after.st_dev, after.st_ino)
            or after.st_size < before.st_size
            or (after.st_mtime_ns != before.st_mtime_ns
                and (compressed or after.st_size == before.st_size))
            or (compressed and after.st_size != before.st_size)):
        raise StatsError(f"Log changed while being read: {path}")


def read_log(path, key):
    counts, visitors = Counter(), set()
    since, valid = None, 0
    compressed = path.endswith(".gz")
    opener = gzip.open if compressed else open
    with opener(path, "rb") as stream:
        before = os.fstat(stream.fileno())
        remaining = before.st_size
        line_number = 0
        while compressed or remaining:
            raw = stream.readline() if compressed else stream.readline(remaining)
            if not raw:
                break
            remaining -= len(raw)
            line_number += 1
            # A writer may still be appending the final record; retry it next run.
            if not raw.endswith(b"\n"):
                continue
            line = raw.decode("utf-8").rstrip("\r\n")
            if not line:
                continue
            match = LOG_PATTERN.match(line)
            if not match:
                raise StatsError(f"Unsupported log record in {path}:{line_number}")
            values = match.groupdict()
            try:
                timestamp = datetime.strptime(values["time"], "%d/%b/%Y:%H:%M:%S %z").isoformat()
            except ValueError as exc:
                raise StatsError(f"Invalid log record in {path}:{line_number}") from exc
            valid += 1
            request = values["request"].split(None, 2)
            if len(request) != 3 or request[0] != "GET" or not 200 <= int(values["status"]) < 400:
                continue
            try:
                target = urlsplit(request[1]).path
            except ValueError as exc:
                raise StatsError(f"Invalid request target in {path}:{line_number}") from exc
            since = earlier(since, timestamp)
            bucket = "page_views" if target in PAGE_PATHS else DOWNLOAD_PATHS.get(target)
            if bucket:
                counts[(fingerprint(key, b"event", line), bucket)] += 1
            if target in PAGE_PATHS:
                visitors.add(fingerprint(key, b"visitor", values["ip"]))
        check_snapshot(path, before, os.fstat(stream.fileno()))
    return counts, visitors, since, valid


def scan(pattern, key):
    paths = sorted(set(glob.glob(pattern)))
    if not paths:
        raise StatsError("No logs matched; keeping the last valid output")
    snapshots = {path: os.stat(path) for path in paths}
    groups, visitors, since, valid = {}, set(), None, 0
    for path in paths:
        counts, found, first, records = read_log(path, key)
        # logrotate can expose both foo.1 and foo.1.gz during compression.
        group = path[:-3] if path.endswith(".gz") else path
        groups[group] = groups.get(group, Counter()) | counts
        visitors.update(found)
        since = earlier(since, first)
        valid += records
    if paths != sorted(set(glob.glob(pattern))):
        raise StatsError("Log file set changed during the scan; retry after rotation")
    for path, before in snapshots.items():
        check_snapshot(path, before, os.stat(path))
    if not valid:
        raise StatsError("No complete valid log records; keeping the last valid output")
    counts = Counter()
    for group in groups.values():
        counts.update(group)
    return counts, visitors, since


def read_baseline(output):
    if not output.exists():
        return None
    try:
        data = json.loads(output.read_text(encoding="utf-8"))
        if type(data["schema"]) is not int or data["schema"] != 1:
            raise ValueError("Unsupported schema")
        values = [data["page_views"], data["unique_visitors"], data["downloads"]["total"]]
        values.extend(data["downloads"][bucket] for bucket in BUCKETS[1:])
        if any(type(value) is not int or value < 0 for value in values):
            raise ValueError("Invalid counter")
        if data["downloads"]["total"] != sum(values[3:]) or data["unique_visitors"] > data["page_views"]:
            raise ValueError("Inconsistent counters")
        if data["since"] is not None:
            if datetime.fromisoformat(data["since"]).utcoffset() is None:
                raise ValueError("Timestamp needs a timezone")
        return data
    except (ValueError, KeyError, TypeError, AttributeError) as exc:
        raise StatsError("Existing output is not valid schema-1 statistics; migration aborted") from exc


def atomic_write(path, data):
    target = Path(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix=f".{target.name}.", suffix=".tmp", dir=target.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(data, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(name, 0o644)
        os.replace(name, target)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def update_stats(pattern, output, state=None):
    state, output = private_state_path(state or default_state(output), output)
    with state_lock(state):
        fd = os.open(state, os.O_CREAT | os.O_RDWR, 0o600)
        os.close(fd)
        os.chmod(state, 0o600)
        with closing(sqlite3.connect(state, timeout=60)) as db:
            db.execute("PRAGMA synchronous = FULL")
            with db:
                db.execute("BEGIN IMMEDIATE")
                version = db.execute("PRAGMA user_version").fetchone()[0]
                if version not in (0, 1):
                    raise StatsError(f"Unsupported private state version: {version}")
                db.execute("CREATE TABLE IF NOT EXISTS metadata (id INTEGER PRIMARY KEY CHECK(id=1), value TEXT NOT NULL)")
                db.execute("CREATE TABLE IF NOT EXISTS events (signature TEXT PRIMARY KEY, bucket TEXT NOT NULL, occurrences INTEGER NOT NULL)")
                db.execute("CREATE TABLE IF NOT EXISTS visitors (signature TEXT PRIMARY KEY)")
                row = db.execute("SELECT value FROM metadata WHERE id=1").fetchone()
                if version == 1 and row is None:
                    raise StatsError("Private state metadata is missing; restore a state backup")
                config = json.loads(row[0]) if row else {
                    "key": secrets.token_hex(32), "offsets": dict.fromkeys(BUCKETS, 0),
                    "visitor_floor": 0, "since": None, "migrated": False,
                }
                counts, visitors, since = scan(pattern, bytes.fromhex(config["key"]))
                if not row:
                    baseline = read_baseline(output)
                    if baseline:
                        totals = Counter()
                        for (_, bucket), count in counts.items():
                            totals[bucket] += count
                        # Anchor this first scan once; future runs add event deltas.
                        for bucket in BUCKETS:
                            previous = baseline[bucket] if bucket == "page_views" else baseline["downloads"][bucket]
                            config["offsets"][bucket] = max(0, previous - totals[bucket])
                        config.update(visitor_floor=baseline["unique_visitors"], since=baseline["since"], migrated=True)
                db.executemany(
                    "INSERT INTO events VALUES (?, ?, ?) ON CONFLICT(signature) DO UPDATE "
                    "SET occurrences = MAX(events.occurrences, excluded.occurrences)",
                    ((signature, bucket, count) for (signature, bucket), count in counts.items()),
                )
                db.executemany("INSERT OR IGNORE INTO visitors VALUES (?)", ((visitor,) for visitor in visitors))
                config["since"] = earlier(config["since"], since)
                db.execute("INSERT OR REPLACE INTO metadata VALUES (1, ?)", (json.dumps(config),))
                db.execute("PRAGMA user_version = 1")
            # Commit before publishing. A failed replace can be retried without recounting.
            totals = dict(config["offsets"])
            for bucket, count in db.execute("SELECT bucket, SUM(occurrences) FROM events GROUP BY bucket"):
                totals[bucket] += count
            downloads = {bucket: totals[bucket] for bucket in BUCKETS[1:]}
            data = {
                "schema": 1, "page_views": totals["page_views"],
                "unique_visitors": max(config["visitor_floor"], db.execute("SELECT COUNT(*) FROM visitors").fetchone()[0]),
                "downloads": {"total": sum(downloads.values()), **downloads},
                "since": config["since"], "generated_at": datetime.now(timezone.utc).isoformat(),
                "history": {
                    "migration": "anchored_existing_output" if config["migrated"] else "none",
                    "unique_visitors_is_lower_bound": config["visitor_floor"] > 0,
                },
            }
            atomic_write(output, data)
            return data


def main(argv=None):
    parser = argparse.ArgumentParser(description="Generate anonymous McGo website statistics.")
    parser.add_argument("--log-pattern", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--state", help="Private persistent SQLite path, outside all served directories")
    args = parser.parse_args(argv)
    try:
        update_stats(args.log_pattern, args.output, args.state)
    except (StatsError, OSError, EOFError, UnicodeError, ValueError, sqlite3.Error, zlib.error) as exc:
        print(f"Statistics update failed: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
