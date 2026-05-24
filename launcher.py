import os
import subprocess
import json
import configparser
import re
import platform
import shlex

config = configparser.ConfigParser()
config.read("launcher_config.ini")


def _hidden_subprocess_kwargs():
    if os.name != "nt":
        return {}

    startupinfo = subprocess.STARTUPINFO()
    startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    return {
        "startupinfo": startupinfo,
        "creationflags": creationflags,
    }


def _current_os_name():
    if os.name == "nt":
        return "windows"
    if sys_platform := platform.system().lower():
        if "darwin" in sys_platform or "mac" in sys_platform:
            return "osx"
        if "linux" in sys_platform:
            return "linux"
    return "unknown"


def _current_os_arch():
    machine = platform.machine().lower()
    if machine in {"amd64", "x86_64"}:
        return "x86_64"
    if machine in {"x86", "i386", "i686"}:
        return "x86"
    if "arm" in machine:
        return "arm64" if "64" in machine else "arm"
    return machine or "unknown"


def _current_os_version():
    return platform.version() or platform.release() or ""


def _rule_allows(rule, features=None):
    os_rule = rule.get("os", {})
    if os_rule:
        name = os_rule.get("name")
        if name and name != _current_os_name():
            return False
        arch = os_rule.get("arch")
        if arch and arch != _current_os_arch():
            return False
        version_pattern = os_rule.get("version")
        if version_pattern and not re.search(version_pattern, _current_os_version()):
            return False

    required_features = rule.get("features", {})
    if required_features:
        feature_state = features or {}
        for key, expected in required_features.items():
            if bool(feature_state.get(key, False)) != bool(expected):
                return False
    return rule.get("action", "allow") != "disallow"


def _argument_list(items, context=None, features=None):
    result = []
    for item in items or []:
        if isinstance(item, str):
            result.append(_substitute(item, context or {}))
            continue
        if isinstance(item, dict):
            rules = item.get("rules")
            if rules and not any(_rule_allows(rule, features=features) for rule in rules):
                continue
            value = item.get("value")
            if isinstance(value, list):
                result.extend(_substitute(entry, context or {}) if isinstance(entry, str) else entry for entry in value)
            elif isinstance(value, str):
                result.append(_substitute(value, context or {}))
    return result


def _substitute(value, context):
    def replace(match):
        return str(context.get(match.group(1), match.group(0)))

    return re.sub(r"\$\{([^}]+)\}", replace, value)


def _merge_version_chain(chain):
    resolved = {}
    libraries = {}
    for version_json in reversed(chain):
        for library in version_json.get("libraries", []):
            key = library.get("name") or library.get("downloads", {}).get("artifact", {}).get("path")
            if key:
                libraries[key] = library

        for key, value in version_json.items():
            if key == "libraries":
                continue
            if key == "arguments" and isinstance(value, dict):
                merged_arguments = resolved.setdefault("arguments", {"jvm": [], "game": []})
                merged_arguments["jvm"].extend(value.get("jvm", []))
                merged_arguments["game"].extend(value.get("game", []))
                continue
            resolved[key] = value

    resolved["libraries"] = list(libraries.values())
    return resolved


def _maven_library_path(name):
    parts = name.split(":")
    if len(parts) != 3:
        return None
    group, artifact, version = parts
    return os.path.join(*group.split("."), artifact, version, f"{artifact}-{version}.jar")


def _library_path(game_directory, library):
    artifact = library.get("downloads", {}).get("artifact", {})
    path = artifact.get("path")
    if path:
        return os.path.join(game_directory, "libraries", path)

    name = library.get("name")
    if not name:
        return None

    maven_path = _maven_library_path(name)
    if not maven_path:
        return None
    return os.path.join(game_directory, "libraries", maven_path)


def _library_allowed(library, features=None):
    rules = library.get("rules")
    if not rules:
        return True
    return any(_rule_allows(rule, features=features) for rule in rules)


