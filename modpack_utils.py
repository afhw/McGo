import json
import os
import re
import zipfile

from file_utils import sanitize_filename
from launcher import get_version_json
from version_utils import (
    resolve_base_minecraft_version,
    runtime_directory_for_version,
    version_display_name,
)


def should_skip_pack_file(relative_path):
    normalized = relative_path.replace("\\", "/").strip("/")
    lowered = normalized.lower()
    if not normalized:
        return True
    if lowered.startswith((".git/", "__pycache__/", "logs/", "crash-reports/")):
        return True
    if lowered in {"options.txt", "launcher_profiles.json"}:
        return True
    if lowered.endswith((".log", ".tmp", ".lock")):
        return True
    return False


def iter_pack_files(root_dir):
    for current_root, _, files in os.walk(root_dir):
        for filename in files:
            path = os.path.join(current_root, filename)
            relative = os.path.relpath(path, root_dir).replace("\\", "/")
            if should_skip_pack_file(relative):
                continue
            yield path, relative


def detect_export_loader(game_dir, version_id):
    version_json = get_version_json(game_dir, version_id) or {}
    text = " ".join([
        version_id,
        version_json.get("id", ""),
        version_json.get("inheritsFrom", ""),
        " ".join(lib.get("name", "") for lib in version_json.get("libraries", []) if isinstance(lib, dict)),
    ]).lower()
    if "fabric-loader" in text:
        match = re.search(r"fabric-loader[:/-]([0-9][^:\s/]*)", text)
        return "fabric-loader", match.group(1) if match else ""
    if "neoforge" in text:
        match = re.search(r"neoforge[:/-]([0-9][^:\s/]*)", text)
        return "neoforge", match.group(1) if match else ""
    if "net.minecraftforge" in text or "forge-" in text:
        match = re.search(r"forge[:/-]([0-9][^:\s/]*)", text)
        return "forge", match.group(1) if match else ""
    if "quilt-loader" in text:
        match = re.search(r"quilt-loader[:/-]([0-9][^:\s/]*)", text)
        return "quilt-loader", match.group(1) if match else ""
    return "", ""


def export_modpack(game_dir, settings, version_id, target_path, pack_format="modrinth", global_isolation=False):
    runtime_dir = runtime_directory_for_version(game_dir, settings, version_id, global_isolation=global_isolation)
    if not os.path.isdir(runtime_dir):
        raise RuntimeError(f"运行目录不存在：{runtime_dir}")

    minecraft_version = resolve_base_minecraft_version(game_dir, version_id)
    pack_name = version_display_name(settings, version_id)
    extension = os.path.splitext(target_path)[1].lower()
    if extension == ".mrpack":
        pack_format = "modrinth"
    loader_key, loader_version = detect_export_loader(game_dir, version_id)
    added = 0

    with zipfile.ZipFile(target_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for file_path, relative in iter_pack_files(runtime_dir):
            archive.write(file_path, f"overrides/{relative}")
            added += 1

        if pack_format == "modrinth":
            dependencies = {"minecraft": minecraft_version}
            if loader_key and loader_version:
                dependencies[loader_key] = loader_version
            index = {
                "formatVersion": 1,
                "game": "minecraft",
                "versionId": sanitize_filename(version_id, "version"),
                "name": pack_name,
                "summary": f"Exported by McGo from {version_id}",
                "files": [],
                "dependencies": dependencies,
            }
            archive.writestr("modrinth.index.json", json.dumps(index, ensure_ascii=False, indent=2))
        elif pack_format == "curseforge":
            loader_id = ""
            if loader_key == "fabric-loader":
                loader_id = f"fabric-{loader_version}" if loader_version else "fabric"
            elif loader_key:
                loader_id = f"{loader_key}-{loader_version}" if loader_version else loader_key
            manifest = {
                "minecraft": {
                    "version": minecraft_version,
                    "modLoaders": [{"id": loader_id, "primary": True}] if loader_id else [],
                },
                "manifestType": "minecraftModpack",
                "manifestVersion": 1,
                "name": pack_name,
                "version": "1.0.0",
                "author": "McGo",
                "files": [],
                "overrides": "overrides",
            }
            archive.writestr("manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2))
    return added


def analyze_crash_logs(game_dir, version_id, runtime_dir):
    candidates = []
    for base in (runtime_dir, os.path.join(game_dir, "versions", version_id), game_dir):
        if not base:
            continue
        latest_log = os.path.join(base, "logs", "latest.log")
        if os.path.isfile(latest_log):
            candidates.append(latest_log)
        crash_dir = os.path.join(base, "crash-reports")
        if os.path.isdir(crash_dir):
            reports = [
                os.path.join(crash_dir, name)
                for name in os.listdir(crash_dir)
                if name.lower().endswith(".txt")
            ]
            candidates.extend(sorted(reports, key=os.path.getmtime, reverse=True)[:3])

    unique = []
    seen = set()
    for path in candidates:
        absolute = os.path.abspath(path)
        if absolute not in seen:
            seen.add(absolute)
            unique.append(absolute)

    if not unique:
        return "未找到 latest.log 或 crash-reports。"

    text_parts = []
    for path in unique[:4]:
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as file_handle:
                text_parts.append(file_handle.read()[-120000:])
        except OSError:
            pass
    text = "\n".join(text_parts)
    lowered = text.lower()
    findings = []

    patterns = [
        ("Java 版本不兼容", ["unsupported class file major version", "has been compiled by a more recent version", "java.lang.unsupportedclassversionerror"]),
        ("Mod 缺少依赖", ["mod requires", "requires version", "missing dependencies", "modresolutionexception"]),
        ("Mod 冲突或加载失败", ["failed to load mod", "mod loading has failed", "exception loading mod", "mixin apply failed"]),
        ("显卡驱动/OpenGL 问题", ["opengl", "glfw error", "pixel format not accelerated", "lwjgl"]),
        ("内存不足", ["outofmemoryerror", "java heap space", "unable to allocate"]),
        ("认证或网络问题", ["authentication", "invalid session", "connection timed out", "unknownhostexception"]),
        ("资源包或配置损坏", ["malformed", "jsonparseexception", "invalid byte", "could not parse"]),
    ]
    for label, needles in patterns:
        if any(needle in lowered for needle in needles):
            findings.append(label)

    exception_lines = []
    for line in text.splitlines():
        if any(token in line for token in ("Exception", "Error", "Caused by:", "Failed to")):
            cleaned = line.strip()
            if cleaned and cleaned not in exception_lines:
                exception_lines.append(cleaned)
        if len(exception_lines) >= 8:
            break

    result = [f"分析文件：{len(unique)} 个"]
    if findings:
        result.append("可能原因：" + "；".join(findings))
    else:
        result.append("未匹配到常见崩溃类型，请查看下方关键行。")
    if exception_lines:
        result.append("关键行：")
        result.extend(exception_lines[:8])
    result.append(f"最新文件：{unique[0]}")
    return "\n".join(result)
