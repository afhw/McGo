import argparse
import hashlib
import json
import os
import shutil
from datetime import datetime, timezone


BUFFER_SIZE = 1024 * 1024


def file_sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(BUFFER_SIZE), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main(argv=None):
    parser = argparse.ArgumentParser(description="Prepare McGo website release files")
    parser.add_argument("--version", required=True)
    parser.add_argument("--exe", default=os.path.join("dist", "McGo.exe"))
    parser.add_argument("--web-dist", default=os.path.join("webside", "dist"))
    parser.add_argument("--base-url", default="https://mcgo.flyliq.cn")
    parser.add_argument("--notes", default="")
    args = parser.parse_args(argv)

    exe_path = os.path.abspath(args.exe)
    web_dist = os.path.abspath(args.web_dist)
    if not os.path.isfile(exe_path):
        raise SystemExit(f"Executable not found: {exe_path}")
    download_dir = os.path.join(web_dist, "download")
    os.makedirs(download_dir, exist_ok=True)
    target_exe = os.path.join(download_dir, "McGo.exe")
    shutil.copy2(exe_path, target_exe)
    size = os.path.getsize(target_exe)
    digest = file_sha256(target_exe)
    base_url = args.base_url.rstrip("/")
    manifest = {
        "version": args.version,
        "url": f"{base_url}/download/McGo.exe",
        "sha256": digest,
        "size": size,
        "published_at": datetime.now(timezone.utc).isoformat(),
        "notes": args.notes,
    }
    manifest_path = os.path.join(web_dist, "update.json")
    with open(manifest_path, "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
    print(json.dumps({"manifest": manifest_path, "exe": target_exe, **manifest}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
