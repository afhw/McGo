import json
import os
import re
import shlex

from launcher import find_version_json_path, get_local_versions, get_version_json


VERSION_SETTINGS_FILE = "version_settings.json"


def load_version_settings(path=VERSION_SETTINGS_FILE):
    if os.path.exists(path):
        with open(path, "r", encoding="utf-8") as file_handle:
            data = json.load(file_handle)
        if isinstance(data, dict):
            data.setdefault("_meta", {})
            return data
    return {"_meta": {"last_launched_version": ""}}


def save_version_settings(settings, path=VERSION_SETTINGS_FILE):
    with open(path, "w", encoding="utf-8") as file_handle:
        json.dump(settings, file_handle, ensure_ascii=False, indent=2)


def version_settings_entry(settings, version_id):
    if not version_id:
        return {}
    return settings.setdefault(version_id, {})


def version_display_name(settings, version_id):
    entry = version_settings_entry(settings, version_id)
    alias = (entry.get("alias") or "").strip()
    icon = (entry.get("icon") or "").strip()
    prefix = ""
    if entry.get("favorite"):
        prefix += "* "
    if icon and icon != "自动":
        prefix += f"{icon} "
    display = f"{alias} [{version_id}]" if alias else version_id
    return f"{prefix}{display}"


def isolated_runtime_directory(game_dir, version_id):
    return os.path.join(game_dir, "versions", version_id)


def runtime_directory_for_version(game_dir, settings, version_id, global_isolation=False):
    entry = version_settings_entry(settings, version_id)
    custom_runtime = (entry.get("runtime_directory") or "").strip()
    if custom_runtime:
        return os.path.abspath(custom_runtime)
    if global_isolation or entry.get("use_isolated_directory", False):
        return isolated_runtime_directory(game_dir, version_id)
    return game_dir


def mods_directory_for_version(game_dir, settings, version_id, global_isolation=False):
    entry = version_settings_entry(settings, version_id)
    custom_mods = (entry.get("mods_directory") or "").strip()
    if custom_mods:
        return os.path.abspath(custom_mods)
    return os.path.join(
        runtime_directory_for_version(game_dir, settings, version_id, global_isolation=global_isolation),
        "mods",
    )


def launch_options_for_version(game_dir, settings, version_id, global_isolation=False):
    entry = version_settings_entry(settings, version_id)
    runtime_directory = runtime_directory_for_version(
        game_dir,
        settings,
        version_id,
        global_isolation=global_isolation,
    )
    raw_jvm_args = (entry.get("jvm_args") or "").strip()
    raw_game_args = (entry.get("game_args") or "").strip()
    extra_jvm_args = shlex.split(raw_jvm_args, posix=False) if raw_jvm_args else []
    extra_game_args = shlex.split(raw_game_args, posix=False) if raw_game_args else []
    return {
        "runtime_directory": runtime_directory,
        "extra_jvm_args": extra_jvm_args,
        "extra_game_args": extra_game_args,
        "min_memory_mb": _positive_int(entry.get("min_memory_mb"), 0),
        "max_memory_mb": _positive_int(entry.get("max_memory_mb"), 0),
        "window_width": _positive_int(entry.get("window_width"), 0),
        "window_height": _positive_int(entry.get("window_height"), 0),
        "pre_launch_command": (entry.get("pre_launch_command") or "").strip(),
        "gc_strategy": (entry.get("gc_strategy") or "G1GC").strip() or "G1GC",
    }


def _positive_int(value, fallback=0):
    try:
        parsed = int(str(value).strip())
    except (TypeError, ValueError):
        return fallback
    return parsed if parsed > 0 else fallback


