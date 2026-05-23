# java_utils.py
import os
import re
import subprocess
from platform import system


def _push_unique(seen, paths, path):
    if not path:
        return
    normalized = os.path.abspath(path)
    if normalized in seen:
        return
    seen.add(normalized)
    paths.append(normalized)


def _looks_like_java_binary(path):
    if not path:
        return False

    normalized = os.path.abspath(path).replace("/", "\\").lower()
    filename = os.path.basename(normalized)
    if filename not in ("java", "java.exe"):
        return False

    allowed_tokens = (
        "\\java\\",
        "\\jdk",
        "\\jre",
        "\\runtime\\",
        "\\jvm",
        "\\hotspot",
        "\\adoptium",
        "\\temurin",
        "\\zulu",
        "\\corretto",
        "\\microsoft",
        "\\bellsoft",
        "\\dragonwell",
        "\\amazon",
    )
    return any(token in normalized for token in allowed_tokens)


def _resolve_java_path(path):
    if os.path.isfile(path):
        return path if _looks_like_java_binary(path) else None

    if os.path.isdir(path):
        candidates = (
            os.path.join(path, "bin", "java.exe"),
            os.path.join(path, "bin", "java"),
            os.path.join(path, "jre", "bin", "java.exe"),
            os.path.join(path, "jre", "bin", "java"),
            os.path.join(path, "Contents", "Home", "bin", "java"),
        )
        for candidate in candidates:
            if os.path.isfile(candidate) and _looks_like_java_binary(candidate):
                return candidate
    return None


def _scan_runtime_tree(root, max_depth=6):
    if not root or not os.path.isdir(root):
        return []

    results = []
    stack = [(root, 0)]
    seen_dirs = set()

    while stack:
        current, depth = stack.pop()
        real_current = os.path.realpath(current)
        if real_current in seen_dirs or depth > max_depth:
            continue
        seen_dirs.add(real_current)

        for candidate in (
            os.path.join(current, "java.exe"),
            os.path.join(current, "java"),
            os.path.join(current, "bin", "java.exe"),
            os.path.join(current, "bin", "java"),
            os.path.join(current, "jre", "bin", "java.exe"),
            os.path.join(current, "jre", "bin", "java"),
            os.path.join(current, "Contents", "Home", "bin", "java"),
        ):
            if os.path.isfile(candidate) and _looks_like_java_binary(candidate):
                results.append(candidate)

        try:
            entries = os.listdir(current)
        except (PermissionError, FileNotFoundError, NotADirectoryError):
            continue

        for entry in entries:
            child = os.path.join(current, entry)
            if os.path.isdir(child):
                stack.append((child, depth + 1))

    return results


def _iter_search_roots():
    roots = []

    for raw_path in os.environ.get("PATH", "").split(os.pathsep):
        cleaned = raw_path.strip().strip('"')
        if cleaned:
            roots.append(cleaned)

    java_home = os.environ.get("JAVA_HOME")
    if java_home:
        roots.extend([
            java_home,
            os.path.join(java_home, "bin", "java"),
        ])

    appdata = os.environ.get("APPDATA")
    if appdata:
        roots.extend([
            os.path.join(appdata, ".minecraft"),
            os.path.join(appdata, ".minecraft", "runtime"),
        ])

    if "windows" in system().lower():
        roots.extend([
            r"C:\Program Files\Java",
            r"C:\Program Files\Eclipse Adoptium",
            r"C:\Program Files\Microsoft",
            r"C:\Program Files\Zulu",
            r"C:\Program Files\BellSoft",
            r"C:\Program Files\Amazon Corretto",
            r"C:\Program Files (x86)\Java",
            r"C:\Program Files (x86)\Eclipse Adoptium",
        ])
    else:
        roots.extend([
            "/usr/bin/java",
            "/usr/local/bin/java",
            "/usr/java",
            "/usr/lib/jvm",
            "/usr/lib64/jvm",
            "/opt/jdk",
            "/opt/jdks",
            "/Library/Java/JavaVirtualMachines",
        ])

    return roots


def find_java_paths():
    """返回去重后的 Java 可执行文件路径列表。"""
    paths = []
    seen = set()

    for root in _iter_search_roots():
        if root.endswith(os.path.join(".minecraft", "runtime")):
            for candidate in _scan_runtime_tree(root, max_depth=6):
                _push_unique(seen, paths, candidate)
            continue

        resolved = _resolve_java_path(root)
        if resolved:
            _push_unique(seen, paths, resolved)
            continue

        if os.path.isdir(root):
            for entry in os.listdir(root):
                entry_path = os.path.join(root, entry)
                for candidate in (
                    _resolve_java_path(entry_path),
                    os.path.join(entry_path, "bin", "java.exe"),
                    os.path.join(entry_path, "bin", "java"),
                    os.path.join(entry_path, "jre", "bin", "java.exe"),
                    os.path.join(entry_path, "jre", "bin", "java"),
                    os.path.join(entry_path, "Contents", "Home", "bin", "java"),
                ):
                    if candidate and os.path.isfile(candidate) and _looks_like_java_binary(candidate):
                        _push_unique(seen, paths, candidate)

    return paths


def _extract_java_version_text(output):
    if not output:
        return None

    lowered = output.lower()
    if "unknown option" in lowered or "unrecognized option" in lowered or "未知的选项" in output:
        return None

    for line in output.splitlines():
        line = line.strip()
        if not line:
            continue
        if re.search(r'version\s+"?\d', line, re.I):
            return output

    return None


def _read_release_version(java_path):
    parent = os.path.dirname(java_path)
    candidates = [
        os.path.join(parent, "..", "release"),
        os.path.join(parent, "..", "..", "release"),
        os.path.join(os.path.dirname(parent), "release"),
        os.path.join(os.path.dirname(os.path.dirname(parent)), "release"),
    ]
    for candidate in candidates:
        candidate = os.path.abspath(candidate)
        if not os.path.isfile(candidate):
            continue
        try:
            with open(candidate, "r", encoding="utf-8", errors="ignore") as file_handle:
                content = file_handle.read()
        except OSError:
            continue

        match = re.search(r'JAVA_VERSION="([^"]+)"', content)
        if match:
            return match.group(1)
    return None


def get_java_version(java_path):
    """获取指定 Java 可执行文件的版本文本。"""
    if not _looks_like_java_binary(java_path):
        return None

    release_version = _read_release_version(java_path)
    if release_version:
        return f'java version "{release_version}"'

    try:
        result = subprocess.run([java_path, "-version"], capture_output=True, text=True, timeout=10)
        output = (result.stderr or result.stdout or "").strip()
        return _extract_java_version_text(output)
    except (FileNotFoundError, subprocess.SubprocessError, OSError):
        return None


def get_java_major_version(java_path):
    """解析 Java 主版本号，例如 8、17、21。"""
    release_version = _read_release_version(java_path)
    if release_version:
        match = re.search(r"^(\d+)(?:\.(\d+))?", release_version)
        if match:
            major = int(match.group(1))
            minor = match.group(2)
            if major == 1 and minor:
                return int(minor)
            return major

    version_text = get_java_version(java_path)
    if not version_text:
        return None

    first_line = version_text.splitlines()[0]
    match = re.search(r'"(\d+)(?:\.(\d+))?', first_line)
    if not match:
        return None

    major = int(match.group(1))
    minor = match.group(2)
    if major == 1 and minor:
        return int(minor)
    return major
