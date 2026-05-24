import json
import os
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
    return f"{alias} [{version_id}]" if alias else version_id


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
    extra_jvm_args = shlex.split(raw_jvm_args, posix=False) if raw_jvm_args else []
    return {
        "runtime_directory": runtime_directory,
        "extra_jvm_args": extra_jvm_args,
    }


def resolve_base_minecraft_version(game_dir, version_id):
    version_json = get_version_json(game_dir, version_id)
    if version_json and version_json.get("inheritsFrom"):
        return version_json.get("inheritsFrom")
    return version_id


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
    if category == "原版":
        return version_type == "原版"
    if category == "仅 OptiFine":
        return version_type == "OptiFine"
    if category == "可安装 Mod":
        return version_type in {"Fabric", "Forge", "NeoForge", "OptiFine"}
    return True


def version_type_label(game_dir, version_id):
    return detect_version_type(game_dir, version_id)