def resolve_base_minecraft_version(game_dir, version_id):
    version_json = get_version_json(game_dir, version_id)
    if version_json:
        inherited = version_json.get("inheritsFrom")
        if _looks_like_minecraft_version(inherited):
            return inherited

        for key in ("minecraftVersion", "mcVersion", "releaseTarget", "clientVersion"):
            value = str(version_json.get(key, "")).strip()
            if _looks_like_minecraft_version(value):
                return value

        asset_id = str(version_json.get("assetIndex", {}).get("id", "")).strip()
        if _looks_like_minecraft_version(asset_id):
            return asset_id

        argument_version = _extract_version_from_arguments(version_json.get("arguments", {}))
        if argument_version:
            return argument_version

        logging_file = version_json.get("logging", {}).get("client", {}).get("file", {})
        logging_text = " ".join(
            str(logging_file.get(key, ""))
            for key in ("id", "url")
            if logging_file.get(key)
        )
        detected = _extract_minecraft_version_from_text(logging_text, strict=True)
        if detected:
            return detected

        text_parts = [
            version_json.get("id", ""),
            version_json.get("jar", ""),
            version_json.get("mainClass", ""),
        ]
        for library in version_json.get("libraries", []):
            if isinstance(library, dict):
                text_parts.append(library.get("name", ""))
                text_parts.append(library.get("downloads", {}).get("artifact", {}).get("path", ""))
        detected = _extract_minecraft_version_from_text(" ".join(str(part) for part in text_parts if part), strict=True)
        if detected:
            return detected

    detected = _extract_minecraft_version_from_text(version_id, strict=True)
    if detected:
        return detected
    return version_id


def _extract_version_from_arguments(arguments):
    values = []
    for item in arguments.get("game", []) if isinstance(arguments, dict) else []:
        if isinstance(item, str):
            values.append(item)
        elif isinstance(item, dict):
            value = item.get("value")
            if isinstance(value, list):
                values.extend(str(entry) for entry in value)
            elif isinstance(value, str):
                values.append(value)

    for flag in ("--fml.mcVersion", "--minecraftVersion", "--mcVersion"):
        if flag in values:
            index = values.index(flag)
            if index + 1 < len(values) and _looks_like_minecraft_version(values[index + 1]):
                return values[index + 1]
    return ""


def _looks_like_minecraft_version(value):
    return bool(re.fullmatch(r"(?:1|2|3|4|20|21|22|23|24|25|26|27|28|29|30)(?:\.\d{1,2}){1,2}(?:[-_](?:pre|rc)\d+)?|(?:\d{2}w\d{2}[a-z])", str(value or "")))


def _extract_minecraft_version_from_text(text, strict=False):
    text = str(text or "")
    patterns = [
        r"fabric-loader-[^/\s:]+-((?:1|2|3|4|20|21|22|23|24|25|26|27|28|29|30)(?:\.\d{1,2}){1,2}(?:[-_](?:pre|rc)\d+)?)",
        r"quilt-loader-[^/\s:]+-((?:1|2|3|4|20|21|22|23|24|25|26|27|28|29|30)(?:\.\d{1,2}){1,2}(?:[-_](?:pre|rc)\d+)?)",
        r"forge-((?:1|2|3|4|20|21|22|23|24|25|26|27|28|29|30)(?:\.\d{1,2}){1,2}(?:[-_](?:pre|rc)\d+)?)-[0-9][^/\s:]*",
        r"net\.minecraftforge:forge:((?:1|2|3|4|20|21|22|23|24|25|26|27|28|29|30)(?:\.\d{1,2}){1,2}(?:[-_](?:pre|rc)\d+)?)-[0-9][^/\s:]*",
        r"neoforge-((?:1|2|3|4|20|21|22|23|24|25|26|27|28|29|30)(?:\.\d{1,2}){1,2}(?:[-_](?:pre|rc)\d+)?)-[0-9][^/\s:]*",
        r"net\.neoforged:forge:((?:1|2|3|4|20|21|22|23|24|25|26|27|28|29|30)(?:\.\d{1,2}){1,2}(?:[-_](?:pre|rc)\d+)?)-[0-9][^/\s:]*",
        r"client-((?:1|2|3|4|20|21|22|23|24|25|26|27|28|29|30)(?:\.\d{1,2}){1,2}(?:[-_](?:pre|rc)\d+)?)\.xml",
        r"server-((?:1|2|3|4|20|21|22|23|24|25|26|27|28|29|30)(?:\.\d{1,2}){1,2}(?:[-_](?:pre|rc)\d+)?)\.xml",
    ]
    for pattern in patterns:
        match = re.search(pattern, text, flags=re.IGNORECASE)
        if match and _looks_like_minecraft_version(match.group(1)):
            return match.group(1)
    if not strict:
        candidates = re.findall(r"(?<![\d.])(?:1|2|3|4|20|21|22|23|24|25|26|27|28|29|30)(?:\.\d{1,2}){1,2}(?:[-_](?:pre|rc)\d+)?(?![\d.])", text, flags=re.IGNORECASE)
        for candidate in sorted(candidates, key=lambda item: (len(item), item), reverse=True):
            if _looks_like_minecraft_version(candidate):
                return candidate
    snapshot = re.search(r"(?<![0-9a-z])(\d{2}w\d{2}[a-z])(?![0-9a-z])", text, flags=re.IGNORECASE)
    return snapshot.group(1) if snapshot else ""


