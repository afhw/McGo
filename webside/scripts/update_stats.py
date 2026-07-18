#!/usr/bin/env python3
import argparse
import glob
import gzip
import json
import os
import re
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlsplit


LOG_PATTERN = re.compile(
    r'^(?P<ip>\S+)\s+\S+\s+\S+\s+\[(?P<time>[^]]+)]\s+'
    r'"(?P<method>\S+)\s+(?P<target>\S+)\s+[^\"]+"\s+'
    r'(?P<status>\d{3})\s+\S+'
)
PAGE_PATHS = {"/", "/index.html", "/privacy.html"}
DOWNLOAD_PATHS = {
    "/download/McGo.exe": "stable",
    "/download/McGo-CSharp-Preview-x64-Setup.exe": "preview",
    "/download/McGo-CSharp-Preview-x64.zip": "legacy_preview",
}


def iter_log_lines(pattern):
    paths = sorted(set(glob.glob(pattern)), key=lambda value: os.path.getmtime(value))
    for path in paths:
        opener = gzip.open if path.endswith(".gz") else open
        try:
            with opener(path, "rt", encoding="utf-8", errors="replace") as stream:
                yield from stream
        except OSError:
            continue


def collect(pattern):
    page_views = 0
    visitors = set()
    downloads = {"stable": 0, "preview": 0, "legacy_preview": 0}
    first_seen = None

    for line in iter_log_lines(pattern):
        match = LOG_PATTERN.match(line)
        if not match:
            continue

        values = match.groupdict()
        if values["method"] != "GET" or not 200 <= int(values["status"]) < 400:
            continue

        path = urlsplit(values["target"]).path
        try:
            timestamp = datetime.strptime(values["time"], "%d/%b/%Y:%H:%M:%S %z")
            first_seen = timestamp if first_seen is None or timestamp < first_seen else first_seen
        except ValueError:
            pass

        if path in PAGE_PATHS:
            page_views += 1
            visitors.add(values["ip"])

        release = DOWNLOAD_PATHS.get(path)
        if release is not None:
            downloads[release] += 1

    return {
        "schema": 1,
        "page_views": page_views,
        "unique_visitors": len(visitors),
        "downloads": {
            "total": sum(downloads.values()),
            **downloads,
        },
        "since": first_seen.isoformat() if first_seen else None,
        "generated_at": datetime.now(timezone.utc).isoformat(),
    }


def atomic_write(path, data):
    target = Path(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_name(f".{target.name}.{os.getpid()}.tmp")
    with temporary.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(data, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, target)
    target.chmod(0o644)


def main():
    parser = argparse.ArgumentParser(description="Generate anonymous McGo website statistics.")
    parser.add_argument("--log-pattern", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    atomic_write(args.output, collect(args.log_pattern))


if __name__ == "__main__":
    main()
