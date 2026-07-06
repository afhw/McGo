import hashlib
import base64
import contextlib
import os
import re
import subprocess
import sys
import tempfile
import uuid
from urllib.parse import urljoin, urlparse

import http_client


UPDATE_BUFFER_SIZE = 1024 * 1024
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
SAFE_FILENAME_RE = re.compile(r"[^A-Za-z0-9._-]+")
SEMVER_RE = re.compile(
    r"^v?(?P<core>\d+(?:\.\d+)*)(?:-(?P<pre>[0-9A-Za-z.-]+))?(?:\+.*)?$"
)


def version_key(version):
    parts = re.split(r"[.\-_]", str(version or "").strip().split("+", 1)[0])
    key = []
    for part in parts:
        if part.isdigit():
            key.append((1, int(part)))
        elif part:
            key.append((0, part.lower()))
    return key or [(0, "")]


def _semver_parts(version):
    match = SEMVER_RE.match(str(version or "").strip())
    if not match:
        return None
    core = tuple(int(part) for part in match.group("core").split("."))
    prerelease = match.group("pre")
    if prerelease is None:
        return core, None
    return core, prerelease.split(".")


def _compare_prerelease(left, right):
    if left is None and right is None:
        return 0
    if left is None:
        return 1
    if right is None:
        return -1
    for left_part, right_part in zip(left, right):
        left_numeric = left_part.isdigit()
        right_numeric = right_part.isdigit()
        if left_numeric and right_numeric:
            left_value = int(left_part)
            right_value = int(right_part)
        else:
            if left_numeric != right_numeric:
                return -1 if left_numeric else 1
            left_value = left_part.lower()
            right_value = right_part.lower()
        if left_value != right_value:
            return 1 if left_value > right_value else -1
    if len(left) == len(right):
        return 0
    return 1 if len(left) > len(right) else -1


def _compare_semver(left, right):
    left_core, left_pre = left
    right_core, right_pre = right
    width = max(len(left_core), len(right_core))
    left_core = left_core + (0,) * (width - len(left_core))
    right_core = right_core + (0,) * (width - len(right_core))
    if left_core != right_core:
        return 1 if left_core > right_core else -1
    return _compare_prerelease(left_pre, right_pre)


def is_newer_version(remote_version, current_version):
    remote_semver = _semver_parts(remote_version)
    current_semver = _semver_parts(current_version)
    if remote_semver and current_semver:
        return _compare_semver(remote_semver, current_semver) > 0
    return version_key(remote_version) > version_key(current_version)


def sanitize_filename_component(value, fallback="download"):
    value = SAFE_FILENAME_RE.sub("-", str(value or "").strip())
    value = value.strip(".-_ ")
    return value or fallback


def _manifest_size(value):
    if value in (None, ""):
        return 0
    try:
        size = int(value)
    except (TypeError, ValueError) as exc:
        raise RuntimeError("更新清单 size 格式无效。") from exc
    if size < 0:
        raise RuntimeError("更新清单 size 格式无效。")
    return size


def _validate_download_url(download_url):
    parsed = urlparse(download_url)
    if parsed.scheme not in {"http", "https"}:
        raise RuntimeError("更新清单 url 必须是 HTTP 或 HTTPS 地址。")
    if not parsed.netloc:
        raise RuntimeError("更新清单 url 缺少主机名。")
    return download_url


def _manifest_sha256(value):
    digest = str(value or "").strip().lower()
    if not digest:
        raise RuntimeError("更新清单缺少 sha256。")
    if not SHA256_RE.match(digest):
        raise RuntimeError("更新清单 sha256 格式无效。")
    return digest


def fetch_update_manifest(manifest_url):
    response = http_client.get(manifest_url, timeout=15, retries=1)
    http_client.raise_for_status(response, "检查更新")
    try:
        data = response.json()
    except ValueError as exc:
        raise RuntimeError("更新清单不是有效 JSON。") from exc
    if not isinstance(data, dict):
        raise RuntimeError("更新清单格式无效。")
    version = str(data.get("version") or "").strip()
    download_url = str(data.get("url") or data.get("download_url") or data.get("downloadUrl") or "").strip()
    if not version:
        raise RuntimeError("更新清单缺少 version。")
    if not download_url:
        raise RuntimeError("更新清单缺少 url。")
    data["version"] = version
    data["url"] = _validate_download_url(urljoin(manifest_url, download_url))
    data["sha256"] = _manifest_sha256(data.get("sha256"))
    data["size"] = _manifest_size(data.get("size", 0))
    data["notes"] = str(data.get("notes") or data.get("changelog") or "").strip()
    return data


