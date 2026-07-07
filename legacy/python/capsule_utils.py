import hashlib
import json
import os
import time
import zipfile

from file_utils import sanitize_filename
from launcher import get_version_json
from modpack_utils import detect_export_loader
from version_utils import (
    resolve_base_minecraft_version,
    runtime_directory_for_version,
)


CAPSULE_MANIFEST = "mcgo-capsule.json"
CAPSULE_SCHEMA_VERSION = 1
CAPSULE_ALLOWED_ROOTS = {
    "mods",
    "config",
    "resourcepacks",
    "shaderpacks",
    "datapacks",
}
CAPSULE_SKIP_DIRS = {
    ".git",
    "__pycache__",
    "cache",
    "caches",
    "logs",
    "crash-reports",
    "screenshots",
    "saves",
}
CAPSULE_SKIP_SUFFIXES = (".log", ".tmp", ".lock", ".lck", ".bak")
CAPSULE_SENSITIVE_NAMES = {
    "accounts.json",
    "launcher_accounts.json",
    "launcher_profiles.json",
    "options.txt",
    "servers.dat",
    "usercache.json",
}


def _normalize_relative(relative_path):
    return os.path.normpath(relative_path).replace("\\", "/").strip("/")


def should_include_capsule_file(relative_path):
    normalized = _normalize_relative(relative_path)
    if not normalized:
        return False
    parts = [part for part in normalized.split("/") if part]
    if not parts or parts[0].lower() not in CAPSULE_ALLOWED_ROOTS:
        return False
    lowered_parts = {part.lower() for part in parts}
    if lowered_parts & CAPSULE_SKIP_DIRS:
        return False
    lowered_name = parts[-1].lower()
    if lowered_name in CAPSULE_SENSITIVE_NAMES:
        return False
    if lowered_name.endswith(CAPSULE_SKIP_SUFFIXES):
        return False
    return True


def iter_capsule_files(runtime_dir):
    for root_name in sorted(CAPSULE_ALLOWED_ROOTS):
        root_dir = os.path.join(runtime_dir, root_name)
        if not os.path.isdir(root_dir):
            continue
        for current_root, dirnames, files in os.walk(root_dir):
            dirnames[:] = [
                name for name in dirnames
                if name.lower() not in CAPSULE_SKIP_DIRS
            ]
            for filename in files:
                path = os.path.join(current_root, filename)
                relative = _normalize_relative(os.path.relpath(path, runtime_dir))
                if should_include_capsule_file(relative):
                    yield path, relative


def file_sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _safe_join(root, relative_path):
    normalized = os.path.normpath(relative_path).replace("\\", os.sep).lstrip(os.sep)
    target = os.path.abspath(os.path.join(root, normalized))
    root_abs = os.path.abspath(root)
    if not target.startswith(root_abs + os.sep) and target != root_abs:
        raise RuntimeError(f"胶囊包含不安全路径：{relative_path}")
    return target