def launch_minecraft(java_path, version_id, game_directory=".minecraft", minecraft_access_token=None, username=None, uuid=None, runtime_directory=None, extra_jvm_args=None):
    """启动 Minecraft。"""
    version_json = get_version_json(game_directory, version_id)
    if not version_json:
        return False

    chain = get_version_inheritance_chain(game_directory, version_id)
    version_json = _merge_version_chain(chain)

    version_jars = []
    for item in chain:
        item_id = item.get("id")
        if not item_id:
            continue
        jar_path = os.path.join(game_directory, "versions", item_id, f"{item_id}.jar")
        if os.path.exists(jar_path) and jar_path not in version_jars:
            version_jars.append(jar_path)

    if not version_jars:
        return False

    natives_version_id = version_json["id"]
    for item in chain:
        if item.get("downloads", {}).get("client") or item.get("assetIndex"):
            natives_version_id = item.get("id", natives_version_id)
            break
    natives_dir = os.path.join(game_directory, 'versions', natives_version_id, f"{natives_version_id}-natives")

    default_jvm_arguments = [
        "-Xmx2G",
        "-XX:+UnlockExperimentalVMOptions",
        "-XX:+UseG1GC",
        "-XX:G1NewSizePercent=20",
        "-XX:G1ReservePercent=20",
        "-XX:MaxGCPauseMillis=50",
        "-XX:G1HeapRegionSize=32M",
        f"-Djava.library.path={natives_dir}",
    ]

    assets_dir = os.path.join(game_directory, "assets")
    runtime_directory = runtime_directory or game_directory
    os.makedirs(runtime_directory, exist_ok=True)

    features = {
        "is_demo_user": False,
        "has_custom_resolution": False,
    }

    classpath = []
    for library in version_json["libraries"]:
        if not _library_allowed(library, features=features):
            continue
        path = _library_path(game_directory, library)
        if path and os.path.exists(path):
            classpath.append(path)
    classpath.extend(version_jars)
    classpath_string = os.pathsep.join(classpath)

    context = {
        "auth_player_name": username if username else config.get("USER", "username", fallback="Player"),
        "version_name": version_json["id"],
        "game_directory": runtime_directory,
        "assets_root": assets_dir,
        "assets_index_name": version_json.get("assetIndex", {}).get("id", ""),
        "auth_uuid": uuid if uuid else config.get("USER", "uuid", fallback="00000000-0000-0000-0000-000000000000"),
        "auth_access_token": minecraft_access_token if minecraft_access_token else config.get("USER", "accessToken", fallback="0"),
        "user_type": "msa" if minecraft_access_token else "mojang",
        "version_type": version_json.get("type", "release"),
        "launcher_name": "McGo",
        "launcher_version": "1.0",
        "natives_directory": natives_dir,
        "library_directory": os.path.join(game_directory, "libraries"),
        "classpath_separator": os.pathsep,
        "classpath": classpath_string,
        "user_properties": "{}",
        "resolution_width": "854",
        "resolution_height": "480",
    }

    modern_arguments = version_json.get("arguments", {})
    if modern_arguments.get("game"):
        game_arguments = _argument_list(modern_arguments.get("game", []), context=context, features=features)
    else:
        legacy_arguments = version_json.get("minecraftArguments", "")
        if legacy_arguments:
            game_arguments = [
                _substitute(value, context)
                for value in shlex.split(legacy_arguments, posix=False)
            ]
        else:
            game_arguments = [
                "--username", context["auth_player_name"],
                "--version", context["version_name"],
                "--gameDir", context["game_directory"],
                "--assetsDir", context["assets_root"],
                "--assetIndex", context["assets_index_name"],
                "--uuid", context["auth_uuid"],
                "--accessToken", context["auth_access_token"],
                "--userType", context["user_type"],
                "--versionType", context["version_type"],
            ]

    resolved_modern_jvm = _argument_list(modern_arguments.get("jvm", []), context=context, features=features)
    if resolved_modern_jvm:
        jvm_arguments = resolved_modern_jvm
        if "-Djava.library.path=" not in " ".join(resolved_modern_jvm):
            jvm_arguments = [*jvm_arguments, f"-Djava.library.path={natives_dir}"]
    else:
        jvm_arguments = default_jvm_arguments

    command = [
        java_path,
        *(extra_jvm_args or []),
        *jvm_arguments,
    ]
    if "-cp" not in jvm_arguments and "--class-path" not in jvm_arguments:
        command.extend(["-cp", classpath_string])
    command.extend([version_json["mainClass"], *game_arguments])

    subprocess.Popen(command, **_hidden_subprocess_kwargs())
    return True


def get_local_versions(game_directory=".minecraft"):
    """获取本地已存在的 Minecraft 版本列表。"""
    versions = []
    versions_dir = os.path.join(game_directory, "versions")
    if os.path.exists(versions_dir):
        for version_dir in os.listdir(versions_dir):
            if os.path.isdir(os.path.join(versions_dir, version_dir)):
                versions.append(version_dir)
    return versions


def get_version_json(game_directory, version_id):
    version_json_path = os.path.join(
        game_directory, "versions", version_id, f"{version_id}.json"
    )
    if not os.path.exists(version_json_path):
        return None

    with open(version_json_path, "r", encoding="utf-8") as file_handle:
        return json.load(file_handle)


def get_version_inheritance_chain(game_directory, version_id):
    chain = []
    current_id = version_id
    seen = set()

    while current_id and current_id not in seen:
        seen.add(current_id)
        version_json = get_version_json(game_directory, current_id)
        if not version_json:
            break
        chain.append(version_json)
        current_id = version_json.get("inheritsFrom")

    return chain


def infer_required_java_version(game_directory, version_id):
    normalized = version_id.lower()
    match = re.match(r"(\d+)\.(\d+)", normalized)

    baseline = 17
    if match:
        major = int(match.group(1))
        minor = int(match.group(2))
        if major > 1 or minor >= 21:
            baseline = 21
        elif minor >= 16:
            baseline = 17
        else:
            baseline = 8

    chain = get_version_inheritance_chain(game_directory, version_id)
    highest_declared = None
    for version_json in chain:
        java_version = version_json.get("javaVersion", {}).get("majorVersion")
        if java_version:
            declared = int(java_version)
            highest_declared = declared if highest_declared is None else max(highest_declared, declared)

    if highest_declared is None:
        return baseline
    return max(baseline, highest_declared)