def check_for_update(manifest_url, current_version):
    manifest = fetch_update_manifest(manifest_url)
    manifest["current_version"] = current_version
    current_sha256 = current_build_sha256()
    remote_sha256 = manifest.get("sha256", "")
    same_version_build_changed = (
        str(manifest["version"]) == str(current_version)
        and bool(current_sha256)
        and bool(remote_sha256)
        and current_sha256.lower() != remote_sha256.lower()
    )
    manifest["current_sha256"] = current_sha256
    manifest["build_changed"] = same_version_build_changed
    manifest["update_available"] = is_newer_version(manifest["version"], current_version) or same_version_build_changed
    return manifest


def file_sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(UPDATE_BUFFER_SIZE), b""):
            digest.update(chunk)
    return digest.hexdigest()


def looks_like_windows_executable(path):
    try:
        with open(path, "rb") as handle:
            return handle.read(2) == b"MZ"
    except OSError:
        return False


def current_build_sha256(executable_path=None):
    executable_path = executable_path or current_executable_path()
    if not can_replace_current_executable(executable_path):
        return ""
    try:
        digest = file_sha256(executable_path).lower()
    except OSError:
        return ""
    return digest if SHA256_RE.match(digest) else ""


def download_update(manifest, target_dir=None, progress_callback=None, status_callback=None):
    target_dir = target_dir or tempfile.gettempdir()
    os.makedirs(target_dir, exist_ok=True)
    download_url = _validate_download_url(str(manifest.get("url", "")).strip())
    expected_sha256 = _manifest_sha256(manifest.get("sha256"))
    filename = os.path.basename(urlparse(download_url).path) or "McGo-update.exe"
    filename = sanitize_filename_component(filename, "McGo-update.exe")
    if not filename.lower().endswith(".exe"):
        filename += ".exe"
    version_suffix = sanitize_filename_component(manifest.get("version"), "latest")
    stem, ext = os.path.splitext(filename)
    target_path = os.path.join(target_dir, f"{stem}-{version_suffix}{ext or '.exe'}")
    temp_path = target_path + ".download"
    if status_callback:
        status_callback(f"正在下载 McGo {manifest.get('version', '')}...")
    if progress_callback:
        progress_callback(0)
    try:
        response = http_client.get(download_url, timeout=30, retries=1, stream=True)
        http_client.raise_for_status(response, "下载更新")
        total = int(response.headers.get("Content-Length", 0) or 0)
        expected_size = _manifest_size(manifest.get("size", 0))
        if expected_size and total and total != expected_size:
            raise RuntimeError("更新文件大小与清单不一致。")
        downloaded = 0
        with open(temp_path, "wb") as handle:
            for chunk in response.iter_content(chunk_size=UPDATE_BUFFER_SIZE):
                if not chunk:
                    continue
                handle.write(chunk)
                downloaded += len(chunk)
                if progress_callback and total:
                    progress_callback(min(100, int(downloaded / total * 100)))
        if downloaded <= 0:
            raise RuntimeError("更新文件为空。")
        if total and downloaded != total:
            raise RuntimeError("更新文件下载不完整。")
        if expected_size and downloaded != expected_size:
            raise RuntimeError("更新文件大小与清单不一致。")
        if not looks_like_windows_executable(temp_path):
            raise RuntimeError("更新文件不是有效的 Windows 可执行文件。")
        actual_sha256 = file_sha256(temp_path)
        if actual_sha256.lower() != expected_sha256:
            raise RuntimeError("更新文件 SHA256 校验失败。")
        os.replace(temp_path, target_path)
    except Exception:
        with contextlib.suppress(OSError):
            os.remove(temp_path)
        raise
    if progress_callback:
        progress_callback(100)
    payload = dict(manifest)
    payload["path"] = target_path
    payload["sha256"] = actual_sha256
    payload["bytes"] = downloaded
    return payload


def current_executable_path():
    for candidate in (sys.argv[0], sys.executable):
        path = os.path.abspath(candidate or "")
        name = os.path.basename(path).lower()
        if path and name.endswith(".exe") and "python" not in name:
            return path
    return os.path.abspath(sys.executable)