def export_capsule(game_dir, settings, version_id, target_path, p2p_info=None, global_isolation=False):
    runtime_dir = runtime_directory_for_version(game_dir, settings, version_id, global_isolation=global_isolation)
    if not os.path.isdir(runtime_dir):
        raise RuntimeError(f"运行目录不存在：{runtime_dir}")

    files = []
    loader_key, loader_version = detect_export_loader(game_dir, version_id)
    entry = settings.get(version_id, {}) if isinstance(settings, dict) else {}
    capsule_name = (entry.get("alias") or version_id).strip() or version_id
    manifest = {
        "schema": "mcgo.capsule",
        "schemaVersion": CAPSULE_SCHEMA_VERSION,
        "createdAt": int(time.time()),
        "sourceVersion": version_id,
        "name": capsule_name,
        "minecraft": resolve_base_minecraft_version(game_dir, version_id),
        "loader": loader_key,
        "loaderVersion": loader_version,
        "p2p": dict(p2p_info or {}),
        "files": files,
    }
    version_json = get_version_json(game_dir, version_id)
    if version_json:
        manifest["versionJson"] = version_json

    with zipfile.ZipFile(target_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for file_path, relative in iter_capsule_files(runtime_dir):
            size = os.path.getsize(file_path)
            sha256 = file_sha256(file_path)
            files.append({
                "path": relative,
                "size": size,
                "sha256": sha256,
            })
        archive.writestr(CAPSULE_MANIFEST, json.dumps(manifest, ensure_ascii=False, indent=2))

    return {
        "name": manifest["name"],
        "version": version_id,
        "minecraft": manifest["minecraft"],
        "loader": loader_key,
        "file_count": len(files),
        "total_size": sum(item["size"] for item in files),
        "target_path": target_path,
    }


def read_capsule_manifest(capsule_path):
    if not zipfile.is_zipfile(capsule_path):
        raise RuntimeError("不是有效的 McGo 开黑胶囊文件。")
    with zipfile.ZipFile(capsule_path, "r") as archive:
        if CAPSULE_MANIFEST not in archive.namelist():
            raise RuntimeError("胶囊缺少 mcgo-capsule.json。")
        with archive.open(CAPSULE_MANIFEST) as handle:
            manifest = json.loads(handle.read().decode("utf-8-sig"))
    if manifest.get("schema") != "mcgo.capsule":
        raise RuntimeError("胶囊格式不受支持。")
    return manifest


def import_capsule(capsule_path, game_dir, settings, target_version_id="", global_isolation=True):
    manifest = read_capsule_manifest(capsule_path)
    source_version = str(manifest.get("sourceVersion") or "capsule")
    capsule_name = str(manifest.get("name") or source_version or "开黑胶囊").strip()
    version_id = target_version_id.strip() or sanitize_filename(capsule_name, "McGoCapsule")
    runtime_dir = os.path.join(game_dir, "versions", version_id)
    os.makedirs(runtime_dir, exist_ok=True)

    version_json = manifest.get("versionJson")
    if isinstance(version_json, dict):
        version_dir = os.path.join(game_dir, "versions", version_id)
        os.makedirs(version_dir, exist_ok=True)
        copied_json = dict(version_json)
        copied_json["id"] = version_id
        with open(os.path.join(version_dir, f"{version_id}.json"), "w", encoding="utf-8") as handle:
            json.dump(copied_json, handle, ensure_ascii=False, indent=4)

    entry = settings.setdefault(version_id, {})
    entry.setdefault("alias", capsule_name)
    entry.setdefault("alias_auto", True)
    entry["use_isolated_directory"] = True
    entry.setdefault("source_capsule", os.path.abspath(capsule_path))

    missing_files = capsule_missing_files(runtime_dir, manifest.get("files", []))
    return {
        "version": version_id,
        "name": capsule_name,
        "minecraft": manifest.get("minecraft", ""),
        "loader": manifest.get("loader", ""),
        "p2p": manifest.get("p2p", {}) if isinstance(manifest.get("p2p", {}), dict) else {},
        "files": manifest.get("files", []),
        "missing_files": missing_files,
        "extracted": 0,
        "verified": 0,
        "runtime_dir": runtime_dir,
    }


def capsule_missing_files(runtime_dir, files):
    missing = []
    for item in files or []:
        relative = _normalize_relative(item.get("path", ""))
        if not should_include_capsule_file(relative):
            continue
        target = _safe_join(runtime_dir, relative)
        expected_sha256 = item.get("sha256", "")
        if not os.path.isfile(target):
            missing.append(item)
            continue
        if expected_sha256 and file_sha256(target) != expected_sha256:
            missing.append(item)
    return missing


def capsule_target_path(runtime_dir, relative_path):
    relative = _normalize_relative(relative_path)
    if not should_include_capsule_file(relative):
        raise RuntimeError(f"胶囊文件路径不允许：{relative_path}")
    return _safe_join(runtime_dir, relative)