def find_matching_fabric_versions(game_dir, base_minecraft_version):
    fabric_versions = []
    for version_id in get_local_versions(game_dir):
        if not version_id.startswith("fabric-loader-"):
            continue
        chain = get_version_json(game_dir, version_id)
        inherited = chain.get("inheritsFrom") if chain else None
        if inherited == base_minecraft_version or version_id.endswith(f"-{base_minecraft_version}"):
            fabric_versions.append(version_id)
    return fabric_versions


def detect_version_type(game_dir, version_id):
    version_json = get_version_json(game_dir, version_id) or {}
    version_dir = os.path.join(game_dir, "versions", version_id)

    text_parts = [
        version_id,
        version_json.get("id", ""),
        version_json.get("mainClass", ""),
        version_json.get("inheritsFrom", ""),
        version_json.get("jar", ""),
        version_json.get("type", ""),
    ]

    arguments = version_json.get("arguments", {})
    for key in ("game", "jvm"):
        values = arguments.get(key, []) if isinstance(arguments, dict) else []
        for item in values:
            if isinstance(item, str):
                text_parts.append(item)
            elif isinstance(item, dict):
                value = item.get("value")
                if isinstance(value, list):
                    text_parts.extend(str(entry) for entry in value)
                elif isinstance(value, str):
                    text_parts.append(value)

    libraries = version_json.get("libraries", [])
    for library in libraries:
        if isinstance(library, dict):
            text_parts.append(library.get("name", ""))
            artifact_path = library.get("downloads", {}).get("artifact", {}).get("path", "")
            text_parts.append(artifact_path)

    manifest_path = find_version_json_path(game_dir, version_id)
    if manifest_path:
        text_parts.append(os.path.basename(manifest_path))

    if os.path.isdir(os.path.join(version_dir, "mods")):
        try:
            mod_names = os.listdir(os.path.join(version_dir, "mods"))
            text_parts.extend(mod_names[:50])
        except OSError:
            pass

    lowered = " ".join(str(part).lower() for part in text_parts if part)
    if "fabric-loader-" in lowered or " net.fabricmc:" in lowered or "fabric-loader" in lowered:
        return "Fabric"
    if "neoforge" in lowered or " net.neoforged:" in lowered or "--launchtarget neoforgeclient" in lowered:
        return "NeoForge"
    if "net.minecraftforge" in lowered or "forgeclient" in lowered or "forge-" in lowered:
        return "Forge"
    if "optifine" in lowered:
        return "OptiFine"
    return "原版"


def version_matches_category(game_dir, version_id, category):
    version_type = detect_version_type(game_dir, version_id)
    if category == "全部版本":
        return True
    if category == "收藏":
        return True
    if category == "隐藏":
        return True
    if category == "原版":
        return version_type == "原版"
    if category == "仅 OptiFine":
        return version_type == "OptiFine"
    if category == "可安装 Mod":
        return version_type in {"Fabric", "Forge", "NeoForge", "OptiFine"}
    return True


def version_type_label(game_dir, version_id):
    return detect_version_type(game_dir, version_id)