def can_replace_current_executable(path=None):
    path = path or current_executable_path()
    return os.name == "nt" and os.path.basename(path).lower().endswith(".exe") and "python" not in os.path.basename(path).lower()


def _powershell_quote(value):
    return "'" + str(value).replace("'", "''") + "'"


def _powershell_encoded_command(command):
    return base64.b64encode(command.encode("utf-16le")).decode("ascii")


def _target_directory_writable(destination):
    directory = os.path.dirname(os.path.abspath(destination))
    probe = os.path.join(directory, f".mcgo-update-write-test-{uuid.uuid4().hex}.tmp")
    try:
        with open(probe, "w", encoding="ascii") as handle:
            handle.write("test")
        return True
    except OSError:
        return False
    finally:
        with contextlib.suppress(OSError):
            os.remove(probe)


def _build_update_apply_command(download_path, executable_path, parent_pid, log_path, elevated=False):
    return f"""
$Source = {_powershell_quote(download_path)}
$Destination = {_powershell_quote(executable_path)}
$ParentPid = {int(parent_pid)}
$LogPath = {_powershell_quote(log_path)}
$Elevated = ${str(bool(elevated)).lower()}
$ErrorActionPreference = 'Stop'
function Write-UpdateLog([string]$message) {{
  Add-Content -LiteralPath $LogPath -Value ((Get-Date -Format o) + ' ' + $message) -Encoding UTF8
}}
Write-UpdateLog "update started pid=$ParentPid source=$Source destination=$Destination elevated=$Elevated"
try {{
  Wait-Process -Id $ParentPid -Timeout 120 -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 800
  for ($i = 0; $i -lt 120; $i++) {{
    try {{
      Copy-Item -LiteralPath $Source -Destination $Destination -Force
      Write-UpdateLog "copy succeeded"
      Start-Process -FilePath $Destination
      Remove-Item -LiteralPath $Source -Force -ErrorAction SilentlyContinue
      Write-UpdateLog "restart succeeded"
      break
    }} catch {{
      Write-UpdateLog ("copy attempt " + ($i + 1) + " failed: " + $_.Exception.Message)
      Start-Sleep -Seconds 1
      if ($i -eq 119) {{ throw }}
    }}
  }}
}} catch {{
  Write-UpdateLog ("update failed: " + $_.Exception.Message)
  throw
}}
"""


def _build_elevation_command(encoded_apply_command):
    return f"""
$ErrorActionPreference = 'Stop'
$args = @(
  '-NoProfile',
  '-ExecutionPolicy', 'Bypass',
  '-EncodedCommand', {_powershell_quote(encoded_apply_command)}
)
Start-Process -FilePath 'powershell.exe' -ArgumentList $args -Verb RunAs -WindowStyle Hidden
"""


def schedule_windows_replace_and_restart(download_path, executable_path=None):
    executable_path = os.path.abspath(executable_path or current_executable_path())
    download_path = os.path.abspath(download_path)
    if not can_replace_current_executable(executable_path):
        raise RuntimeError("当前不是打包后的 McGo.exe，无法执行自更新替换。")
    log_path = os.path.join(tempfile.gettempdir(), "mcgo-update.log")
    pid = os.getpid()
    needs_elevation = not _target_directory_writable(executable_path)
    apply_command = _build_update_apply_command(download_path, executable_path, pid, log_path, elevated=needs_elevation)
    encoded_command = _powershell_encoded_command(apply_command)
    if needs_elevation:
        encoded_command = _powershell_encoded_command(_build_elevation_command(encoded_command))
    powershell_args = [
        "powershell.exe",
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-EncodedCommand",
        encoded_command,
    ]
    popen_kwargs = {"close_fds": True}
    if os.name == "nt":
        creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        if creationflags:
            popen_kwargs["creationflags"] = creationflags
        startupinfo_type = getattr(subprocess, "STARTUPINFO", None)
        startf_use_show_window = getattr(subprocess, "STARTF_USESHOWWINDOW", 0)
        if startupinfo_type and startf_use_show_window:
            startupinfo = startupinfo_type()
            startupinfo.dwFlags |= startf_use_show_window
            startupinfo.wShowWindow = getattr(subprocess, "SW_HIDE", 0)
            popen_kwargs["startupinfo"] = startupinfo
    subprocess.Popen(powershell_args, **popen_kwargs)
    return {"elevated": needs_elevation, "log_path": log_path}
