import asyncio
import configparser
import json
import os
import re
import sys
import shutil
import subprocess
import tarfile
import threading
import tempfile
import time
import types
import uuid as uuidlib
import webbrowser
import zipfile
import html
from io import BytesIO
from collections import deque
from urllib.parse import urlparse

import requests
from flask import Flask, request
from markupsafe import escape
from PyQt6.QtCore import (
    QEasingCurve,
    QPoint,
    QPropertyAnimation,
    QParallelAnimationGroup,
    QUrl,
    QThread,
    QTimer,
    Qt,
    QObject,
    pyqtSignal as Signal,
)
from PyQt6.QtMultimedia import QAudioOutput, QMediaPlayer
from PyQt6.QtWidgets import (
    QApplication,
    QAbstractItemView,
    QFileDialog,
    QFrame,
    QGraphicsOpacityEffect,
    QGridLayout,
    QHBoxLayout,
    QLabel,
    QMessageBox,
    QListWidgetItem,
    QSizePolicy,
    QTextEdit,
    QStackedWidget,
    QVBoxLayout,
    QWidget,
)
from qfluentwidgets import (
    Action,
    BodyLabel,
    BreadcrumbBar,
    CaptionLabel,
    CardWidget,
    CheckBox,
    ComboBox,
    FluentIcon,
    FluentWindow,
    IndeterminateProgressRing,
    InfoBar,
    InfoBarPosition,
    LineEdit,
    ListWidget,
    PasswordLineEdit,
    Pivot,
    PrimaryPushButton,
    ProgressBar,
    PushButton,
    RoundMenu,
    ScrollArea,
    SegmentedWidget,
    SmoothMode,
    SpinBox,
    Slider,
    SubtitleLabel,
    TextEdit,
    Theme,
    TitleLabel,
    setTheme,
)
from qfluentwidgets.common.smooth_scroll import SmoothMode as NativeSmoothMode
from qfluentwidgets.common.animation import FluentAnimation
from qfluentwidgets.components.navigation.navigation_panel import NavigationDisplayMode, NavigationTreeWidgetBase
from qfluentwidgets.components.widgets.combo_box import ComboBoxMenu

from auth import MicrosoftAuthenticator
from downloader import collect_missing_game_files, download_game_files, extract_natives, repair_game_files
from java_utils import find_java_paths, get_java_major_version, get_java_version
from launcher import build_launch_command, get_local_versions, get_version_inheritance_chain, infer_required_java_version, launch_minecraft, get_version_json
from log_utils import get_logger, redact_mapping, setup_logging
from version_utils import (
    find_matching_fabric_versions,
    launch_options_for_version,
    load_version_settings,
    mods_directory_for_version,
    resolve_base_minecraft_version,
    runtime_directory_for_version,
    save_version_settings,
    version_display_name,
    version_matches_category,
    version_settings_entry,
    version_type_label,
)

client_id = "cf1d47c2-2199-495a-9822-a2a2b97cd568"
redirect_uri = "http://localhost:5000/login/callback"

game_directory = ".minecraft"
config_file = "launcher_config.ini"
accounts_file = "accounts.json"
AUTHLIB_INJECTOR_METADATA_URL = "https://authlib-injector.yushi.moe/artifact/latest.json"

MIRROR_SOURCES = {
    "official": "https://launchermeta.mojang.com",
    "bmclapi": "https://bmclapi2.bangbang93.com",
}

config = configparser.ConfigParser()
authenticator = MicrosoftAuthenticator(client_id, redirect_uri)
app = Flask(__name__)
LOG_PATH = setup_logging()
logger = get_logger(__name__)


def hidden_subprocess_kwargs():
    if os.name != "nt":
        return {}

    startupinfo = subprocess.STARTUPINFO()
    startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    return {
        "startupinfo": startupinfo,
        "creationflags": creationflags,
    }


def system_memory_mb():
    if os.name == "nt":
        try:
            import ctypes

            class MemoryStatusEx(ctypes.Structure):
                _fields_ = [
                    ("dwLength", ctypes.c_ulong),
                    ("dwMemoryLoad", ctypes.c_ulong),
                    ("ullTotalPhys", ctypes.c_ulonglong),
                    ("ullAvailPhys", ctypes.c_ulonglong),
                    ("ullTotalPageFile", ctypes.c_ulonglong),
                    ("ullAvailPageFile", ctypes.c_ulonglong),
                    ("ullTotalVirtual", ctypes.c_ulonglong),
                    ("ullAvailVirtual", ctypes.c_ulonglong),
                    ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
                ]

            status = MemoryStatusEx()
            status.dwLength = ctypes.sizeof(MemoryStatusEx)
            ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status))
            return int(status.ullTotalPhys / 1024 / 1024), int(status.ullAvailPhys / 1024 / 1024)
        except Exception:
            logger.debug("Failed to query Windows memory", exc_info=True)
    return 0, 0


def recommended_memory_mb():
    total_mb, available_mb = system_memory_mb()
    if available_mb <= 0:
        return 4096
    reserve_mb = 2048 if total_mb >= 8192 else 1536
    recommended = max(2048, min(8192, available_mb - reserve_mb))
    if total_mb and total_mb <= 4096:
        recommended = max(1536, min(2048, available_mb - 1024))
    return max(1024, int(recommended // 256 * 256))


@app.route("/login/callback")
def login_callback():
    error = request.args.get("error")
    if error:
        logger.warning("Microsoft OAuth callback failed: error=%s", error)
        return render_oauth_callback_page(
            "Microsoft 登录失败",
            "Microsoft 返回了错误，请回到 McGo 查看状态日志并重新登录。",
            status="error",
            detail=request.args.get("error_description", error),
        ), 400

    code = request.args.get("code")
    if not code:
        logger.warning("Microsoft OAuth callback missing authorization code")
        return render_oauth_callback_page(
            "缺少授权码",
            "回调地址没有收到授权码，请回到 McGo 重新发起 Microsoft 登录。",
            status="error",
        ), 400

    authenticator.authorization_code = code
    logger.info("Microsoft OAuth callback received: has_code=%s", bool(authenticator.authorization_code))
    return render_oauth_callback_page(
        "登录成功",
        "McGo 已收到 Microsoft 授权码。你可以关闭此页面，回到启动器继续。",
    )


@app.route("/")
def oauth_callback_index():
    return render_oauth_callback_page(
        "McGo 登录回调服务",
        "这个本地页面用于接收 Microsoft 登录回调。请从 McGo 启动器中发起登录。",
    )


def render_oauth_callback_page(title, message, status="success", detail=""):
    is_success = status == "success"
    accent = "#2e7d32" if is_success else "#b3261e"
    icon = "✓" if is_success else "!"
    escaped_title = escape(title)
    escaped_message = escape(message)
    escaped_detail = escape(detail)
    detail_html = f"<p class='detail'>{escaped_detail}</p>" if detail else ""
    return f"""<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{escaped_title}</title>
  <style>
    :root {{
      color-scheme: light dark;
      font-family: "Segoe UI", "Microsoft YaHei", Arial, sans-serif;
    }}
    body {{
      margin: 0;
      min-height: 100vh;
      display: grid;
      place-items: center;
      background: #f4f6f8;
      color: #1f1f1f;
    }}
    main {{
      width: min(520px, calc(100vw - 32px));
      padding: 32px;
      border: 1px solid rgba(0, 0, 0, 0.08);
      border-radius: 10px;
      background: white;
      box-shadow: 0 18px 48px rgba(0, 0, 0, 0.10);
    }}
    .icon {{
      width: 48px;
      height: 48px;
      display: grid;
      place-items: center;
      border-radius: 50%;
      background: {accent};
      color: white;
      font-size: 28px;
      font-weight: 700;
      margin-bottom: 18px;
    }}
    h1 {{
      margin: 0 0 10px;
      font-size: 24px;
      font-weight: 650;
    }}
    p {{
      margin: 0;
      line-height: 1.65;
      color: #4b5563;
    }}
    .detail {{
      margin-top: 12px;
      padding: 12px;
      border-radius: 8px;
      background: rgba(0, 0, 0, 0.04);
      word-break: break-word;
    }}
    @media (prefers-color-scheme: dark) {{
      body {{
        background: #171717;
        color: #f5f5f5;
      }}
      main {{
        background: #242424;
        border-color: rgba(255, 255, 255, 0.10);
      }}
      p {{
        color: #c9c9c9;
      }}
      .detail {{
        background: rgba(255, 255, 255, 0.08);
      }}
    }}
  </style>
</head>
<body>
  <main>
    <div class="icon">{icon}</div>
    <h1>{escaped_title}</h1>
    <p>{escaped_message}</p>
    {detail_html}
  </main>
</body>
</html>"""


def load_config():
    logger.debug("Loading config from %s", os.path.abspath(config_file))
    config.read(config_file)
    defaults = {
        "USER": {"username": "", "uuid": "", "accessToken": ""},
        "DOWNLOAD": {
            "mirror_source": "official",
            "max_core_threads": "12",
            "max_asset_threads": "24",
            "speed_limit_kbps": "0",
            "cache_strategy": "reuse",
        },
        "AUTH": {
            "use_microsoft_login": "False",
            "refresh_token": "",
            "auto_open_browser": "True",
        },
        "GAME": {"directory": game_directory, "enable_resource_isolation": "True"},
        "UI": {"advanced_mode": "False", "theme": "dark", "theme_image": ""},
        "HOME": {"content_source": "", "allow_network": "False"},
        "MUSIC": {"path": "", "enabled": "False", "volume": "35", "pause_on_launch": "True"},
        "FEATURES": {"show_download": "True", "show_manage": "True"},
        "SERVERS": {"items": ""},
        "ACCOUNTS": {"selected_account_id": ""},
    }
    for section, values in defaults.items():
        if not config.has_section(section):
            config.add_section(section)
        for key, value in values.items():
            if not config.has_option(section, key):
                config.set(section, key, value)
    save_config()
    logger.debug("Config loaded with sections: %s", config.sections())


def save_config():
    with open(config_file, "w") as f:
        config.write(f)
    logger.debug("Config saved to %s", os.path.abspath(config_file))


def account_label(account):
    labels = {
        "microsoft": "Microsoft",
        "external": "外置登录",
        "offline": "离线",
    }
    account_type = labels.get(account.get("type"), account.get("type", "离线"))
    return f"{account.get('display_name', account.get('username', '未命名'))} ({account_type})"


def load_accounts():
    if os.path.exists(accounts_file):
        with open(accounts_file, "r", encoding="utf-8") as f:
            accounts = json.load(f)
        logger.debug("Loaded %d accounts from %s", len(accounts), os.path.abspath(accounts_file))
        return accounts

    accounts = []
    username = config.get("USER", "username", fallback="").strip()
    if username:
        accounts.append({
            "id": str(uuidlib.uuid4()),
            "type": "offline",
            "display_name": username,
            "username": username,
            "uuid": config.get("USER", "uuid", fallback="").strip(),
            "access_token": config.get("USER", "accessToken", fallback="").strip(),
            "refresh_token": "",
        })

    refresh_token = config.get("AUTH", "refresh_token", fallback="").strip()
    if refresh_token:
        accounts.append({
            "id": str(uuidlib.uuid4()),
            "type": "microsoft",
            "display_name": "Microsoft 账号",
            "username": "",
            "uuid": "",
            "access_token": "",
            "refresh_token": refresh_token,
        })

    save_accounts(accounts)
    logger.debug("Migrated %d accounts from launcher_config.ini", len(accounts))
    return accounts


def save_accounts(accounts):
    with open(accounts_file, "w", encoding="utf-8") as f:
        json.dump(accounts, f, ensure_ascii=False, indent=2)
    logger.debug("Saved %d accounts to %s", len(accounts), os.path.abspath(accounts_file))


def fallback_mirror_source(mirror_source):
    return "bmclapi" if mirror_source == "official" else "official"


def mirror_source_sequence(mirror_source):
    primary = mirror_source if mirror_source in MIRROR_SOURCES else "official"
    fallback = fallback_mirror_source(primary)
    return [primary, fallback] if fallback != primary else [primary]


def rewrite_download_url_for_mirror(url, mirror_source):
    mirror = MIRROR_SOURCES.get(mirror_source, MIRROR_SOURCES["official"])
    if mirror_source == "bmclapi":
        replacements = {
            "https://piston-data.mojang.com": mirror,
            "https://launcher.mojang.com": mirror,
            "https://libraries.minecraft.net": f"{mirror}/maven",
            "https://resources.download.minecraft.net": f"{mirror}/assets",
            "https://launchermeta.mojang.com": mirror,
        }
        for source, target in replacements.items():
            if url.startswith(source):
                return url.replace(source, target, 1)
    elif mirror_source == "official":
        replacements = {
            f"{MIRROR_SOURCES['bmclapi']}/maven": "https://libraries.minecraft.net",
            f"{MIRROR_SOURCES['bmclapi']}/assets": "https://resources.download.minecraft.net",
            MIRROR_SOURCES["bmclapi"]: "https://piston-data.mojang.com",
        }
        for source, target in replacements.items():
            if url.startswith(source):
                return url.replace(source, target, 1)
    return url


def get_remote_versions(version_type, mirror_source):
    logger.info("Fetching remote versions: type=%s mirror=%s", version_type, mirror_source)
    response = requests.get(f"{MIRROR_SOURCES[mirror_source]}/mc/game/version_manifest.json", timeout=30)
    response.raise_for_status()
    versions = [v["id"] for v in response.json()["versions"] if v["type"] == version_type]
    logger.info("Fetched %d remote versions: type=%s mirror=%s", len(versions), version_type, mirror_source)
    return versions


def get_version_url(version_id, mirror_source):
    logger.debug("Resolving version metadata URL: version=%s mirror=%s", version_id, mirror_source)
    response = requests.get(f"{MIRROR_SOURCES[mirror_source]}/mc/game/version_manifest.json", timeout=30)
    response.raise_for_status()
    for version in response.json()["versions"]:
        if version["id"] == version_id:
            logger.debug("Resolved version metadata URL: version=%s url=%s", version_id, version["url"])
            return version["url"]
    logger.warning("Version metadata URL not found: version=%s mirror=%s", version_id, mirror_source)
    return None


def get_version_metadata_with_fallback(version_id, mirror_source, status_callback=None):
    errors = []
    for source in mirror_source_sequence(mirror_source):
        try:
            if status_callback and source != mirror_source:
                status_callback(f"主下载源失败，正在切换到 {source} 获取版本信息...")
            version_url = get_version_url(version_id, source)
            if not version_url:
                raise RuntimeError(f"无法获取版本 {version_id} 的下载地址")
            version_url = rewrite_download_url_for_mirror(version_url, source)
            response = requests.get(version_url, timeout=30)
            response.raise_for_status()
            return response.json(), source
        except Exception as exc:
            logger.warning("Version metadata fetch failed: version=%s mirror=%s error=%s", version_id, source, exc)
            errors.append(f"{source}: {exc}")
    raise RuntimeError("；".join(errors) or f"无法获取版本 {version_id} 的下载地址")


def mirror_root(mirror_source):
    return MIRROR_SOURCES.get(mirror_source, MIRROR_SOURCES["official"])


def stream_download(url, file_path, progress_callback=None, status_label="下载中", mirror_source=None):
    os.makedirs(os.path.dirname(file_path), exist_ok=True)
    logger.info("Starting stream download: label=%s target=%s url=%s", status_label, os.path.abspath(file_path), url)
    downloaded = 0
    try:
        response = None
        last_error = None
        for source in mirror_source_sequence(mirror_source or "official"):
            candidate_url = rewrite_download_url_for_mirror(url, source)
            try:
                response = requests.get(candidate_url, stream=True, timeout=60)
                response.raise_for_status()
                url = candidate_url
                break
            except Exception as exc:
                if response is not None:
                    response.close()
                response = None
                last_error = exc
                logger.warning("Stream download source failed: label=%s mirror=%s url=%s error=%s", status_label, source, candidate_url, exc)
        if response is None:
            raise last_error or RuntimeError(f"下载失败：{url}")
        with response:
            total = int(response.headers.get("content-length") or 0)
            logger.debug(
                "Stream download response: label=%s status=%s total_bytes=%d target=%s",
                status_label,
                response.status_code,
                total,
                os.path.abspath(file_path),
            )
            last_tick = time.monotonic()
            last_bytes = 0
            with open(file_path, "wb") as file_handle:
                for chunk in response.iter_content(chunk_size=128 * 1024):
                    if not chunk:
                        continue
                    file_handle.write(chunk)
                    downloaded += len(chunk)
                    now = time.monotonic()
                    if progress_callback and total:
                        speed = 0
                        elapsed = max(now - last_tick, 1e-6)
                        if downloaded > last_bytes:
                            speed = max(0, int((downloaded - last_bytes) / elapsed))
                            last_bytes = downloaded
                            last_tick = now
                        progress_callback({
                            "progress": min(1.0, downloaded / total),
                            "phase": status_label,
                            "current_file": os.path.basename(file_path),
                            "downloaded_bytes": downloaded,
                            "total_bytes": total,
                            "speed_bytes": speed,
                            "completed_files": 0,
                            "total_files": 1,
                            "reused_files": 0,
                        })
            if progress_callback and total:
                progress_callback({
                    "progress": 1.0,
                    "phase": status_label,
                    "current_file": os.path.basename(file_path),
                    "downloaded_bytes": downloaded,
                    "total_bytes": total,
                    "speed_bytes": 0,
                    "completed_files": 1,
                    "total_files": 1,
                    "reused_files": 0,
                })
        logger.info("Stream download finished: label=%s bytes=%d target=%s", status_label, downloaded, os.path.abspath(file_path))
    except Exception:
        logger.exception("Stream download failed: label=%s bytes=%d target=%s url=%s", status_label, downloaded, os.path.abspath(file_path), url)
        raise


def extract_zip_bytes(zip_bytes, target_dir):
    with zipfile.ZipFile(BytesIO(zip_bytes)) as zip_file:
        zip_file.extractall(target_dir)


def version_key(version):
    parts = re.split(r"[.\-+_]", str(version))
    key = []
    for part in parts:
        if part.isdigit():
            key.append((0, int(part)))
        else:
            key.append((1, part))
    return key


def library_relative_path(library_name):
    parts = library_name.split(":")
    if len(parts) != 3:
        return None
    group, artifact, version = parts
    return os.path.join(*group.split("."), artifact, version, f"{artifact}-{version}.jar")


def mirror_library_base(library_url, mirror_source):
    if mirror_source == "bmclapi":
        if "maven.fabricmc.net" in library_url:
            return f"{mirror_root(mirror_source)}/maven"
        if "maven.neoforged.net" in library_url:
            return f"{mirror_root(mirror_source)}/maven"
        if "libraries.minecraft.net" in library_url:
            return f"{mirror_root(mirror_source)}/maven"
        if "maven.minecraftforge.net" in library_url:
            return f"{mirror_root(mirror_source)}/maven"
    return library_url.rstrip("/")


def download_profile_libraries(profile_json, game_dir, mirror_source, progress_callback=None):
    libraries = profile_json.get("libraries", [])
    if not libraries:
        return

    total_files = len(libraries)
    completed_files = 0
    for library in libraries:
        name = library.get("name", "")
        relative_path = library_relative_path(name)
        if not relative_path:
            continue
        url_base = mirror_library_base(library.get("url", "https://libraries.minecraft.net/"), mirror_source)
        artifact_url = f"{url_base}/{relative_path.replace(os.sep, '/')}"
        target_path = os.path.join(game_dir, "libraries", relative_path)
        if os.path.exists(target_path):
            completed_files += 1
            if progress_callback:
                progress_callback({
                    "progress": min(1.0, completed_files / max(total_files, 1)),
                    "phase": "复用安装依赖",
                    "current_file": os.path.basename(target_path),
                    "downloaded_bytes": completed_files,
                    "total_bytes": total_files,
                    "speed_bytes": 0,
                    "completed_files": completed_files,
                    "total_files": total_files,
                    "reused_files": completed_files,
                })
            continue
        stream_download(artifact_url, target_path, progress_callback, "下载安装依赖")
        completed_files += 1


def get_fabric_loader_versions(game_version, mirror_source):
    base = mirror_root(mirror_source)
    response = requests.get(f"{base}/fabric-meta/v2/versions/loader/{game_version}", timeout=30)
    response.raise_for_status()
    return response.json()


def get_forge_promos(mirror_source):
    if mirror_source == "bmclapi":
        url = f"{mirror_root(mirror_source)}/forge/promos"
    else:
        url = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json"
    response = requests.get(url, timeout=30)
    response.raise_for_status()
    data = response.json()
    if isinstance(data, dict) and "promos" in data:
        return data["promos"]
    return data


def get_forge_version_for_mc(game_version, mirror_source):
    promos = get_forge_promos(mirror_source)
    if isinstance(promos, list):
        preferred = []
        fallback = []
        for item in promos:
            build = item.get("build")
            if not isinstance(build, dict):
                continue
            if build.get("mcversion") != game_version:
                continue
            version = build.get("version")
            if not version:
                continue
            if "recommended" in item.get("name", ""):
                preferred.append(version)
            else:
                fallback.append(version)
        if preferred:
            return sorted(preferred, key=version_key, reverse=True)[0]
        if fallback:
            return sorted(fallback, key=version_key, reverse=True)[0]
        return None

    candidates = [
        promos.get(f"{game_version}-recommended"),
        promos.get(f"{game_version}-latest"),
    ]
    for candidate in candidates:
        if candidate:
            return candidate
    return None


def get_neoforge_list(game_version, mirror_source):
    if mirror_source == "bmclapi":
        url = f"{mirror_root(mirror_source)}/neoforge/list/{game_version}"
        response = requests.get(url, timeout=30)
        response.raise_for_status()
        return response.json()
    raise RuntimeError("NeoForge 安装当前仅支持 BMCLAPI 镜像。")


def get_optifine_list(mirror_source):
    if mirror_source == "bmclapi":
        url = f"{mirror_root(mirror_source)}/optifine/versionList"
        response = requests.get(url, timeout=30)
        response.raise_for_status()
        return response.json()
    raise RuntimeError("OptiFine 安装当前仅支持 BMCLAPI 镜像。")


def get_fabric_api_versions(game_version):
    response = requests.get(
        "https://api.modrinth.com/v2/project/fabric-api/version",
        params={
            "loaders": json.dumps(["fabric"]),
            "game_versions": json.dumps([game_version]),
        },
        timeout=30,
    )
    response.raise_for_status()
    data = response.json()
    if isinstance(data, dict) and "value" in data:
        data = data["value"]
    return data or []


def build_optifine_version_id(minecraft_version, selected):
    filename = (selected.get("filename") or "").strip()
    if filename:
        name = filename
        if name.lower().endswith(".jar"):
            name = name[:-4]
        if name.startswith("preview_"):
            name = name[len("preview_"):]
        prefix = f"OptiFine_{minecraft_version}_"
        if name.startswith(prefix):
            return f"{minecraft_version}-OptiFine_{name[len(prefix):]}"

    optifine_type = (selected.get("type") or "HD_U").strip()
    patch = (selected.get("patch") or "").strip()
    suffix = optifine_type if not patch else f"{optifine_type}_{patch}"
    return f"{minecraft_version}-OptiFine_{suffix}"


def build_optifine_library_version(selected):
    filename = (selected.get("filename") or "").strip()
    if filename:
        name = filename[:-4] if filename.lower().endswith(".jar") else filename
        if name.startswith("preview_"):
            name = name[len("preview_"):]
        prefix = "OptiFine_"
        if name.startswith(prefix):
            return name[len(prefix):]
        return name

    minecraft_version = (selected.get("mcversion") or "").strip()
    optifine_type = (selected.get("type") or "HD_U").strip()
    patch = (selected.get("patch") or "").strip()
    return f"{minecraft_version}_{optifine_type}" if not patch else f"{minecraft_version}_{optifine_type}_{patch}"


def select_optifine_candidate(candidates):
    stable = [item for item in candidates if not str(item.get("filename", "")).startswith("preview_")]
    ordered = stable or candidates
    return ordered[0] if ordered else None


INSTALL_TYPE_LABELS = {
    "fabric": "Fabric",
    "forge": "Forge",
    "neoforge": "NeoForge",
    "optifine": "OptiFine",
    "fabric_api": "Fabric API",
}


def sanitize_filename(value, fallback="ImportedPack"):
    cleaned = re.sub(r'[<>:"/\\|?*]+', "_", str(value or "").strip()).strip(". ")
    return cleaned or fallback


def sha1_file(path):
    import hashlib

    digest = hashlib.sha1()
    with open(path, "rb") as file_handle:
        for chunk in iter(lambda: file_handle.read(128 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


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


RESOURCE_TYPE_LABELS = {
    "mod": "Mod",
    "resourcepack": "资源包",
    "shader": "光影",
    "datapack": "数据包",
}


RESOURCE_TYPE_FACETS = {
    "mod": "project_type:mod",
    "resourcepack": "project_type:resourcepack",
    "shader": "project_type:shader",
    "datapack": "project_type:datapack",
}

RESOURCE_SOURCE_LABELS = {
    "modrinth": "Modrinth",
    "curseforge": "CurseForge",
    "local": "本地",
}

RESOURCE_SEARCH_SORTS = {
    "相关度": "relevance",
    "下载量": "downloads",
    "收藏数": "follows",
    "最近更新": "updated",
}

CURSEFORGE_CLASS_IDS = {
    "mod": 6,
    "resourcepack": 12,
    "shader": 6552,
    "datapack": 6945,
}

RESOURCE_EXTENSIONS = {
    "mod": (".jar", ".jar.disabled"),
    "resourcepack": (".zip",),
    "shader": (".zip",),
    "datapack": (".zip",),
}

VERSION_ICON_LABELS = ["自动", "草方块", "金块", "红石", "命令方块", "Fabric", "Forge", "NeoForge", "OptiFine"]


DOWNLOAD_PRESETS = {
    "保守": {"core": 6, "asset": 12, "speed_kbps": 0, "cache": "reuse"},
    "均衡": {"core": 12, "asset": 24, "speed_kbps": 0, "cache": "reuse"},
    "激进": {"core": 20, "asset": 40, "speed_kbps": 0, "cache": "reuse"},
}

GC_STRATEGIES = ["G1GC", "ZGC", "Shenandoah", "默认"]


class DownloadTask:
    def __init__(self, task_type, title, start_callback):
        self.task_type = task_type
        self.title = title
        self.start_callback = start_callback

    def start(self):
        self.start_callback()


def modrinth_loader_for_version(game_dir, version_id):
    version_type = version_type_label(game_dir, version_id)
    mapping = {
        "Fabric": "fabric",
        "Forge": "forge",
        "NeoForge": "neoforge",
        "OptiFine": "optifine",
    }
    return mapping.get(version_type, "")


def normalize_minecraft_version_for_api(game_dir, version_id):
    base_version = resolve_base_minecraft_version(game_dir, version_id)
    match = re.search(r"(?<![\d.])(?:1|2|3|4|20|21|22|23|24|25|26|27|28|29|30)(?:\.\d{1,2}){1,2}(?:[-_](?:pre|rc)\d+)?(?![\d.])", str(base_version), flags=re.IGNORECASE)
    if match:
        logger.debug("Normalized Minecraft version for API: version_id=%s base=%s normalized=%s", version_id, base_version, match.group(0))
        return match.group(0)
    snapshot = re.search(r"(?<![0-9a-z])(\d{2}w\d{2}[a-z])(?![0-9a-z])", str(base_version), flags=re.IGNORECASE)
    if snapshot:
        logger.debug("Normalized Minecraft snapshot for API: version_id=%s base=%s normalized=%s", version_id, base_version, snapshot.group(1))
        return snapshot.group(1)
    logger.warning("Could not normalize Minecraft version for API: version_id=%s base=%s", version_id, base_version)
    return base_version


def resource_directory_for_type(game_dir, settings, version_id, resource_type, global_isolation=False):
    runtime_dir = runtime_directory_for_version(game_dir, settings, version_id, global_isolation=global_isolation)
    if resource_type == "mod":
        return mods_directory_for_version(game_dir, settings, version_id, global_isolation=global_isolation)
    if resource_type == "resourcepack":
        return os.path.join(runtime_dir, "resourcepacks")
    if resource_type == "shader":
        return os.path.join(runtime_dir, "shaderpacks")
    if resource_type == "datapack":
        return os.path.join(runtime_dir, "saves")
    return runtime_dir


def search_modrinth_resources(query, resource_type, game_version="", loader="", limit=25, index="relevance"):
    def run_search(current_game_version="", current_loader=""):
        facets = [[RESOURCE_TYPE_FACETS.get(resource_type, "project_type:mod")]]
        if current_game_version:
            facets.append([f"versions:{current_game_version}"])
        if resource_type == "mod" and current_loader:
            facets.append([f"categories:{current_loader}"])
        response = requests.get(
            "https://api.modrinth.com/v2/search",
            params={
                "query": query,
                "limit": limit,
                "index": index,
                "facets": json.dumps(facets),
            },
            headers={"User-Agent": "McGo/1.0"},
            timeout=30,
        )
        response.raise_for_status()
        result_hits = response.json().get("hits", [])
        logger.info(
            "Modrinth search attempt: query=%s type=%s game=%s loader=%s index=%s hits=%d",
            query,
            resource_type,
            current_game_version or "<any>",
            current_loader or "<any>",
            index,
            len(result_hits),
        )
        return result_hits

    logger.info(
        "Modrinth search started: query=%s type=%s game=%s loader=%s index=%s limit=%d",
        query,
        resource_type,
        game_version or "<any>",
        loader or "<any>",
        index,
        limit,
    )
    attempts = [(game_version, loader)]
    if loader:
        attempts.append((game_version, ""))
    if game_version:
        attempts.append(("", loader))
    attempts.append(("", ""))

    seen_attempts = set()
    hits = []
    relaxed = False
    for current_game_version, current_loader in attempts:
        key = (current_game_version, current_loader)
        if key in seen_attempts:
            continue
        seen_attempts.add(key)
        hits = run_search(current_game_version, current_loader)
        if hits:
            relaxed = key != (game_version, loader)
            break

    for hit in hits:
        hit["source"] = "modrinth"
        hit["relaxed_search"] = relaxed
    logger.info(
        "Modrinth search finished: query=%s type=%s game=%s loader=%s hits=%d relaxed=%s",
        query,
        resource_type,
        game_version or "<any>",
        loader or "<any>",
        len(hits),
        relaxed,
    )
    return hits


def strict_modrinth_search(query, resource_type, game_version="", loader="", limit=25, index="relevance"):
    facets = [[RESOURCE_TYPE_FACETS.get(resource_type, "project_type:mod")]]
    if game_version:
        facets.append([f"versions:{game_version}"])
    if resource_type == "mod" and loader:
        facets.append([f"categories:{loader}"])
    response = requests.get(
        "https://api.modrinth.com/v2/search",
        params={
            "query": query,
            "limit": limit,
            "index": index,
            "facets": json.dumps(facets),
        },
        headers={"User-Agent": "McGo/1.0"},
        timeout=30,
    )
    response.raise_for_status()
    hits = response.json().get("hits", [])
    for hit in hits:
        hit["source"] = "modrinth"
    return hits


def get_modrinth_versions(project_id, resource_type, game_version, loader):
    params = {"game_versions": json.dumps([game_version])} if game_version else {}
    if resource_type == "mod" and loader:
        params["loaders"] = json.dumps([loader])
    logger.debug(
        "Fetching Modrinth versions: project=%s type=%s game=%s loader=%s",
        project_id,
        resource_type,
        game_version or "<any>",
        loader or "<any>",
    )
    response = requests.get(
        f"https://api.modrinth.com/v2/project/{project_id}/version",
        params=params,
        headers={"User-Agent": "McGo/1.0"},
        timeout=30,
    )
    response.raise_for_status()
    versions = response.json()
    logger.info(
        "Fetched Modrinth versions: project=%s type=%s game=%s loader=%s count=%d",
        project_id,
        resource_type,
        game_version or "<any>",
        loader or "<any>",
        len(versions),
    )
    return versions


def modrinth_version_is_compatible(version, resource_type, game_version="", loader=""):
    game_versions = {str(item).lower() for item in version.get("game_versions", [])}
    loaders = {str(item).lower() for item in version.get("loaders", [])}
    if game_version and str(game_version).lower() not in game_versions:
        return False
    if resource_type == "mod" and loader and str(loader).lower() not in loaders:
        return False
    return True


def find_compatible_modrinth_version(project_id, resource_type, game_version, loader):
    versions = get_modrinth_versions(project_id, resource_type, game_version, loader)
    if not versions and (game_version or loader):
        logger.info(
            "Modrinth exact version query returned no results, falling back to local filtering: project=%s type=%s game=%s loader=%s",
            project_id,
            resource_type,
            game_version or "<any>",
            loader or "<any>",
        )
        try:
            versions = get_modrinth_versions(project_id, resource_type, "", "")
        except Exception:
            logger.exception("Modrinth fallback version query failed: project=%s", project_id)
            versions = []
        versions = [
            item for item in versions
            if modrinth_version_is_compatible(item, resource_type, game_version, loader)
        ]
    preferred = [item for item in versions if item.get("version_type") == "release"]
    selected = (preferred or versions)[0] if versions else None
    if selected:
        logger.info(
            "Selected Modrinth version: project=%s version=%s type=%s game_versions=%s loaders=%s",
            project_id,
            selected.get("version_number", ""),
            selected.get("version_type", ""),
            ",".join(str(item) for item in selected.get("game_versions", [])),
            ",".join(str(item) for item in selected.get("loaders", [])),
        )
    else:
        logger.warning(
            "No compatible Modrinth version selected: project=%s type=%s game=%s loader=%s",
            project_id,
            resource_type,
            game_version or "<any>",
            loader or "<any>",
        )
    return selected


def hit_has_modrinth_compatibility(hit, resource_type, game_version="", loader=""):
    if game_version:
        versions = {str(item).lower() for item in hit.get("versions", [])}
        if versions and str(game_version).lower() not in versions:
            return False
    if resource_type == "mod" and loader:
        categories = {str(item).lower() for item in hit.get("categories", [])}
        display_categories = {str(item).lower() for item in hit.get("display_categories", [])}
        all_categories = categories | display_categories
        if all_categories and str(loader).lower() not in all_categories:
            return False
    return True


def select_modrinth_version(project_id, resource_type, game_version, loader):
    selected = find_compatible_modrinth_version(project_id, resource_type, game_version, loader)
    if not selected:
        target = f"Minecraft {game_version}" if game_version else "当前 Minecraft 版本"
        if resource_type == "mod" and loader:
            target += f" / {loader}"
        raise RuntimeError(f"这个资源没有适配 {target} 的可安装文件。")
    return selected


def install_modrinth_resource(project_id, resource_type, game_version, loader, target_dir, install_dependencies=False, installed=None, status_callback=None, progress_callback=None):
    installed = installed if installed is not None else set()
    project_key = f"modrinth:{project_id}"
    if project_key in installed:
        logger.debug("Skipping already installed Modrinth dependency: project=%s", project_id)
        return {"filename": "", "path": "", "version": "", "dependencies_installed": []}
    installed.add(project_key)

    logger.info(
        "Installing Modrinth resource: project=%s type=%s game=%s loader=%s target=%s dependencies=%s",
        project_id,
        resource_type,
        game_version or "<any>",
        loader or "<any>",
        os.path.abspath(target_dir),
        install_dependencies,
    )
    selected = select_modrinth_version(project_id, resource_type, game_version, loader)
    dependencies_installed = []
    if install_dependencies:
        for dependency in selected.get("dependencies", []):
            if dependency.get("dependency_type") != "required":
                continue
            dep_project_id = dependency.get("project_id")
            if not dep_project_id:
                continue
            logger.info(
                "Installing required Modrinth dependency: parent=%s dependency=%s",
                project_id,
                dep_project_id,
            )
            if status_callback:
                status_callback(f"正在安装依赖：{dep_project_id}")
            dep_payload = install_modrinth_resource(
                dep_project_id,
                resource_type,
                game_version,
                loader,
                target_dir,
                install_dependencies=True,
                installed=installed,
                status_callback=status_callback,
                progress_callback=progress_callback,
            )
            if dep_payload.get("filename"):
                dependencies_installed.append(dep_payload.get("filename", dep_project_id))
            dependencies_installed.extend(dep_payload.get("dependencies_installed", []))

    files = selected.get("files", [])
    primary = next((item for item in files if item.get("primary")), None) or (files[0] if files else None)
    if not primary or not primary.get("url"):
        logger.error(
            "Selected Modrinth version has no downloadable file: project=%s version=%s",
            project_id,
            selected.get("version_number", ""),
        )
        raise RuntimeError("资源版本缺少可下载文件。")

    filename = sanitize_filename(primary.get("filename") or f"{project_id}.jar", f"{project_id}.jar")
    os.makedirs(target_dir, exist_ok=True)
    target_path = os.path.join(target_dir, filename)
    logger.info(
        "Downloading Modrinth resource file: project=%s version=%s filename=%s size=%s target=%s",
        project_id,
        selected.get("version_number", ""),
        filename,
        primary.get("size", 0),
        os.path.abspath(target_path),
    )
    stream_download(primary["url"], target_path, progress_callback, "下载资源")
    expected_hash = primary.get("hashes", {}).get("sha1", "")
    if expected_hash and sha1_file(target_path).lower() != expected_hash.lower():
        logger.error("Modrinth resource SHA1 mismatch: project=%s target=%s", project_id, os.path.abspath(target_path))
        raise RuntimeError("资源文件 SHA1 校验失败。")
    logger.info(
        "Modrinth resource installed: project=%s version=%s filename=%s dependencies=%d",
        project_id,
        selected.get("version_number", ""),
        filename,
        len(dependencies_installed),
    )
    return {
        "filename": filename,
        "path": target_path,
        "version": selected.get("version_number", ""),
        "dependencies_installed": dependencies_installed,
    }


def get_modrinth_resource_detail(project_id, resource_type, game_version, loader):
    project_response = requests.get(
        f"https://api.modrinth.com/v2/project/{project_id}",
        headers={"User-Agent": "McGo/1.0"},
        timeout=30,
    )
    project_response.raise_for_status()
    project = project_response.json()
    versions = get_modrinth_versions(project_id, resource_type, game_version, loader)
    selected = (versions or [{}])[0]
    screenshots = project.get("gallery", [])[:5]
    dependencies = [
        item for item in selected.get("dependencies", [])
        if item.get("dependency_type") in {"required", "optional"}
    ]
    return {
        "source": "modrinth",
        "title": project.get("title", project_id),
        "description": project.get("description", ""),
        "body": project.get("body", ""),
        "downloads": project.get("downloads", 0),
        "followers": project.get("followers", 0),
        "project_url": f"https://modrinth.com/{project.get('project_type', 'mod')}/{project.get('slug', project_id)}",
        "screenshots": [item.get("url", "") for item in screenshots if item.get("url")],
        "dependencies": dependencies,
        "versions": versions[:8],
    }


def curseforge_headers():
    api_key = os.environ.get("CURSEFORGE_API_KEY", "").strip()
    if not api_key:
        raise RuntimeError("CurseForge 官方 API 需要 CURSEFORGE_API_KEY 环境变量。")
    return {"Accept": "application/json", "x-api-key": api_key, "User-Agent": "McGo/1.0"}


def search_curseforge_resources(query, resource_type, game_version="", loader="", limit=25):
    params = {
        "gameId": 432,
        "searchFilter": query,
        "pageSize": limit,
        "sortField": 2,
        "sortOrder": "desc",
    }
    class_id = CURSEFORGE_CLASS_IDS.get(resource_type)
    if class_id:
        params["classId"] = class_id
    if game_version:
        params["gameVersion"] = game_version
    response = requests.get(
        "https://api.curseforge.com/v1/mods/search",
        params=params,
        headers=curseforge_headers(),
        timeout=30,
    )
    response.raise_for_status()
    hits = []
    for item in response.json().get("data", []):
        links = item.get("links", {})
        logo = item.get("logo") or {}
        hits.append({
            "source": "curseforge",
            "project_id": item.get("id"),
            "title": item.get("name", ""),
            "slug": item.get("slug", ""),
            "description": item.get("summary", ""),
            "downloads": item.get("downloadCount", 0),
            "follows": item.get("thumbsUpCount", 0),
            "project_url": links.get("websiteUrl", ""),
            "icon_url": logo.get("thumbnailUrl", ""),
        })
    return hits


def get_curseforge_resource_detail(project_id):
    response = requests.get(
        f"https://api.curseforge.com/v1/mods/{project_id}",
        headers=curseforge_headers(),
        timeout=30,
    )
    response.raise_for_status()
    project = response.json().get("data", {})
    files_response = requests.get(
        f"https://api.curseforge.com/v1/mods/{project_id}/files",
        headers=curseforge_headers(),
        timeout=30,
    )
    files_response.raise_for_status()
    files = files_response.json().get("data", [])[:8]
    links = project.get("links", {})
    screenshots = [
        screenshot.get("url", "")
        for screenshot in project.get("screenshots", [])[:5]
        if screenshot.get("url")
    ]
    return {
        "source": "curseforge",
        "title": project.get("name", str(project_id)),
        "description": project.get("summary", ""),
        "body": "",
        "downloads": project.get("downloadCount", 0),
        "followers": project.get("thumbsUpCount", 0),
        "project_url": links.get("websiteUrl", ""),
        "screenshots": screenshots,
        "dependencies": [],
        "versions": files,
    }


def list_local_resources(query, resource_type, target_dir):
    lowered_query = query.lower()
    extensions = RESOURCE_EXTENSIONS.get(resource_type, (".jar", ".zip"))
    hits = []
    if not os.path.isdir(target_dir):
        return hits
    for current_root, _, files in os.walk(target_dir):
        for filename in files:
            lowered = filename.lower()
            if not lowered.endswith(extensions):
                continue
            if lowered_query and lowered_query not in lowered:
                continue
            path = os.path.join(current_root, filename)
            try:
                stat = os.stat(path)
            except OSError:
                continue
            hits.append({
                "source": "local",
                "project_id": path,
                "title": filename,
                "slug": filename,
                "description": os.path.relpath(path, target_dir),
                "downloads": stat.st_size,
                "follows": 0,
                "path": path,
                "updated": stat.st_mtime,
            })
    hits.sort(key=lambda item: item.get("updated", 0), reverse=True)
    return hits[:100]


def analyze_local_mod_file(path, mods_dir):
    basename = os.path.basename(path)
    lowered = basename.lower()
    status = "禁用" if lowered.endswith(".jar.disabled") else "启用"
    hints = []
    if lowered.endswith(".jar.disabled"):
        hints.append("当前不会被加载")
    dependency_markers = {
        "fabric-api": "Fabric API",
        "architectury": "Architectury API",
        "cloth-config": "Cloth Config",
        "modmenu": "Mod Menu",
        "geckolib": "GeckoLib",
        "balm": "Balm",
        "moonlight": "Moonlight Lib",
    }
    available = set()
    if os.path.isdir(mods_dir):
        available = {name.lower().replace(".disabled", "") for name in os.listdir(mods_dir)}
    for marker, label in dependency_markers.items():
        if marker in lowered:
            continue
        if any(marker in name for name in available):
            continue
        if marker in {"fabric-api"} and ("fabric" in lowered or basename.endswith(".jar")):
            hints.append(f"可能需要 {label}")
            break
    return status, hints


def get_local_resource_detail(path):
    stat = os.stat(path)
    status, hints = analyze_local_mod_file(path, os.path.dirname(path))
    return {
        "source": "local",
        "title": os.path.basename(path),
        "description": os.path.abspath(path),
        "body": "",
        "downloads": stat.st_size,
        "followers": 0,
        "project_url": os.path.abspath(os.path.dirname(path)),
        "screenshots": [],
        "dependencies": [{"dependency_type": "hint", "project_id": hint} for hint in hints],
        "versions": [],
        "status": status,
    }


def cache_resource_screenshot(url):
    if not url:
        return ""
    cache_dir = os.path.join(tempfile.gettempdir(), "mcgo-resource-screenshots")
    os.makedirs(cache_dir, exist_ok=True)
    extension = os.path.splitext(url.split("?", 1)[0])[1].lower()
    if extension not in {".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"}:
        extension = ".jpg"
    safe_name = sanitize_filename(sha1_text(url), "screenshot") + extension
    target_path = os.path.join(cache_dir, safe_name)
    if os.path.isfile(target_path) and os.path.getsize(target_path) > 0:
        return target_path
    response = requests.get(url, headers={"User-Agent": "McGo/1.0"}, timeout=30)
    response.raise_for_status()
    with open(target_path, "wb") as file_handle:
        file_handle.write(response.content)
    return target_path


def sha1_text(value):
    import hashlib

    return hashlib.sha1(str(value).encode("utf-8", errors="ignore")).hexdigest()


def current_platform_name():
    if sys.platform.startswith("win"):
        return "windows"
    if sys.platform == "darwin":
        return "mac"
    return "linux"


def current_arch_name():
    machine = os.environ.get("PROCESSOR_ARCHITECTURE", "")
    if not machine and hasattr(os, "uname"):
        machine = os.uname().machine
    machine = str(machine).lower()
    if machine in {"amd64", "x86_64"}:
        return "x64"
    if machine in {"aarch64", "arm64"}:
        return "aarch64"
    if machine in {"x86", "i386", "i686"}:
        return "x32"
    return "x64"


def java_runtime_download_url(major_version):
    response = requests.get(
        f"https://api.adoptium.net/v3/assets/latest/{major_version}/hotspot",
        params={
            "architecture": current_arch_name(),
            "image_type": "jre",
            "os": current_platform_name(),
            "vendor": "eclipse",
        },
        headers={"User-Agent": "McGo/1.0"},
        timeout=30,
    )
    response.raise_for_status()
    data = response.json()
    if not data:
        raise RuntimeError(f"未找到 Java {major_version} 的可下载运行时。")
    package = data[0].get("binary", {}).get("package", {})
    link = package.get("link")
    if not link:
        raise RuntimeError("Adoptium 返回数据缺少下载链接。")
    return link, package.get("name") or os.path.basename(link)


def find_java_in_directory(root_dir):
    candidates = []
    for current_root, _, files in os.walk(root_dir):
        for filename in files:
            if filename not in ("java", "java.exe"):
                continue
            path = os.path.join(current_root, filename)
            if os.path.basename(os.path.dirname(path)).lower() == "bin":
                candidates.append(path)
    candidates.sort(key=lambda path: (len(path), path))
    return candidates[0] if candidates else ""


def extract_java_archive(archive_path, target_dir):
    if zipfile.is_zipfile(archive_path):
        with zipfile.ZipFile(archive_path, "r") as archive:
            archive.extractall(target_dir)
        return
    if tarfile.is_tarfile(archive_path):
        with tarfile.open(archive_path, "r:*") as archive:
            archive.extractall(target_dir)
        return
    raise RuntimeError("Java 运行时压缩包格式不受支持。")


def normalize_auth_server(server_url):
    server = (server_url or "").strip().rstrip("/")
    if not server:
        raise RuntimeError("请填写外置登录服务器地址。")
    if not server.startswith(("http://", "https://")):
        server = "https://" + server
    parsed = urlparse(server)
    if parsed.scheme not in {"http", "https"} or not parsed.netloc:
        raise RuntimeError("外置登录服务器地址格式不正确。")
    if parsed.scheme == "http" and parsed.hostname not in {"localhost", "127.0.0.1", "::1"}:
        raise RuntimeError("外置登录服务器必须使用 HTTPS，本机测试地址除外。")
    if server.endswith("/authserver"):
        server = server[: -len("/authserver")]
    return server


def external_auth_endpoint(server, action):
    return f"{normalize_auth_server(server)}/authserver/{action}"


def external_auth_headers():
    return {"Content-Type": "application/json", "Accept": "application/json", "User-Agent": "McGo/1.0"}


def external_auth_error(response, action):
    labels = {
        "authenticate": "登录",
        "refresh": "刷新",
        "validate": "验证",
    }
    action_label = labels.get(action, action)
    detail = ""
    try:
        data = response.json()
        detail = data.get("errorMessage") or data.get("error") or ""
    except ValueError:
        detail = response.text[:300].strip()
    if response.status_code in {401, 403}:
        return f"外置登录{action_label}被拒绝，请检查用户名、密码或令牌。{('详情：' + detail) if detail else ''}"
    if response.status_code == 404:
        return "认证端点不存在，请确认地址是 Yggdrasil/Authlib-Injector 根地址，例如 https://example.com/api/yggdrasil。"
    if response.status_code >= 500:
        return f"认证服务器内部错误（{response.status_code}）。{('详情：' + detail) if detail else ''}"
    return f"外置登录{action_label}失败（HTTP {response.status_code}）。{('详情：' + detail) if detail else ''}"


def raise_for_external_auth(response, action):
    if 200 <= response.status_code < 300:
        return
    raise RuntimeError(external_auth_error(response, action))


def probe_external_auth_server(server_url):
    server = normalize_auth_server(server_url)
    probes = [
        f"{server}/authserver",
        f"{server}/authserver/validate",
        server,
    ]
    errors = []
    for url in probes:
        try:
            if url.endswith("/validate"):
                response = requests.post(url, json={"accessToken": "mcgo-probe"}, headers=external_auth_headers(), timeout=10)
            else:
                response = requests.get(url, headers={"Accept": "application/json", "User-Agent": "McGo/1.0"}, timeout=10)
            if response.status_code < 500:
                return {
                    "server": server,
                    "status": response.status_code,
                    "message": f"服务器可访问，探测端点返回 HTTP {response.status_code}",
                }
            errors.append(f"{url}: HTTP {response.status_code}")
        except requests.RequestException as exc:
            errors.append(f"{url}: {exc}")
    raise RuntimeError("认证服务器不可访问：" + "；".join(errors[-2:]))


def authenticate_external_account(server_url, username, password, client_token=""):
    server = normalize_auth_server(server_url)
    if not username.strip() or not password:
        raise RuntimeError("外置登录需要用户名和密码。")
    client_token = client_token or str(uuidlib.uuid4())
    logger.info("Authenticating external account: server=%s username=%s", server, username.strip())
    payload = {
        "agent": {"name": "Minecraft", "version": 1},
        "username": username.strip(),
        "password": password,
        "clientToken": client_token,
        "requestUser": True,
    }
    try:
        response = requests.post(
            external_auth_endpoint(server, "authenticate"),
            json=payload,
            headers=external_auth_headers(),
            timeout=30,
        )
    except requests.RequestException as exc:
        raise RuntimeError(f"无法连接外置登录服务器：{exc}") from exc
    raise_for_external_auth(response, "authenticate")
    try:
        data = response.json()
    except ValueError as exc:
        raise RuntimeError("外置登录服务器返回的不是有效 JSON。") from exc
    selected = data.get("selectedProfile") or {}
    if not selected.get("id") or not selected.get("name") or not data.get("accessToken"):
        raise RuntimeError("外置登录服务器返回的数据不完整。")
    logger.info("External account authenticated: server=%s username=%s uuid=%s", server, selected.get("name"), selected.get("id"))
    return {
        "server": server,
        "username": selected.get("name"),
        "display_name": selected.get("name"),
        "uuid": selected.get("id"),
        "access_token": data.get("accessToken"),
        "client_token": data.get("clientToken", client_token),
    }


def refresh_external_account(account):
    server = normalize_auth_server(account.get("auth_server", ""))
    access_token = account.get("access_token", "")
    client_token = account.get("client_token", "")
    if not access_token:
        raise RuntimeError("外置登录账号缺少 Access Token，请重新登录。")
    logger.info(
        "Refreshing external account: server=%s username=%s uuid=%s",
        server,
        account.get("username", ""),
        account.get("uuid", ""),
    )
    payload = {
        "accessToken": access_token,
        "clientToken": client_token,
        "requestUser": True,
    }
    try:
        response = requests.post(
            external_auth_endpoint(server, "refresh"),
            json=payload,
            headers=external_auth_headers(),
            timeout=30,
        )
    except requests.RequestException as exc:
        raise RuntimeError(f"无法连接外置登录服务器：{exc}") from exc
    if response.status_code == 403:
        validate_payload = {"accessToken": access_token, "clientToken": client_token}
        try:
            validate_response = requests.post(
                external_auth_endpoint(server, "validate"),
                json=validate_payload,
                headers=external_auth_headers(),
                timeout=30,
            )
        except requests.RequestException as exc:
            raise RuntimeError(f"无法验证外置登录令牌：{exc}") from exc
        raise_for_external_auth(validate_response, "validate")
        logger.info("External account token validated without refresh: server=%s username=%s", server, account.get("username", ""))
        return dict(account)
    raise_for_external_auth(response, "refresh")
    try:
        data = response.json()
    except ValueError as exc:
        raise RuntimeError("外置登录刷新接口返回的不是有效 JSON。") from exc
    selected = data.get("selectedProfile") or {}
    refreshed = dict(account)
    refreshed["access_token"] = data.get("accessToken", access_token)
    refreshed["client_token"] = data.get("clientToken", client_token)
    if selected.get("id"):
        refreshed["uuid"] = selected.get("id")
    if selected.get("name"):
        refreshed["username"] = selected.get("name")
        refreshed["display_name"] = selected.get("name")
    logger.info("External account refreshed: server=%s username=%s uuid=%s", server, refreshed.get("username", ""), refreshed.get("uuid", ""))
    return refreshed


def authlib_injector_download_url():
    logger.info("Fetching authlib-injector metadata: url=%s", AUTHLIB_INJECTOR_METADATA_URL)
    response = requests.get(AUTHLIB_INJECTOR_METADATA_URL, headers={"User-Agent": "McGo/1.0"}, timeout=30)
    response.raise_for_status()
    data = response.json()
    version = data.get("version") or "latest"
    checksums = data.get("checksums") or {}
    url = (
        data.get("download_url")
        or data.get("downloadUrl")
        or data.get("url")
        or f"https://authlib-injector.yushi.moe/artifact/{version}/authlib-injector.jar"
    )
    filename = data.get("fileName") or data.get("filename") or f"authlib-injector-{version}.jar"
    logger.info("Authlib-injector metadata fetched: filename=%s url=%s has_sha256=%s", filename, url, bool(checksums.get("sha256", "")))
    return url, filename, checksums.get("sha256", "")


def authlib_injector_args(account):
    if account.get("type") != "external":
        return []
    injector_path = (account.get("authlib_injector_path") or "").strip()
    server = normalize_auth_server(account.get("auth_server", ""))
    if not injector_path:
        raise RuntimeError("外置登录账号缺少 authlib-injector jar 路径。")
    if not os.path.isfile(injector_path):
        raise RuntimeError(f"authlib-injector jar 不存在：{injector_path}")
    logger.debug("Authlib-injector args prepared: server=%s injector=%s", server, injector_path)
    return [
        f"-javaagent:{injector_path}={server}",
        f"-Dauthlibinjector.yggdrasil.prefetched={server}",
    ]


class InstallerEngine:
    def __init__(self, minecraft_version, mirror_source, game_dir, java_path, status_callback=None, progress_callback=None):
        self.minecraft_version = minecraft_version
        self.mirror_source = mirror_source
        self.game_dir = os.path.abspath(game_dir)
        self.java_path = java_path
        self.status_callback = status_callback
        self.progress_callback = progress_callback

    def emit_status(self, message):
        if self.status_callback:
            self.status_callback(message)

    def emit_snapshot(self, snapshot):
        if self.progress_callback:
            self.progress_callback(snapshot)

    def emit_progress_text(self, phase, current_file="", progress=0.0, completed_files=0, total_files=0):
        self.emit_snapshot({
            "progress": progress,
            "phase": phase,
            "current_file": current_file,
            "downloaded_bytes": completed_files,
            "total_bytes": total_files,
            "speed_bytes": 0,
            "completed_files": completed_files,
            "total_files": total_files,
            "reused_files": 0,
        })

    def runtime_directory_for(self, version_id):
        settings = load_version_settings()
        return runtime_directory_for_version(
            self.game_dir,
            settings,
            version_id,
            global_isolation=config.getboolean("GAME", "enable_resource_isolation", fallback=True),
        )

    def _resolve_base_minecraft_version(self):
        return resolve_base_minecraft_version(self.game_dir, self.minecraft_version)

    def _find_matching_fabric_versions(self, base_minecraft_version):
        return find_matching_fabric_versions(self.game_dir, base_minecraft_version)

    def ensure_launcher_profiles(self):
        launcher_profiles_path = os.path.join(self.game_dir, "launcher_profiles.json")
        if os.path.exists(launcher_profiles_path):
            return
        with open(launcher_profiles_path, "w", encoding="utf-8") as file_handle:
            json.dump({"profiles": {}, "settings": {}}, file_handle, ensure_ascii=False, indent=2)

    def _optifine_library_path(self, library_version):
        return os.path.join(
            self.game_dir,
            "libraries",
            "optifine",
            "OptiFine",
            library_version,
            f"OptiFine-{library_version}.jar",
        )

    def _ensure_launchwrapper_library(self, archive):
        launchwrapper_entries = [
            name for name in archive.namelist()
            if name.lower().startswith("launchwrapper-of-") and name.lower().endswith(".jar")
        ]
        if launchwrapper_entries:
            entry = launchwrapper_entries[0]
            version = entry.rsplit("-", 1)[-1][:-4]
            target_path = os.path.join(
                self.game_dir,
                "libraries",
                "optifine",
                "launchwrapper-of",
                version,
                f"launchwrapper-of-{version}.jar",
            )
            os.makedirs(os.path.dirname(target_path), exist_ok=True)
            with archive.open(entry) as src, open(target_path, "wb") as dst:
                shutil.copyfileobj(src, dst)
            return {
                "name": f"optifine:launchwrapper-of:{version}",
                "path": target_path,
            }

        fallback_name = "net.minecraft:launchwrapper:1.12"
        fallback_relative = library_relative_path(fallback_name)
        fallback_path = os.path.join(self.game_dir, "libraries", fallback_relative)
        if not os.path.exists(fallback_path):
            library_url = f"{mirror_root(self.mirror_source)}/maven/{fallback_relative.replace(os.sep, '/')}"
            self.emit_status("正在补充 LaunchWrapper 依赖")
            stream_download(library_url, fallback_path, self.emit_snapshot, "下载 LaunchWrapper")
        return {
            "name": fallback_name,
            "path": fallback_path,
        }

    def _write_optifine_version_json(self, version_id, base_json, optifine_library_name, launchwrapper_library_name):
        version_dir = os.path.join(self.game_dir, "versions", version_id)
        os.makedirs(version_dir, exist_ok=True)

        version_json = {
            "id": version_id,
            "inheritsFrom": self.minecraft_version,
            "mainClass": "net.minecraft.launchwrapper.Launch",
            "type": base_json.get("type", "release"),
            "libraries": [
                {"name": launchwrapper_library_name},
                {"name": optifine_library_name},
            ],
        }

        if base_json.get("releaseTime"):
            version_json["releaseTime"] = base_json["releaseTime"]
        if base_json.get("time"):
            version_json["time"] = base_json["time"]

        modern_game_args = base_json.get("arguments", {}).get("game")
        if modern_game_args:
            version_json["arguments"] = {
                "game": ["--tweakClass", "optifine.OptiFineTweaker"],
                "jvm": [],
            }
        else:
            legacy_arguments = (base_json.get("minecraftArguments") or "").strip()
            tweak_argument = "--tweakClass optifine.OptiFineTweaker"
            if tweak_argument not in legacy_arguments:
                legacy_arguments = f"{legacy_arguments} {tweak_argument}".strip()
            version_json["minecraftArguments"] = legacy_arguments

        version_json_path = os.path.join(version_dir, f"{version_id}.json")
        with open(version_json_path, "w", encoding="utf-8") as file_handle:
            json.dump(version_json, file_handle, ensure_ascii=False, indent=4)

    def _install_fabric(self):
        base_minecraft_version = self._resolve_base_minecraft_version()
        if base_minecraft_version != self.minecraft_version:
            raise RuntimeError("安装 Fabric 时请选择原版 Minecraft 版本，而不是已安装的 Fabric 版本。")

        versions = get_fabric_loader_versions(self.minecraft_version, self.mirror_source)
        if not versions:
            raise RuntimeError(f"未找到适用于 {self.minecraft_version} 的 Fabric Loader。")

        selected = versions[0]
        loader_version = selected["loader"]["version"]
        profile_url = (
            f"{mirror_root(self.mirror_source)}/fabric-meta/v2/versions/loader/"
            f"{self.minecraft_version}/{loader_version}/profile/json"
        )
        self.emit_status(f"正在获取 Fabric 安装配置：{self.minecraft_version} / Loader {loader_version}")
        response = requests.get(profile_url, timeout=30)
        response.raise_for_status()
        profile_json = response.json()

        version_id = profile_json.get("id") or f"fabric-loader-{loader_version}-{self.minecraft_version}"
        version_dir = os.path.join(self.game_dir, "versions", version_id)
        os.makedirs(version_dir, exist_ok=True)
        version_json_path = os.path.join(version_dir, f"{version_id}.json")
        with open(version_json_path, "w", encoding="utf-8") as file_handle:
            json.dump(profile_json, file_handle, ensure_ascii=False, indent=4)

        if not get_version_json(self.game_dir, self.minecraft_version):
            raise RuntimeError(f"请先下载原版 Minecraft {self.minecraft_version}。")

        download_profile_libraries(profile_json, self.game_dir, self.mirror_source, self.emit_snapshot)

        self.emit_snapshot({
            "progress": 1.0,
            "phase": "Fabric 安装完成",
            "current_file": version_id,
            "downloaded_bytes": 0,
            "total_bytes": 0,
            "speed_bytes": 0,
            "completed_files": 1,
            "total_files": 1,
            "reused_files": 0,
        })
        return {"installed_version": version_id, "message": f"Fabric {loader_version} 已安装"}

    def _install_fabric_api(self):
        base_minecraft_version = self._resolve_base_minecraft_version()
        fabric_versions = self._find_matching_fabric_versions(base_minecraft_version)
        if not fabric_versions:
            raise RuntimeError(f"请先为 Minecraft {base_minecraft_version} 安装 Fabric，再安装 Fabric API。")
        versions = get_fabric_api_versions(base_minecraft_version)
        if not versions:
            raise RuntimeError(f"未找到适用于 {base_minecraft_version} 的 Fabric API。")

        preferred = [item for item in versions if item.get("version_type") == "release"]
        selected = (preferred or versions)[0]
        files = selected.get("files", [])
        primary = next((item for item in files if item.get("primary")), None) or (files[0] if files else None)
        if not primary or not primary.get("url"):
            raise RuntimeError("Fabric API 元数据缺少可下载文件。")

        target_version = fabric_versions[0]
        mods_dir = os.path.join(self.runtime_directory_for(target_version), "mods")
        os.makedirs(mods_dir, exist_ok=True)
        filename = primary.get("filename") or f"fabric-api-{base_minecraft_version}.jar"
        target_path = os.path.join(mods_dir, filename)
        self.emit_status(f"正在安装 Fabric API：{selected.get('version_number', base_minecraft_version)}")
        stream_download(primary["url"], target_path, self.emit_snapshot, "下载 Fabric API")
        return {
            "installed_version": target_version,
            "message": f"Fabric API 已安装：{filename}",
        }

    def _install_forge_like(self, kind):
        if not self.java_path:
            raise RuntimeError(f"安装 {kind} 需要可用的 Java。")
        os.makedirs(self.game_dir, exist_ok=True)
        self.ensure_launcher_profiles()

        if kind == "forge":
            forge_version = get_forge_version_for_mc(self.minecraft_version, self.mirror_source)
            if not forge_version:
                raise RuntimeError(f"未找到适用于 {self.minecraft_version} 的 Forge 版本。")
            installer_url = (
                f"{mirror_root(self.mirror_source)}/maven/net/minecraftforge/forge/"
                f"{self.minecraft_version}-{forge_version}/forge-{self.minecraft_version}-{forge_version}-installer.jar"
            )
            label = f"Forge {forge_version}"
        else:
            versions = get_neoforge_list(self.minecraft_version, self.mirror_source)
            if not versions:
                raise RuntimeError(f"未找到适用于 {self.minecraft_version} 的 NeoForge 版本。")
            selected_entry = sorted(versions, key=lambda item: version_key(item.get("rawVersion", "")), reverse=True)[0]
            installer_path = selected_entry.get("installerPath")
            selected = selected_entry.get("version") or selected_entry.get("rawVersion")
            if installer_path:
                installer_url = f"{mirror_root(self.mirror_source)}{installer_path}"
            else:
                selected_value = selected_entry.get("version") or (selected_entry.get("rawVersion") or "").replace("neoforge-", "")
                installer_url = (
                    f"{mirror_root(self.mirror_source)}/maven/net/neoforged/neoforge/"
                    f"{selected_value}/neoforge-{selected_value}-installer.jar"
                )
            label = f"NeoForge {selected}"

        temp_dir = tempfile.mkdtemp(prefix=f"mcgo-{kind}-")
        try:
            installer_path = os.path.join(temp_dir, f"{kind}-installer.jar")
            self.emit_status(f"正在下载 {label} 安装器")
            stream_download(installer_url, installer_path, self.emit_snapshot, f"下载 {label} 安装器")
            self.emit_status(f"正在安装 {label}")
            before_versions = set(get_local_versions(self.game_dir))
            install_args = [self.java_path, "-jar", installer_path]
            if kind == "neoforge":
                install_args.extend(["--install-client", self.game_dir])
            else:
                install_args.extend(["--installClient", self.game_dir])
            process = subprocess.Popen(
                install_args,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                cwd=temp_dir,
                encoding="utf-8",
                errors="replace",
                **hidden_subprocess_kwargs(),
            )
            collected_output = []
            self.emit_progress_text(f"正在执行 {label} 安装器", os.path.basename(installer_path), progress=0.5, completed_files=1, total_files=2)
            start_time = time.monotonic()
            while True:
                line = process.stdout.readline() if process.stdout else ""
                if line:
                    cleaned = line.rstrip()
                    if cleaned:
                        collected_output.append(cleaned)
                        self.emit_status(cleaned)
                if process.poll() is not None:
                    break
                if time.monotonic() - start_time > 600:
                    process.kill()
                    raise RuntimeError(f"{label} 安装超时。")
            remaining = process.stdout.read() if process.stdout else ""
            if remaining:
                for line in remaining.splitlines():
                    cleaned = line.rstrip()
                    if cleaned:
                        collected_output.append(cleaned)
                        self.emit_status(cleaned)
            if process.returncode != 0:
                output = "\n".join(collected_output).strip()
                raise RuntimeError(output or f"{label} 安装失败，退出码 {process.returncode}")
            self.emit_progress_text(f"{label} 安装收尾中", "", progress=0.95, completed_files=2, total_files=2)
            after_versions = set(get_local_versions(self.game_dir))
            new_versions = sorted(after_versions - before_versions)
            installed_version = new_versions[-1] if new_versions else self.minecraft_version
            output = "\n".join(collected_output).strip()
            return {"installed_version": installed_version, "message": output or f"{label} 安装完成"}
        finally:
            shutil.rmtree(temp_dir, ignore_errors=True)

    def _install_optifine(self):
        base_json = get_version_json(self.game_dir, self.minecraft_version)
        if not base_json:
            raise RuntimeError(f"请先在当前游戏目录下载原版 Minecraft {self.minecraft_version}，再安装 OptiFine。")

        versions = get_optifine_list(self.mirror_source)
        candidates = [item for item in versions if str(item.get("mcversion")) == self.minecraft_version]
        selected = select_optifine_candidate(candidates)
        if not selected:
            raise RuntimeError(f"未找到适用于 {self.minecraft_version} 的 OptiFine。")

        patch = selected.get("patch") or selected.get("type")
        optifine_type = selected.get("type") or "HD_U"
        version_id = build_optifine_version_id(self.minecraft_version, selected)
        library_version = build_optifine_library_version(selected)
        optifine_library_name = f"optifine:OptiFine:{library_version}"
        installer_url = f"{mirror_root(self.mirror_source)}/optifine/{self.minecraft_version}/{optifine_type}/{patch}"

        temp_dir = tempfile.mkdtemp(prefix="mcgo-optifine-")
        try:
            installer_path = os.path.join(temp_dir, "optifine-installer.jar")
            self.emit_status(f"正在下载 OptiFine {self.minecraft_version} {optifine_type} {patch}")
            stream_download(installer_url, installer_path, self.emit_snapshot, "下载 OptiFine 安装包")

            optifine_library_path = self._optifine_library_path(library_version)
            os.makedirs(os.path.dirname(optifine_library_path), exist_ok=True)
            shutil.copy2(installer_path, optifine_library_path)

            with zipfile.ZipFile(installer_path) as archive:
                launchwrapper = self._ensure_launchwrapper_library(archive)

            self.emit_status(f"正在生成 OptiFine 版本 {version_id}")
            self._write_optifine_version_json(
                version_id,
                base_json,
                optifine_library_name,
                launchwrapper["name"],
            )
            self.emit_snapshot({
                "progress": 1.0,
                "phase": "OptiFine 安装完成",
                "current_file": version_id,
                "downloaded_bytes": 0,
                "total_bytes": 0,
                "speed_bytes": 0,
                "completed_files": 1,
                "total_files": 1,
                "reused_files": 0,
            })
            return {
                "installed_version": version_id,
                "message": f"OptiFine 已安装：{version_id}",
            }
        finally:
            shutil.rmtree(temp_dir, ignore_errors=True)

    def install(self, install_type):
        if install_type == "fabric":
            return self._install_fabric()
        if install_type == "fabric_api":
            return self._install_fabric_api()
        if install_type == "forge":
            return self._install_forge_like("forge")
        if install_type == "neoforge":
            return self._install_forge_like("neoforge")
        if install_type == "optifine":
            return self._install_optifine()
        raise RuntimeError(f"不支持的安装类型：{install_type}")

    def install_sequence(self, install_types):
        messages = []
        installed_version = self.minecraft_version
        for install_type in install_types:
            self.emit_status(f"正在安装 {INSTALL_TYPE_LABELS.get(install_type, install_type)}")
            payload = self.install(install_type)
            messages.append(payload.get("message", INSTALL_TYPE_LABELS.get(install_type, install_type)))
            if payload.get("installed_version") and install_type != "fabric_api":
                installed_version = payload["installed_version"]
        return {
            "installed_version": installed_version,
            "message": "；".join(messages),
            "steps": list(install_types),
            "install_type": install_types[-1] if install_types else "",
        }


def run_flask_app():
    app.run(port=5000, debug=False, use_reloader=False)


def read_download_options():
    return {
        "max_core_concurrency": max(1, config.getint("DOWNLOAD", "max_core_threads", fallback=12)),
        "max_asset_concurrency": max(1, config.getint("DOWNLOAD", "max_asset_threads", fallback=24)),
        "speed_limit_kbps": max(0, config.getint("DOWNLOAD", "speed_limit_kbps", fallback=0)),
        "cache_strategy": config.get("DOWNLOAD", "cache_strategy", fallback="reuse"),
    }


class DownloadWorker(QObject):
    progress = Signal(int)
    metrics = Signal(dict)
    status = Signal(str)
    install_metrics = Signal(dict)
    install_status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, version_id, mirror_source, game_dir, auto_install_types=None, java_path="", download_options=None):
        super().__init__()
        self.version_id = version_id
        self.mirror_source = mirror_source
        self.game_dir = os.path.abspath(game_dir)
        self.auto_install_types = list(auto_install_types or [])
        self.java_path = java_path
        self.download_options = dict(download_options or {})

    def run(self):
        try:
            logger.info(
                "DownloadWorker started: version=%s mirror=%s game_dir=%s auto_install=%s java=%s",
                self.version_id,
                self.mirror_source,
                self.game_dir,
                self.auto_install_types,
                self.java_path,
            )
            self.status.emit(f"正在获取 {self.version_id} 版本信息...")
            version_json, resolved_source = get_version_metadata_with_fallback(
                self.version_id,
                self.mirror_source,
                status_callback=self.status.emit,
            )
            resolved_mirror_root = MIRROR_SOURCES[resolved_source]
            logger.debug("Version metadata loaded: version=%s keys=%s", self.version_id, sorted(version_json.keys()))

            def on_progress(snapshot):
                value = snapshot.get("progress", 0.0)
                self.progress.emit(max(0, min(100, int(value * 100))))
                self.metrics.emit(snapshot)

            self.status.emit(f"正在下载 Minecraft {self.version_id}...")
            asyncio.run(download_game_files(
                version_json,
                self.game_dir,
                self.version_id,
                resolved_mirror_root,
                progress_callback=on_progress,
                **self.download_options,
            ))
            self.status.emit("正在解压 natives...")
            extract_natives(version_json, self.game_dir, self.version_id)
            payload = {"version": self.version_id}
            if self.auto_install_types:
                self.install_status.emit("原版下载完成，开始安装附加组件...")
                def on_install_progress(snapshot):
                    value = snapshot.get("progress", 0.0)
                    self.progress.emit(max(0, min(100, int(value * 100))))
                    self.install_metrics.emit(snapshot)

                engine = InstallerEngine(
                    self.version_id,
                    self.mirror_source,
                    self.game_dir,
                    self.java_path,
                    status_callback=self.install_status.emit,
                    progress_callback=on_install_progress,
                )
                install_payload = engine.install_sequence(self.auto_install_types)
                payload["post_install"] = install_payload
            self.progress.emit(100)
            logger.info("DownloadWorker finished: payload=%s", payload)
            self.finished.emit(payload)
        except Exception as e:
            logger.exception("DownloadWorker failed: version=%s", self.version_id)
            self.failed.emit(str(e))


class InstallWorker(QObject):
    progress = Signal(int)
    metrics = Signal(dict)
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, install_type, minecraft_version, mirror_source, game_dir, java_path):
        super().__init__()
        self.install_type = install_type
        self.minecraft_version = minecraft_version
        self.mirror_source = mirror_source
        self.game_dir = game_dir
        self.java_path = java_path

    def emit_snapshot(self, snapshot):
        value = snapshot.get("progress", 0.0)
        self.progress.emit(max(0, min(100, int(value * 100))))
        self.metrics.emit(snapshot)

    def run(self):
        try:
            logger.info(
                "InstallWorker started: install_type=%s minecraft_version=%s mirror=%s game_dir=%s java=%s",
                self.install_type,
                self.minecraft_version,
                self.mirror_source,
                self.game_dir,
                self.java_path,
            )
            engine = InstallerEngine(
                self.minecraft_version,
                self.mirror_source,
                self.game_dir,
                self.java_path,
                status_callback=self.status.emit,
                progress_callback=self.emit_snapshot,
            )
            payload = engine.install(self.install_type)
            payload["install_type"] = self.install_type
            self.progress.emit(100)
            logger.info("InstallWorker finished: payload=%s", payload)
            self.finished.emit(payload)
        except Exception as exc:
            logger.exception("InstallWorker failed: install_type=%s minecraft_version=%s", self.install_type, self.minecraft_version)
            self.failed.emit(str(exc))


class RepairWorker(QObject):
    progress = Signal(int)
    metrics = Signal(dict)
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, version_id, mirror_source, game_dir, download_options=None):
        super().__init__()
        self.version_id = version_id
        self.mirror_source = mirror_source
        self.game_dir = os.path.abspath(game_dir)
        self.download_options = dict(download_options or {})

    def run(self):
        try:
            logger.info(
                "RepairWorker started: version=%s mirror=%s game_dir=%s",
                self.version_id,
                self.mirror_source,
                self.game_dir,
            )
            chain = get_version_inheritance_chain(self.game_dir, self.version_id)
            if not chain:
                raise RuntimeError(f"未找到版本清单：{self.version_id}")

            def on_progress(snapshot):
                value = snapshot.get("progress", 0.0)
                self.progress.emit(max(0, min(100, int(value * 100))))
                self.metrics.emit(snapshot)

            total_missing_before = 0
            total_missing_after = 0
            report_path = ""
            for index, version_json in enumerate(reversed(chain), start=1):
                version_id = version_json.get("id", self.version_id)
                self.status.emit(f"正在校验并补全 {version_id}（{index}/{len(chain)}）...")
                missing_before = collect_missing_game_files(version_json, self.game_dir, version_id)
                logger.info("Repair scan before: version=%s missing=%d", version_id, len(missing_before))
                total_missing_before += len(missing_before)
                asyncio.run(repair_game_files(
                    version_json,
                    self.game_dir,
                    version_id,
                    MIRROR_SOURCES[self.mirror_source],
                    progress_callback=on_progress,
                    **self.download_options,
                ))
                missing_after = collect_missing_game_files(version_json, self.game_dir, version_id)
                logger.info("Repair scan after: version=%s missing=%d", version_id, len(missing_after))
                total_missing_after += len(missing_after)
                if missing_after:
                    report_path = os.path.join(self.game_dir, "versions", self.version_id, "repair-missing-files.json")
                    os.makedirs(os.path.dirname(report_path), exist_ok=True)
                    with open(report_path, "w", encoding="utf-8") as file_handle:
                        json.dump(missing_after, file_handle, ensure_ascii=False, indent=2)
            self.progress.emit(100)
            logger.info(
                "RepairWorker finished: version=%s checked=%d missing_before=%d missing_after=%d report=%s",
                self.version_id,
                len(chain),
                total_missing_before,
                total_missing_after,
                report_path,
            )
            self.finished.emit({
                "version": self.version_id,
                "checked_versions": len(chain),
                "missing_before": total_missing_before,
                "missing_after": total_missing_after,
                "report_path": report_path,
            })
        except Exception as exc:
            logger.exception("RepairWorker failed: version=%s", self.version_id)
            self.failed.emit(str(exc))


class ModpackImportWorker(QObject):
    progress = Signal(int)
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, pack_path, game_dir, mirror_source="official", java_path=""):
        super().__init__()
        self.pack_path = pack_path
        self.game_dir = os.path.abspath(game_dir)
        self.mirror_source = mirror_source
        self.java_path = java_path
        self.missing_files = []

    def _safe_join(self, root, relative_path):
        normalized = os.path.normpath(relative_path).replace("\\", os.sep).lstrip(os.sep)
        target = os.path.abspath(os.path.join(root, normalized))
        root_abs = os.path.abspath(root)
        if not target.startswith(root_abs + os.sep) and target != root_abs:
            raise RuntimeError(f"整合包包含不安全路径：{relative_path}")
        return target

    def _extract_prefix(self, archive, prefix, target_dir):
        prefix = prefix.strip("/")
        if prefix:
            prefix = prefix + "/"
        copied = 0
        for item in archive.infolist():
            name = item.filename.replace("\\", "/")
            if item.is_dir() or (prefix and not name.startswith(prefix)):
                continue
            relative = name[len(prefix):] if prefix else name
            if not relative:
                continue
            target = self._safe_join(target_dir, relative)
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with archive.open(item) as src, open(target, "wb") as dst:
                shutil.copyfileobj(src, dst)
            copied += 1
        return copied

    def _read_json(self, archive, name):
        with archive.open(name) as handle:
            return json.loads(handle.read().decode("utf-8-sig"))

    def _find_entry(self, archive, candidates):
        names = {item.filename.replace("\\", "/"): item.filename for item in archive.infolist()}
        for candidate in candidates:
            if candidate in names:
                return names[candidate]
        for name in names:
            for candidate in candidates:
                if name.endswith("/" + candidate):
                    return names[name]
        return ""

    def _download_file(self, url, target_path):
        os.makedirs(os.path.dirname(target_path), exist_ok=True)
        with requests.get(url, stream=True, timeout=60) as response:
            response.raise_for_status()
            with open(target_path, "wb") as handle:
                for chunk in response.iter_content(chunk_size=128 * 1024):
                    if chunk:
                        handle.write(chunk)

    def _ensure_minecraft_version(self, minecraft_version):
        if not minecraft_version or get_version_json(self.game_dir, minecraft_version):
            return
        self.status.emit(f"正在下载整合包基础版本 {minecraft_version}...")
        version_json, resolved_source = get_version_metadata_with_fallback(
            minecraft_version,
            self.mirror_source,
            status_callback=self.status.emit,
        )
        asyncio.run(download_game_files(
            version_json,
            self.game_dir,
            minecraft_version,
            MIRROR_SOURCES[resolved_source],
            progress_callback=None,
            **read_download_options(),
        ))
        extract_natives(version_json, self.game_dir, minecraft_version)

    def _install_declared_loader(self, minecraft_version, loader_key):
        if not minecraft_version or not loader_key:
            return minecraft_version

        normalized_loader = str(loader_key).lower().strip()
        if normalized_loader.startswith("fabric"):
            normalized_loader = "fabric"
        elif normalized_loader.startswith("forge"):
            normalized_loader = "forge"
        elif normalized_loader.startswith("neoforge"):
            normalized_loader = "neoforge"

        install_type = {
            "fabric-loader": "fabric",
            "fabric": "fabric",
            "forge": "forge",
            "neoforge": "neoforge",
        }.get(normalized_loader)
        if not install_type:
            self.missing_files.append({
                "path": "loader",
                "reason": f"暂不支持自动安装加载器：{loader_key}",
            })
            return minecraft_version
        if install_type in {"forge", "neoforge"} and not self.java_path:
            raise RuntimeError(f"整合包声明需要 {INSTALL_TYPE_LABELS[install_type]}，请先在环境中选择 Java。")

        self.status.emit(f"正在安装整合包加载器 {INSTALL_TYPE_LABELS.get(install_type, install_type)}...")
        engine = InstallerEngine(
            minecraft_version,
            self.mirror_source,
            self.game_dir,
            self.java_path,
            status_callback=self.status.emit,
            progress_callback=None,
        )
        payload = engine.install(install_type)
        return payload.get("installed_version") or minecraft_version

    def _write_imported_version(self, version_id, minecraft_version, installed_base_version):
        version_dir = os.path.join(self.game_dir, "versions", version_id)
        os.makedirs(version_dir, exist_ok=True)
        source_json = get_version_json(self.game_dir, installed_base_version or minecraft_version)
        if not source_json:
            return

        version_json = dict(source_json)
        version_json["id"] = version_id
        if installed_base_version and installed_base_version != version_id:
            version_json = {
                "id": version_id,
                "inheritsFrom": installed_base_version,
                "type": source_json.get("type", "release"),
                "time": source_json.get("time", ""),
                "releaseTime": source_json.get("releaseTime", ""),
            }
        with open(os.path.join(version_dir, f"{version_id}.json"), "w", encoding="utf-8") as file_handle:
            json.dump(version_json, file_handle, ensure_ascii=False, indent=4)

    def _write_missing_report(self, version_id):
        if not self.missing_files:
            return ""
        report_dir = os.path.join(self.game_dir, "versions", version_id)
        os.makedirs(report_dir, exist_ok=True)
        report_path = os.path.join(report_dir, "missing-modpack-files.json")
        with open(report_path, "w", encoding="utf-8") as file_handle:
            json.dump(self.missing_files, file_handle, ensure_ascii=False, indent=2)
        return report_path

    def _copy_override_file(self, archive, item, runtime_dir, prefix):
        name = item.filename.replace("\\", "/")
        relative = name[len(prefix):] if prefix else name
        if not relative:
            return False
        target = self._safe_join(runtime_dir, relative)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with archive.open(item) as src, open(target, "wb") as dst:
            shutil.copyfileobj(src, dst)
        return True

    def _extract_overrides(self, archive, runtime_dir, prefixes):
        copied = 0
        normalized_prefixes = []
        for prefix in prefixes:
            prefix = prefix.strip("/")
            normalized_prefixes.append(f"{prefix}/" if prefix else "")
        for item in archive.infolist():
            name = item.filename.replace("\\", "/")
            if item.is_dir():
                continue
            for prefix in normalized_prefixes:
                if prefix and not name.startswith(prefix):
                    continue
                if self._copy_override_file(archive, item, runtime_dir, prefix):
                    copied += 1
                break
        return copied

    def _curseforge_file_url(self, project_id, file_id):
        return f"https://edge.forgecdn.net/files/{str(file_id)[:-3]}/{str(file_id)[-3:]}/{file_id}"

    def _download_curseforge_files(self, files, runtime_dir):
        downloaded = 0
        optional = 0
        for idx, item in enumerate(files, start=1):
            project_id = item.get("projectID")
            file_id = item.get("fileID")
            required = item.get("required", True)
            if not project_id or not file_id:
                continue
            if not required:
                optional += 1
                self.missing_files.append({
                    "projectID": project_id,
                    "fileID": file_id,
                    "reason": "可选文件，未自动下载",
                })
                continue
            self.status.emit(f"正在下载 CurseForge 文件 {idx}/{len(files)}：{project_id}/{file_id}")
            self.progress.emit(min(95, int(idx / max(len(files), 1) * 75) + 15))
            try:
                meta_response = requests.get(
                    f"https://api.cfwidget.com/minecraft/mc-mods/{project_id}",
                    timeout=20,
                )
                filename = f"{project_id}-{file_id}.jar"
                if meta_response.ok:
                    data = meta_response.json()
                    for candidate in data.get("files", []):
                        if str(candidate.get("id")) == str(file_id):
                            filename = candidate.get("name") or candidate.get("displayName") or filename
                            if not filename.lower().endswith(".jar"):
                                filename = f"{filename}.jar"
                            break
                target_path = os.path.join(runtime_dir, "mods", sanitize_filename(filename, f"{project_id}-{file_id}.jar"))
                self._download_file(self._curseforge_file_url(project_id, file_id), target_path)
                downloaded += 1
            except Exception as exc:
                self.missing_files.append({
                    "projectID": project_id,
                    "fileID": file_id,
                    "reason": str(exc),
                    "manual_url": f"https://www.curseforge.com/minecraft/mc-mods/{project_id}/files/{file_id}",
                })
        return downloaded, optional

    def _discover_import_metadata(self, payload):
        minecraft_version = ""
        loader_key = ""
        pack_name = ""

        if not isinstance(payload, dict):
            return minecraft_version, loader_key, pack_name

        pack_name = (
            payload.get("name")
            or payload.get("displayName")
            or payload.get("instanceName")
            or payload.get("title")
            or ""
        )
        minecraft_version = (
            payload.get("minecraft")
            or payload.get("minecraftVersion")
            or payload.get("gameVersion")
            or payload.get("mcversion")
            or ""
        )
        loader_key = payload.get("loader") or payload.get("modLoader") or payload.get("loaderType") or ""

        components = payload.get("components") or payload.get("addons") or payload.get("loaders") or []
        if isinstance(components, dict):
            components = [{"uid": key, "version": value} for key, value in components.items()]
        for component in components if isinstance(components, list) else []:
            if not isinstance(component, dict):
                continue
            uid = str(component.get("uid") or component.get("id") or component.get("name") or "").lower()
            version = str(component.get("version") or component.get("versionNumber") or "")
            if not minecraft_version and ("minecraft" == uid or uid.endswith("minecraft")):
                minecraft_version = version
            if not loader_key:
                if "fabric" in uid:
                    loader_key = "fabric"
                elif "neoforge" in uid:
                    loader_key = "neoforge"
                elif "forge" in uid:
                    loader_key = "forge"
                elif "quilt" in uid:
                    loader_key = "quilt"

        launch = payload.get("launch") or payload.get("version") or {}
        if isinstance(launch, dict):
            minecraft_version = minecraft_version or launch.get("minecraft") or launch.get("minecraftVersion") or ""
            loader_key = loader_key or launch.get("loader") or launch.get("modLoader") or ""

        return str(minecraft_version), str(loader_key).lower(), str(pack_name)

    def _read_text(self, archive, name):
        with archive.open(name) as handle:
            return handle.read().decode("utf-8-sig", errors="replace")

    def _parse_instance_cfg(self, text):
        payload = {"components": []}
        for raw_line in text.splitlines():
            line = raw_line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.split("=", 1)
            key = key.strip()
            value = value.strip()
            lowered = key.lower()
            if lowered in {"name", "instancename"}:
                payload["name"] = value
            elif "minecraft" in lowered and "version" in lowered:
                payload["minecraftVersion"] = value
            elif "fabric" in lowered:
                payload["components"].append({"uid": "fabric", "version": value})
            elif "neoforge" in lowered:
                payload["components"].append({"uid": "neoforge", "version": value})
            elif "forge" in lowered:
                payload["components"].append({"uid": "forge", "version": value})
            elif "quilt" in lowered:
                payload["components"].append({"uid": "quilt", "version": value})
        return payload

    def _install_generic_manifest_pack(self, archive, manifest_entry, pack_kind):
        if manifest_entry.lower().endswith(".json") or manifest_entry.lower().endswith(".packmeta"):
            manifest = self._read_json(archive, manifest_entry)
        else:
            manifest = self._parse_instance_cfg(self._read_text(archive, manifest_entry))
        minecraft_version, loader_key, manifest_name = self._discover_import_metadata(manifest)
        pack_name = (manifest_name or os.path.splitext(os.path.basename(self.pack_path))[0]).strip()
        version_id = sanitize_filename(pack_name, f"{pack_kind}-{minecraft_version or 'Imported'}")
        runtime_dir = os.path.join(self.game_dir, "versions", version_id)
        os.makedirs(runtime_dir, exist_ok=True)

        if minecraft_version:
            self.status.emit("正在准备基础版本和加载器...")
            self._ensure_minecraft_version(minecraft_version)
            installed_base_version = self._install_declared_loader(minecraft_version, loader_key)
            self._write_imported_version(version_id, minecraft_version, installed_base_version)
        else:
            self.missing_files.append({
                "path": manifest_entry,
                "reason": "未识别到 Minecraft 版本，已按普通覆写包导入。",
            })

        base = manifest_entry.replace("\\", "/").rsplit("/", 1)[0]
        base_prefix = f"{base}/" if base else ""
        prefixes = [
            base_prefix + "overrides",
            base_prefix + "minecraft",
            base_prefix + ".minecraft",
            base_prefix + "instance",
            base_prefix + "mmc-pack",
        ]
        copied = self._extract_overrides(archive, runtime_dir, prefixes)
        if copied == 0:
            copied = self._extract_prefix(archive, "", runtime_dir)

        missing_report = self._write_missing_report(version_id)
        return {
            "version": version_id,
            "alias": pack_name,
            "minecraft": minecraft_version,
            "loader": loader_key,
            "message": f"{pack_kind} 整合包已导入：覆写 {copied} 个文件，缺失 {len(self.missing_files)} 项。",
            "missing_report": missing_report,
        }

    def _install_modrinth(self, archive, index_entry):
        index = self._read_json(archive, index_entry)
        dependencies = index.get("dependencies", {})
        minecraft_version = dependencies.get("minecraft", "")
        pack_name = (index.get("name") or os.path.splitext(os.path.basename(self.pack_path))[0]).strip()
        version_id = sanitize_filename(pack_name, f"Modrinth-{minecraft_version}")
        runtime_dir = os.path.join(self.game_dir, "versions", version_id)
        os.makedirs(runtime_dir, exist_ok=True)

        self.status.emit("正在准备基础版本和加载器...")
        loader_key = next((key for key in ("fabric-loader", "forge", "neoforge", "quilt-loader") if dependencies.get(key)), "")
        self._ensure_minecraft_version(minecraft_version)
        installed_base_version = self._install_declared_loader(minecraft_version, loader_key)
        self._write_imported_version(version_id, minecraft_version, installed_base_version)

        self.status.emit("正在解压 overrides...")
        base = index_entry.replace("\\", "/").rsplit("/", 1)[0]
        base_prefix = f"{base}/" if base else ""
        copied = self._extract_overrides(archive, runtime_dir, [base_prefix + "overrides", base_prefix + "client-overrides"])

        files = index.get("files", [])
        for idx, item in enumerate(files, start=1):
            path = item.get("path", "")
            downloads = item.get("downloads", [])
            if not path or not downloads:
                continue
            self.status.emit(f"正在下载整合包文件 {idx}/{len(files)}：{os.path.basename(path)}")
            self.progress.emit(min(95, int(idx / max(len(files), 1) * 90)))
            try:
                self._download_file(downloads[0], self._safe_join(runtime_dir, path))
            except Exception as exc:
                self.missing_files.append({"path": path, "downloads": downloads, "reason": str(exc)})

        missing_report = self._write_missing_report(version_id)

        return {
            "version": version_id,
            "alias": pack_name,
            "minecraft": minecraft_version,
            "loader": loader_key,
            "message": f"Modrinth 整合包已导入：{pack_name}，覆写 {copied} 个文件，缺失 {len(self.missing_files)} 项",
            "missing_report": missing_report,
        }

    def _install_curseforge(self, archive, manifest_entry):
        manifest = self._read_json(archive, manifest_entry)
        minecraft_version = manifest.get("minecraft", {}).get("version", "")
        pack_name = (manifest.get("name") or os.path.splitext(os.path.basename(self.pack_path))[0]).strip()
        version_id = sanitize_filename(pack_name, f"CurseForge-{minecraft_version}")
        runtime_dir = os.path.join(self.game_dir, "versions", version_id)
        os.makedirs(runtime_dir, exist_ok=True)

        mod_loaders = manifest.get("minecraft", {}).get("modLoaders", [])
        primary_loader = next((item.get("id", "") for item in mod_loaders if item.get("primary")), "")
        if not primary_loader and mod_loaders:
            primary_loader = mod_loaders[0].get("id", "")
        loader_key = primary_loader.split("-", 1)[0] if primary_loader else ""

        self.status.emit("正在准备基础版本和加载器...")
        self._ensure_minecraft_version(minecraft_version)
        installed_base_version = self._install_declared_loader(minecraft_version, loader_key)
        self._write_imported_version(version_id, minecraft_version, installed_base_version)

        override_dir = (manifest.get("overrides") or "overrides").strip("/\\")
        base = manifest_entry.replace("\\", "/").rsplit("/", 1)[0]
        base_prefix = f"{base}/" if base else ""
        copied = self._extract_prefix(archive, base_prefix + override_dir, runtime_dir)
        files = manifest.get("files", [])
        downloaded, optional = self._download_curseforge_files(files, runtime_dir)
        missing_report = self._write_missing_report(version_id)
        return {
            "version": version_id,
            "alias": pack_name,
            "minecraft": minecraft_version,
            "loader": loader_key,
            "message": f"CurseForge 整合包已导入：覆写 {copied} 个，下载 {downloaded} 个，可选 {optional} 个，缺失 {len(self.missing_files)} 项。",
            "missing_report": missing_report,
        }

    def _install_plain_zip(self, archive):
        pack_name = os.path.splitext(os.path.basename(self.pack_path))[0].strip()
        version_id = sanitize_filename(pack_name, "ImportedPack")
        runtime_dir = os.path.join(self.game_dir, "versions", version_id)
        os.makedirs(runtime_dir, exist_ok=True)
        copied = self._extract_prefix(archive, "", runtime_dir)
        self._write_missing_report(version_id)
        return {
            "version": version_id,
            "alias": pack_name,
            "minecraft": "",
            "loader": "",
            "message": f"压缩包已导入：{copied} 个文件",
        }

    def run(self):
        try:
            if not zipfile.is_zipfile(self.pack_path):
                raise RuntimeError("当前仅支持 zip/mrpack 格式整合包。")
            self.progress.emit(3)
            self.status.emit("正在识别整合包格式...")
            with zipfile.ZipFile(self.pack_path, "r") as archive:
                modrinth_entry = self._find_entry(archive, ["modrinth.index.json"])
                manifest_entry = self._find_entry(archive, ["manifest.json"])
                mmc_entry = self._find_entry(archive, ["mmc-pack.json", "instance.cfg"])
                hmcl_entry = self._find_entry(archive, ["modpack.json", "hmcl.json"])
                mcbbs_entry = self._find_entry(archive, ["mcbbs.packmeta", "mcbbs-pack.json", "pack.json"])
                if modrinth_entry:
                    payload = self._install_modrinth(archive, modrinth_entry)
                elif manifest_entry:
                    payload = self._install_curseforge(archive, manifest_entry)
                elif mmc_entry:
                    payload = self._install_generic_manifest_pack(archive, mmc_entry, "MMC")
                elif hmcl_entry:
                    payload = self._install_generic_manifest_pack(archive, hmcl_entry, "HMCL")
                elif mcbbs_entry:
                    payload = self._install_generic_manifest_pack(archive, mcbbs_entry, "MCBBS")
                else:
                    payload = self._install_plain_zip(archive)
            self.progress.emit(100)
            self.finished.emit(payload)
        except Exception as exc:
            self.failed.emit(str(exc))


class ResourceSearchWorker(QObject):
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, query, resource_type, game_version, loader, source="modrinth", sort_index="relevance", target_dir=""):
        super().__init__()
        self.query = query
        self.resource_type = resource_type
        self.game_version = game_version
        self.loader = loader
        self.source = source
        self.sort_index = sort_index
        self.target_dir = target_dir

    def run(self):
        try:
            logger.info(
                "ResourceSearchWorker started: source=%s query=%s type=%s game=%s loader=%s sort=%s target=%s",
                self.source,
                self.query,
                self.resource_type,
                self.game_version or "<any>",
                self.loader or "<any>",
                self.sort_index,
                self.target_dir,
            )
            self.status.emit("正在搜索资源市场...")
            if self.source == "modrinth":
                hits = search_modrinth_resources(
                    self.query,
                    self.resource_type,
                    self.game_version,
                    self.loader,
                    index=self.sort_index,
                )
            elif self.source == "curseforge":
                hits = search_curseforge_resources(self.query, self.resource_type, self.game_version, self.loader)
            elif self.source == "local":
                hits = list_local_resources(self.query, self.resource_type, self.target_dir)
            else:
                raise RuntimeError(f"不支持的资源来源：{self.source}")
            for hit in hits:
                hit["target_game_version"] = self.game_version
                hit["target_loader"] = self.loader
                hit["compatible"] = True
                if self.source == "modrinth":
                    search_hint_compatible = hit_has_modrinth_compatibility(
                        hit,
                        self.resource_type,
                        self.game_version,
                        self.loader,
                    )
                    hit["compatibility_unverified"] = True
                    hit["compatibility_checking"] = True
                    if not search_hint_compatible:
                        hit["compatibility_hint"] = "搜索结果未列出目标版本或加载器，安装时会重新确认"
            compatible_count = sum(1 for hit in hits if hit.get("compatible", True))
            unverified_count = sum(1 for hit in hits if hit.get("compatibility_unverified"))
            logger.info(
                "ResourceSearchWorker finished: source=%s query=%s hits=%d compatible=%d unverified=%d",
                self.source,
                self.query,
                len(hits),
                compatible_count,
                unverified_count,
            )
            self.finished.emit({
                "resource_type": self.resource_type,
                "query": self.query,
                "source": self.source,
                "hits": hits,
            })
        except Exception as exc:
            logger.exception("ResourceSearchWorker failed: source=%s query=%s", self.source, self.query)
            self.failed.emit(str(exc))


class ResourceCompatibilityWorker(QObject):
    checked = Signal(dict)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, hits, resource_type, game_version, loader, generation=0):
        super().__init__()
        self.hits = [dict(hit) for hit in hits]
        self.resource_type = resource_type
        self.game_version = game_version
        self.loader = loader
        self.generation = generation

    def run(self):
        checked_count = 0
        compatible_count = 0
        try:
            logger.info(
                "ResourceCompatibilityWorker started: hits=%d type=%s game=%s loader=%s",
                len(self.hits),
                self.resource_type,
                self.game_version or "<any>",
                self.loader or "<any>",
            )
            for index, hit in enumerate(self.hits):
                thread = QThread.currentThread()
                if thread.isInterruptionRequested():
                    logger.info("ResourceCompatibilityWorker interrupted: checked=%d", checked_count)
                    break
                project_id = hit.get("project_id") or hit.get("slug")
                if hit.get("source") != "modrinth" or not project_id:
                    continue
                updated = dict(hit)
                updated["compatibility_index"] = index
                try:
                    compatible_version = find_compatible_modrinth_version(
                        project_id,
                        self.resource_type,
                        self.game_version,
                        self.loader,
                    )
                except Exception as exc:
                    logger.warning(
                        "Failed to verify Modrinth compatibility: project=%s game=%s loader=%s error=%s",
                        project_id,
                        self.game_version,
                        self.loader,
                        exc,
                    )
                    compatible_version = None
                    updated["compatibility_error"] = str(exc)
                checked_count += 1
                updated["compatibility_generation"] = self.generation
                updated["compatibility_checking"] = False
                updated["compatibility_unverified"] = False
                updated["compatible"] = bool(compatible_version)
                if compatible_version:
                    compatible_count += 1
                    updated["compatible_version"] = compatible_version.get("version_number", "")
                    updated.pop("compatibility_error", None)
                else:
                    updated["compatible_version"] = ""
                self.checked.emit(updated)
            logger.info(
                "ResourceCompatibilityWorker finished: checked=%d compatible=%d",
                checked_count,
                compatible_count,
            )
            self.finished.emit({"checked": checked_count, "compatible": compatible_count, "generation": self.generation})
        except Exception as exc:
            logger.exception("ResourceCompatibilityWorker failed")
            self.failed.emit(str(exc))


class ResourceDetailWorker(QObject):
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, hit, resource_type, game_version, loader):
        super().__init__()
        self.hit = dict(hit)
        self.resource_type = resource_type
        self.game_version = game_version
        self.loader = loader

    def run(self):
        try:
            source = self.hit.get("source", "modrinth")
            logger.info(
                "ResourceDetailWorker started: source=%s project=%s type=%s game=%s loader=%s",
                source,
                self.hit.get("project_id") or self.hit.get("slug") or self.hit.get("path"),
                self.resource_type,
                self.game_version or "<any>",
                self.loader or "<any>",
            )
            if source == "modrinth":
                detail = get_modrinth_resource_detail(
                    self.hit.get("project_id") or self.hit.get("slug"),
                    self.resource_type,
                    self.game_version,
                    self.loader,
                )
            elif source == "curseforge":
                detail = get_curseforge_resource_detail(self.hit.get("project_id"))
            elif source == "local":
                detail = get_local_resource_detail(self.hit.get("path") or self.hit.get("project_id"))
            else:
                raise RuntimeError(f"不支持的资源来源：{source}")
            detail["hit"] = self.hit
            cached_screenshots = []
            for screenshot in detail.get("screenshots", [])[:5]:
                try:
                    cached = cache_resource_screenshot(screenshot)
                except Exception:
                    cached = ""
                cached_screenshots.append({
                    "url": screenshot,
                    "path": cached,
                })
            detail["screenshots"] = cached_screenshots
            logger.info(
                "ResourceDetailWorker finished: source=%s title=%s versions=%d screenshots=%d",
                source,
                detail.get("title", ""),
                len(detail.get("versions", [])),
                len(detail.get("screenshots", [])),
            )
            self.finished.emit(detail)
        except Exception as exc:
            logger.exception(
                "ResourceDetailWorker failed: source=%s project=%s",
                self.hit.get("source", "modrinth"),
                self.hit.get("project_id") or self.hit.get("slug") or self.hit.get("path"),
            )
            self.failed.emit(str(exc))


class ResourceInstallWorker(QObject):
    progress = Signal(int)
    metrics = Signal(dict)
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, project_id, title, resource_type, game_version, loader, target_dir, source="modrinth", install_dependencies=False):
        super().__init__()
        self.project_id = project_id
        self.title = title
        self.resource_type = resource_type
        self.game_version = game_version
        self.loader = loader
        self.target_dir = target_dir
        self.source = source
        self.install_dependencies = install_dependencies

    def run(self):
        try:
            logger.info(
                "ResourceInstallWorker started: source=%s project=%s title=%s type=%s game=%s loader=%s target=%s dependencies=%s",
                self.source,
                self.project_id,
                self.title,
                self.resource_type,
                self.game_version or "<any>",
                self.loader or "<any>",
                self.target_dir,
                self.install_dependencies,
            )
            if self.source != "modrinth":
                raise RuntimeError("当前只支持从 Modrinth 一键安装；CurseForge 需要手动下载或使用整合包导入。")
            self.status.emit(f"正在安装资源：{self.title}")
            self.progress.emit(20)

            def on_progress(snapshot):
                value = snapshot.get("progress", 0.0)
                self.progress.emit(max(20, min(96, int(20 + value * 76))))
                self.metrics.emit(snapshot)

            payload = install_modrinth_resource(
                self.project_id,
                self.resource_type,
                self.game_version,
                self.loader,
                self.target_dir,
                install_dependencies=self.install_dependencies,
                status_callback=self.status.emit,
                progress_callback=on_progress,
            )
            payload.update({
                "project_id": self.project_id,
                "title": self.title,
                "resource_type": self.resource_type,
                "source": self.source,
            })
            self.progress.emit(100)
            logger.info("ResourceInstallWorker finished: project=%s payload=%s", self.project_id, payload)
            self.finished.emit(payload)
        except Exception as exc:
            logger.exception("ResourceInstallWorker failed: source=%s project=%s", self.source, self.project_id)
            self.failed.emit(str(exc))


class AuthlibInjectorDownloadWorker(QObject):
    progress = Signal(int)
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, install_dir):
        super().__init__()
        self.install_dir = os.path.abspath(install_dir)

    def run(self):
        try:
            self.status.emit("正在获取 authlib-injector 下载信息...")
            url, filename, _ = authlib_injector_download_url()
            os.makedirs(self.install_dir, exist_ok=True)
            target_path = os.path.join(self.install_dir, sanitize_filename(filename, "authlib-injector.jar"))

            def on_progress(snapshot):
                self.progress.emit(max(0, min(100, int(snapshot.get("progress", 0.0) * 100))))
                self.status.emit(f"{snapshot.get('phase', '下载 authlib-injector')}：{snapshot.get('current_file', filename)}")

            stream_download(url, target_path, on_progress, "下载 authlib-injector")
            self.progress.emit(100)
            self.finished.emit({"path": target_path})
        except Exception as exc:
            self.failed.emit(str(exc))


class JavaDownloadWorker(QObject):
    progress = Signal(int)
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, major_version, install_root):
        super().__init__()
        self.major_version = int(major_version)
        self.install_root = os.path.abspath(install_root)

    def run(self):
        temp_dir = tempfile.mkdtemp(prefix="mcgo-java-")
        try:
            self.status.emit(f"正在获取 Java {self.major_version} 下载信息...")
            url, filename = java_runtime_download_url(self.major_version)
            archive_path = os.path.join(temp_dir, filename)

            def progress_callback(snapshot):
                value = snapshot.get("progress", 0.0)
                self.progress.emit(max(0, min(90, int(value * 90))))
                current = snapshot.get("current_file") or filename
                self.status.emit(f"{snapshot.get('phase', '下载 Java')}：{current}")

            self.status.emit(f"正在下载 Java {self.major_version}...")
            stream_download(url, archive_path, progress_callback, f"下载 Java {self.major_version}")
            target_dir = os.path.join(self.install_root, f"temurin-{self.major_version}")
            if os.path.isdir(target_dir):
                shutil.rmtree(target_dir)
            os.makedirs(target_dir, exist_ok=True)
            self.status.emit("正在解压 Java 运行时...")
            self.progress.emit(94)
            extract_java_archive(archive_path, target_dir)
            java_path = find_java_in_directory(target_dir)
            if not java_path:
                raise RuntimeError("解压完成但未找到 Java 可执行文件。")
            self.progress.emit(100)
            self.finished.emit({
                "major": self.major_version,
                "java_path": os.path.abspath(java_path),
                "install_dir": target_dir,
            })
        except Exception as exc:
            self.failed.emit(str(exc))
        finally:
            shutil.rmtree(temp_dir, ignore_errors=True)


class AuthWorker(QObject):
    login_url_ready = Signal(str)
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, auto_open_browser):
        super().__init__()
        self.auto_open_browser = auto_open_browser

    def run(self):
        try:
            logger.info("AuthWorker started: auto_open_browser=%s", self.auto_open_browser)
            login_url = authenticator.get_login_url()
            self.login_url_ready.emit(login_url)
            authenticator.authorization_code = None
            if self.auto_open_browser:
                self.status.emit("正在打开浏览器进行 Microsoft 登录...")
                webbrowser.open(login_url)
            else:
                self.status.emit("请手动打开登录链接完成 Microsoft 登录...")
            while authenticator.authorization_code is None:
                QThread.msleep(500)
            logger.info("Microsoft authorization code detected, exchanging tokens")
            asyncio.run(authenticator.authenticate())
            uuid, username, _ = asyncio.run(authenticator.get_minecraft_profile())
            logger.info("AuthWorker finished: username=%s uuid=%s", username, uuid)
            self.finished.emit({
                "id": str(uuidlib.uuid4()),
                "type": "microsoft",
                "display_name": username,
                "username": username,
                "uuid": uuid,
                "access_token": "",
                "refresh_token": authenticator.refresh_token,
            })
        except Exception as e:
            logger.exception("AuthWorker failed")
            self.failed.emit(str(e))


class ExternalAuthWorker(QObject):
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, action, server="", username="", password="", injector_path="", account=None):
        super().__init__()
        self.action = action
        self.server = server
        self.username = username
        self.password = password
        self.injector_path = injector_path
        self.account = dict(account or {})

    def run(self):
        try:
            if self.action == "probe":
                self.status.emit("正在探测外置登录服务器...")
                payload = probe_external_auth_server(self.server)
                payload["action"] = "probe"
                self.finished.emit(payload)
                return

            if self.action == "login":
                if not self.injector_path or not os.path.isfile(self.injector_path):
                    raise RuntimeError("请选择有效的 authlib-injector.jar。")
                self.status.emit("正在连接外置登录服务器...")
                auth_payload = authenticate_external_account(self.server, self.username, self.password)
                auth_payload["action"] = "login"
                auth_payload["authlib_injector_path"] = self.injector_path
                self.finished.emit(auth_payload)
                return

            if self.action == "refresh":
                self.status.emit("正在刷新或验证外置登录账号...")
                refreshed = refresh_external_account(self.account)
                refreshed["action"] = "refresh"
                self.finished.emit(refreshed)
                return

            raise RuntimeError(f"未知外置登录任务：{self.action}")
        except Exception as exc:
            logger.exception("ExternalAuthWorker failed: action=%s", self.action)
            self.failed.emit(str(exc))


class ScanWorker(QObject):
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, task_type, game_dir="", version_type="", mirror_source="official"):
        super().__init__()
        self.task_type = task_type
        self.game_dir = os.path.abspath(game_dir) if game_dir else ""
        self.version_type = version_type
        self.mirror_source = mirror_source

    def run(self):
        try:
            logger.info(
                "ScanWorker started: task=%s game_dir=%s version_type=%s mirror=%s",
                self.task_type,
                self.game_dir,
                self.version_type,
                self.mirror_source,
            )
            if self.task_type == "java":
                self.status.emit("正在扫描 Java...")
                paths = find_java_paths()
                payload = {
                    "task": "java",
                    "paths": paths,
                    "versions": {path: get_java_major_version(path) for path in paths},
                }
            elif self.task_type == "local_versions":
                self.status.emit("正在扫描本地版本...")
                payload = {
                    "task": "local_versions",
                    "versions": get_local_versions(self.game_dir),
                }
            elif self.task_type == "remote_versions":
                self.status.emit("正在刷新远程版本列表...")
                payload = {
                    "task": "remote_versions",
                    "versions": get_remote_versions(self.version_type, self.mirror_source),
                }
            else:
                raise RuntimeError(f"未知扫描任务：{self.task_type}")
            logger.info("ScanWorker finished: task=%s payload_keys=%s", self.task_type, sorted(payload.keys()))
            self.finished.emit(payload)
        except Exception as exc:
            logger.exception("ScanWorker failed: task=%s", self.task_type)
            self.failed.emit(str(exc))


class LaunchWorker(QObject):
    status = Signal(str)
    progress = Signal(int)
    stage = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, java_path, version, game_dir, account, launch_options):
        super().__init__()
        self.java_path = java_path
        self.version = version
        self.game_dir = game_dir
        self.account = dict(account)
        self.launch_options = dict(launch_options or {})

    def run(self):
        try:
            account = dict(self.account)
            logger.info(
                "LaunchWorker started: version=%s java=%s game_dir=%s runtime=%s account=%s extra_jvm_args=%d",
                self.version,
                self.java_path,
                self.game_dir,
                self.launch_options.get("runtime_directory") or self.game_dir,
                redact_mapping({
                    "id": account.get("id"),
                    "type": account.get("type"),
                    "username": account.get("username"),
                    "uuid": account.get("uuid"),
                    "access_token": account.get("access_token"),
                    "refresh_token": account.get("refresh_token"),
                }),
                len(self.launch_options.get("extra_jvm_args") or []),
            )
            username = account.get("username") or None
            uuid = account.get("uuid") or None
            token = account.get("access_token") or None

            if account.get("type") == "offline":
                if not username:
                    raise Exception("离线账号需要填写用户名。")
                self.stage.emit("离线账号校验")
                self.progress.emit(25)
            elif account.get("type") == "microsoft":
                refresh_token = account.get("refresh_token", "")
                if not refresh_token:
                    raise Exception("该 Microsoft 账号没有可用的刷新令牌，请重新登录。")

                self.stage.emit("刷新 Microsoft 登录")
                self.progress.emit(20)
                self.status.emit("正在刷新 Microsoft 登录状态...")
                session = MicrosoftAuthenticator(client_id, redirect_uri)
                asyncio.run(session.refresh_access_token(refresh_token))
                uuid, username, _ = asyncio.run(session.get_minecraft_profile())
                token = session.minecraft_access_token
                account["refresh_token"] = session.refresh_token
                account["username"] = username
                account["uuid"] = uuid
                account["display_name"] = username
            elif account.get("type") == "external":
                if not username or not uuid or not token:
                    raise Exception("外置登录账号缺少用户名、UUID 或 Access Token，请重新登录。")
                self.stage.emit("外置登录校验")
                self.progress.emit(18)
                self.status.emit("正在刷新或验证外置登录状态...")
                account = refresh_external_account(account)
                username = account.get("username") or username
                uuid = account.get("uuid") or uuid
                token = account.get("access_token") or token
                self.progress.emit(25)
                self.status.emit("正在使用外置登录账号启动...")
            else:
                raise Exception(f"不支持的账号类型：{account.get('type')}")

            self.stage.emit("构建启动命令")
            self.progress.emit(70)
            pre_launch_command = (self.launch_options.get("pre_launch_command") or "").strip()
            if pre_launch_command:
                self.status.emit("正在执行启动前命令...")
                subprocess.run(
                    pre_launch_command,
                    shell=True,
                    cwd=self.launch_options.get("runtime_directory") or self.game_dir,
                    check=True,
                    **hidden_subprocess_kwargs(),
                )
            self.status.emit(f"正在启动 Minecraft {self.version}...")
            jvm_args = list(self.launch_options.get("extra_jvm_args") or [])
            if account.get("type") == "external":
                jvm_args = [*authlib_injector_args(account), *jvm_args]
            launched = launch_minecraft(
                self.java_path,
                self.version,
                self.game_dir,
                token,
                username,
                uuid,
                runtime_directory=self.launch_options.get("runtime_directory"),
                extra_jvm_args=jvm_args,
                extra_game_args=self.launch_options.get("extra_game_args") or [],
                min_memory_mb=self.launch_options.get("min_memory_mb", 0),
                max_memory_mb=self.launch_options.get("max_memory_mb", 0),
                window_width=self.launch_options.get("window_width", 0),
                window_height=self.launch_options.get("window_height", 0),
                gc_strategy=self.launch_options.get("gc_strategy", "G1GC"),
            )
            if not launched:
                raise Exception(f"本地未找到版本 {self.version}，请先下载。")

            self.stage.emit("启动进程")
            self.progress.emit(100)
            logger.info("LaunchWorker finished: version=%s username=%s", self.version, username)
            self.finished.emit({
                "version": self.version,
                "account": account,
                "username": username,
            })
        except Exception as exc:
            logger.exception("LaunchWorker failed: version=%s", self.version)
            self.failed.emit(str(exc))


class Page(ScrollArea):
    def __init__(self, object_name, title, subtitle):
        super().__init__()
        self.setObjectName(object_name)
        self._configure_scroll_behavior()
        self.view = QWidget()
        self.view.setStyleSheet("background: transparent;")
        self.layout = QVBoxLayout(self.view)
        self.layout.setContentsMargins(32, 28, 32, 32)
        self.layout.setSpacing(18)
        self.layout.addWidget(TitleLabel(title))
        self.layout.addWidget(CaptionLabel(subtitle))
        self.breadcrumb_bar = BreadcrumbBar()
        self.breadcrumb_bar.setObjectName(f"{object_name}Breadcrumb")
        self.layout.addWidget(self.breadcrumb_bar)
        self.setWidget(self.view)
        self.setWidgetResizable(True)
        self.setFrameShape(QFrame.Shape.NoFrame)
        self.set_breadcrumbs([])

    def _configure_scroll_behavior(self):
        self.setSmoothMode(SmoothMode.NO_SMOOTH, Qt.Orientation.Vertical)
        self.setSmoothMode(SmoothMode.NO_SMOOTH, Qt.Orientation.Horizontal)
        self.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.setVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAsNeeded)

    def set_breadcrumbs(self, crumbs):
        self.breadcrumb_bar.blockSignals(True)
        self.breadcrumb_bar.clear()
        if not crumbs:
            self.breadcrumb_bar.setVisible(False)
            self.breadcrumb_bar.blockSignals(False)
            return

        self.breadcrumb_bar.setVisible(True)
        for index, crumb in enumerate(crumbs):
            route_key = crumb.get("route_key") or f"breadcrumb_{index}"
            self.breadcrumb_bar.addItem(route_key, crumb.get("label", ""))
        self.breadcrumb_bar.setCurrentIndex(len(crumbs) - 1)
        self.breadcrumb_bar.blockSignals(False)


class NativeComboBoxMenu(ComboBoxMenu):
    def __init__(self, parent=None):
        super().__init__(parent)
        if hasattr(self.view, "scrollDelegate"):
            self.view.scrollDelegate.verticalSmoothScroll.setSmoothMode(NativeSmoothMode.NO_SMOOTH)
            self.view.scrollDelegate.horizonSmoothScroll.setSmoothMode(NativeSmoothMode.NO_SMOOTH)


class NativeComboBox(ComboBox):
    def _createComboMenu(self):
        return NativeComboBoxMenu(self)


class UiMotionController(QObject):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.animations = []
        self.last_trigger_at = {}

    def _track(self, animation):
        self.animations.append(animation)

        def cleanup():
            try:
                self.animations.remove(animation)
            except ValueError:
                pass

        if isinstance(animation, QParallelAnimationGroup):
            animation.finished.connect(cleanup)
        else:
            animation.finished.connect(cleanup)
        return animation

    def _opacity_effect(self, widget):
        effect = widget.graphicsEffect()
        if isinstance(effect, QGraphicsOpacityEffect):
            effect.setEnabled(True)
            return effect

        effect = QGraphicsOpacityEffect(widget)
        effect.setOpacity(0.0)
        effect.setEnabled(False)
        widget.setGraphicsEffect(effect)
        effect.setEnabled(True)
        return effect

    def _finish_with_effect(self, animation, effect):
        def cleanup_effect():
            effect.setOpacity(1.0)
            effect.setEnabled(False)

        animation.finished.connect(cleanup_effect)

    def _curve_accelerate(self):
        return FluentAnimation.createBezierCurve(0.18, 0.0, 0.0, 1.0)

    def _curve_decelerate(self):
        return FluentAnimation.createBezierCurve(0.12, 0.82, 0.22, 1.0)

    def _curve_emphasized(self):
        return FluentAnimation.createBezierCurve(0.2, 0.0, 0.0, 1.0)

    def fade_slide_in(self, widget, offset=18, duration=320):
        if widget is None:
            return

        effect = self._opacity_effect(widget)
        effect.setOpacity(0.0)

        opacity_animation = QPropertyAnimation(effect, b"opacity", widget)
        opacity_animation.setDuration(duration)
        opacity_animation.setStartValue(0.0)
        opacity_animation.setEndValue(1.0)
        opacity_animation.setEasingCurve(self._curve_accelerate())
        self._finish_with_effect(opacity_animation, effect)

        group = QParallelAnimationGroup(widget)
        group.addAnimation(opacity_animation)
        self._track(group).start()

    def cross_fade_stack(self, stack, index, duration=260):
        if stack is None or index < 0 or index >= stack.count():
            return

        current = stack.currentWidget()
        target = stack.widget(index)
        if target is None or current is target:
            stack.setCurrentIndex(index)
            return

        effect = self._opacity_effect(target)

        effect.setOpacity(0.0)
        stack.setCurrentIndex(index)

        opacity_animation = QPropertyAnimation(effect, b"opacity", target)
        opacity_animation.setDuration(duration)
        opacity_animation.setStartValue(0.0)
        opacity_animation.setEndValue(1.0)
        opacity_animation.setEasingCurve(self._curve_accelerate())
        self._finish_with_effect(opacity_animation, effect)

        group = QParallelAnimationGroup(target)
        group.addAnimation(opacity_animation)
        self._track(group).start()

    def pulse_list(self, list_widget, duration=220):
        if list_widget is None:
            return

        effect = self._opacity_effect(list_widget)
        effect.setOpacity(0.24)

        animation = QPropertyAnimation(effect, b"opacity", list_widget)
        animation.setDuration(duration)
        animation.setStartValue(0.24)
        animation.setEndValue(1.0)
        animation.setEasingCurve(self._curve_decelerate())
        self._finish_with_effect(animation, effect)
        self._track(animation).start()

    def pulse_widget(self, widget, duration=220, start_opacity=0.55, throttle_key=None, min_interval=0.0):
        if widget is None:
            return

        if throttle_key:
            now = time.monotonic()
            previous = self.last_trigger_at.get(throttle_key, 0.0)
            if now - previous < min_interval:
                return
            self.last_trigger_at[throttle_key] = now

        effect = self._opacity_effect(widget)
        effect.setOpacity(start_opacity)

        animation = QPropertyAnimation(effect, b"opacity", widget)
        animation.setDuration(duration)
        animation.setStartValue(start_opacity)
        animation.setEndValue(1.0)
        animation.setEasingCurve(self._curve_decelerate())
        self._finish_with_effect(animation, effect)
        self._track(animation).start()


class LauncherWindow(FluentWindow):
    def __init__(self):
        super().__init__()
        self.motion = UiMotionController(self)
        self.download_thread = None
        self.download_worker = None
        self.install_thread = None
        self.install_worker = None
        self.repair_thread = None
        self.repair_worker = None
        self.modpack_thread = None
        self.modpack_worker = None
        self.resource_search_thread = None
        self.resource_search_worker = None
        self.resource_compat_thread = None
        self.resource_compat_worker = None
        self.resource_search_generation = 0
        self.resource_detail_thread = None
        self.resource_detail_worker = None
        self.resource_install_thread = None
        self.resource_install_worker = None
        self.resource_search_hits = []
        self.authlib_download_thread = None
        self.authlib_download_worker = None
        self.java_download_thread = None
        self.java_download_worker = None
        self.auth_thread = None
        self.auth_worker = None
        self.external_auth_thread = None
        self.external_auth_worker = None
        self.launch_thread = None
        self.launch_worker = None
        self.download_task_queue = deque()
        self.active_download_task = None
        self.last_failed_download_task = None
        self.canceling_download_task = False
        self.scan_threads = {}
        self.scan_workers = {}
        self.scan_feedback_tasks = set()
        self.java_versions = {}
        self.accounts = load_accounts()
        self.version_settings = load_version_settings()
        self.media_player = QMediaPlayer(self)
        self.audio_output = QAudioOutput(self)
        self.media_player.setAudioOutput(self.audio_output)
        self.account_index_ids = []
        self.manage_account_index_ids = []
        self.delete_account_index_ids = []
        self.version_display_ids = []
        self.version_list_ids = []
        self.resource_version_ids = []
        self.selected_account_id = config.get("ACCOUNTS", "selected_account_id", fallback="")
        self.setWindowTitle("McGo")
        self.resize(1120, 760)
        self.setMinimumSize(980, 650)
        self.setMicaEffectEnabled(False)
        self.setCustomBackgroundColor("#f5f5f5", "#202020")
        self.setStyleSheet("""
            Page, QWidget#homePage, QWidget#launchPage, QWidget#downloadPage, QWidget#settingsPage, QWidget#logPage {
                background: transparent;
            }
            CardWidget {
                border-radius: 12px;
            }
        """)

        self.build_controls()
        self.build_pages()
        self.apply_theme_image()
        self.init_navigation()
        self.update_download_advanced_visibility()
        self.apply_feature_visibility()
        self.apply_music_settings(show_feedback=False)
        self.refresh_account_selector()
        self.remote_version_combo.addItem("点击刷新远程版本")
        self.log("QFluentWidgets 界面已启动。远程版本列表已延后加载。")
        QTimer.singleShot(0, self.initialize_background_state)
        QTimer.singleShot(40, self.animate_initial_views)

    def initialize_background_state(self):
        self.refresh_java_paths(show_feedback=False)
        self.refresh_local_versions(show_feedback=False)

    def animate_initial_views(self):
        self.animate_card_group(getattr(self, "home_cards", []))

    def animate_card_group(self, widgets):
        for widget in widgets:
            if widget is None:
                continue
            effect = widget.graphicsEffect()
            if not isinstance(effect, QGraphicsOpacityEffect):
                effect = QGraphicsOpacityEffect(widget)
                widget.setGraphicsEffect(effect)
            effect.setOpacity(0.0)
            effect.setEnabled(True)

        for index, widget in enumerate(widgets):
            QTimer.singleShot(
                42 * index,
                lambda current=widget: self.motion.fade_slide_in(current, offset=16, duration=240),
            )

    def build_controls(self):
        self.java_combo = NativeComboBox()
        self.java_combo.currentTextChanged.connect(self.on_java_selected)
        self.java_version_label = BodyLabel("未选择 Java")
        self.java_download_status_label = CaptionLabel("可自动下载当前版本推荐的 Java 运行时")
        self.java_download_progress_bar = ProgressBar()
        self.java_download_progress_bar.setRange(0, 100)
        self.java_download_progress_bar.setValue(0)
        self.version_category_combo = NativeComboBox()
        self.version_category_combo.addItems(["全部版本", "收藏", "原版", "仅 OptiFine", "可安装 Mod", "隐藏"])
        self.version_category_combo.currentTextChanged.connect(lambda _: self.refresh_local_versions(show_feedback=False))
        self.version_display_combo = NativeComboBox()
        self.version_display_combo.currentTextChanged.connect(self.on_version_display_selected)
        self.version_list = ListWidget()
        self.version_list.setSelectionMode(QAbstractItemView.SelectionMode.SingleSelection)
        self.version_list.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.version_list.setWordWrap(True)
        self.version_list.setMinimumHeight(320)
        self.version_list.currentItemChanged.connect(self.on_version_list_selected)
        if hasattr(self.version_list, "setSmoothMode"):
            self.version_list.setSmoothMode(SmoothMode.NO_SMOOTH)
        self.local_version_combo = NativeComboBox()
        self.local_version_combo.currentTextChanged.connect(self.on_local_version_changed)
        self.remote_version_combo = NativeComboBox()
        self.install_type_combo = NativeComboBox()
        self.install_type_combo.addItems(["fabric", "forge", "neoforge", "optifine", "fabric_api"])
        self.install_type_combo.currentTextChanged.connect(self.update_install_button_text)
        self.install_type_combo.currentTextChanged.connect(lambda _: self.refresh_install_versions())
        self.install_version_combo = NativeComboBox()
        self.version_type_combo = NativeComboBox()
        self.version_type_combo.addItems(["release", "snapshot", "old_alpha", "old_beta"])
        self.mirror_combo = NativeComboBox()
        self.mirror_combo.addItems(list(MIRROR_SOURCES.keys()))
        self.mirror_combo.setCurrentText(config.get("DOWNLOAD", "mirror_source", fallback="official"))
        self.download_preset_combo = NativeComboBox()
        self.download_preset_combo.addItems(list(DOWNLOAD_PRESETS.keys()))
        self.download_preset_combo.currentTextChanged.connect(self.apply_download_preset)
        self.download_install_combo = NativeComboBox()
        self.download_install_combo.addItems(["不安装", "fabric", "forge", "neoforge", "optifine"])
        self.download_install_combo.currentTextChanged.connect(lambda _: self.update_download_addon_controls())
        self.download_core_threads_input = SpinBox()
        self.download_core_threads_input.setRange(1, 64)
        self.download_core_threads_input.setValue(config.getint("DOWNLOAD", "max_core_threads", fallback=12))
        self.download_asset_threads_input = SpinBox()
        self.download_asset_threads_input.setRange(1, 96)
        self.download_asset_threads_input.setValue(config.getint("DOWNLOAD", "max_asset_threads", fallback=24))
        self.download_speed_limit_input = SpinBox()
        self.download_speed_limit_input.setRange(0, 1024 * 1024)
        self.download_speed_limit_input.setSingleStep(256)
        self.download_speed_limit_input.setSuffix(" KB/s")
        self.download_speed_limit_input.setValue(config.getint("DOWNLOAD", "speed_limit_kbps", fallback=0))
        self.download_cache_combo = NativeComboBox()
        self.download_cache_combo.addItems(["reuse", "network_only"])
        self.download_cache_combo.setCurrentText(config.get("DOWNLOAD", "cache_strategy", fallback="reuse"))
        self.login_mode_combo = NativeComboBox()
        self.login_mode_combo.addItems(["offline", "microsoft", "external"])
        self.login_mode_combo.setCurrentText("microsoft" if config.getboolean("AUTH", "use_microsoft_login", fallback=False) else "offline")
        self.login_mode_combo.currentTextChanged.connect(self.update_account_field_visibility)
        self.account_combo = NativeComboBox()
        self.account_combo.currentTextChanged.connect(self.on_account_selected)
        self.manage_account_combo = NativeComboBox()
        self.manage_account_combo.currentTextChanged.connect(self.on_manage_account_selected)
        self.username_input = LineEdit()
        self.username_input.setText(config.get("USER", "username", fallback=""))
        self.uuid_input = LineEdit()
        self.uuid_input.setText(config.get("USER", "uuid", fallback=""))
        self.access_token_input = PasswordLineEdit()
        self.access_token_input.setText(config.get("USER", "accessToken", fallback=""))
        self.external_server_input = LineEdit()
        self.external_server_input.setPlaceholderText("https://example.com/api/yggdrasil")
        self.external_username_input = LineEdit()
        self.external_username_input.setPlaceholderText("外置登录用户名或邮箱")
        self.external_password_input = PasswordLineEdit()
        self.external_password_input.setPlaceholderText("密码不会保存")
        self.authlib_injector_input = LineEdit()
        self.authlib_injector_input.setPlaceholderText("authlib-injector.jar 路径")
        self.external_status_label = BodyLabel("外置登录未连接")
        self.external_server_input.textChanged.connect(self.on_external_form_changed)
        self.external_username_input.textChanged.connect(self.on_external_form_changed)
        self.authlib_injector_input.textChanged.connect(self.on_external_form_changed)
        self.advanced_mode_check = CheckBox("高级模式：显示更多启动器选项")
        self.advanced_mode_check.setChecked(config.getboolean("UI", "advanced_mode", fallback=False))
        self.advanced_mode_check.stateChanged.connect(self.on_advanced_mode_changed)
        self.theme_combo = NativeComboBox()
        self.theme_combo.addItems(["dark", "light", "auto"])
        self.theme_combo.setCurrentText(config.get("UI", "theme", fallback="dark"))
        self.theme_combo.currentTextChanged.connect(self.on_theme_changed)
        self.theme_image_input = LineEdit()
        self.theme_image_input.setText(config.get("UI", "theme_image", fallback=""))
        self.theme_image_input.setPlaceholderText("可选：背景图片路径")
        self.home_content_input = LineEdit()
        self.home_content_input.setText(config.get("HOME", "content_source", fallback=""))
        self.home_content_input.setPlaceholderText("本地 txt/md 文件；高级模式可用 http(s) 纯文本")
        self.home_network_check = CheckBox("允许联网主页纯文本")
        self.home_network_check.setChecked(config.getboolean("HOME", "allow_network", fallback=False))
        self.music_path_input = LineEdit()
        self.music_path_input.setText(config.get("MUSIC", "path", fallback=""))
        self.music_path_input.setPlaceholderText("本地音乐文件路径")
        self.music_enabled_check = CheckBox("启用背景音乐")
        self.music_enabled_check.setChecked(config.getboolean("MUSIC", "enabled", fallback=False))
        self.music_pause_on_launch_check = CheckBox("游戏启动后暂停音乐")
        self.music_pause_on_launch_check.setChecked(config.getboolean("MUSIC", "pause_on_launch", fallback=True))
        self.music_volume_input = SpinBox()
        self.music_volume_input.setRange(0, 100)
        self.music_volume_input.setSuffix("%")
        self.music_volume_input.setValue(config.getint("MUSIC", "volume", fallback=35))
        self.server_list_input = TextEdit()
        self.server_list_input.setAcceptRichText(False)
        self.server_list_input.setPlainText(config.get("SERVERS", "items", fallback=""))
        self.server_list_input.setPlaceholderText("每行一个服务器，例如：mc.example.com:25565 | 生存服")
        self.show_download_check = CheckBox("显示下载页")
        self.show_download_check.setChecked(config.getboolean("FEATURES", "show_download", fallback=True))
        self.show_manage_check = CheckBox("显示管理页")
        self.show_manage_check.setChecked(config.getboolean("FEATURES", "show_manage", fallback=True))
        self.auto_open_browser_check = CheckBox("Microsoft 登录时自动打开浏览器")
        self.auto_open_browser_check.setChecked(config.getboolean("AUTH", "auto_open_browser", fallback=True))
        self.resource_isolation_check = CheckBox("启用资源隔离（每个版本使用独立 versions/<版本名> 运行目录）")
        self.resource_isolation_check.setChecked(config.getboolean("GAME", "enable_resource_isolation", fallback=False))
        self.resource_isolation_check.stateChanged.connect(lambda _: self.on_local_version_changed(self.current_selected_version()))
        self.game_dir_input = LineEdit()
        self.game_dir_input.setText(config.get("GAME", "directory", fallback=game_directory))
        self.login_link_input = LineEdit()
        self.login_link_input.setReadOnly(True)
        self.login_link_input.setPlaceholderText("关闭自动打开后，Microsoft 登录链接会显示在这里")
        self.login_link_button = PushButton("打开登录链接")
        self.login_link_button.setVisible(False)
        self.delete_account_combo = NativeComboBox()
        self.progress_bar = ProgressBar()
        self.progress_bar.setRange(0, 100)
        self.download_metrics_label = BodyLabel("等待下载")
        self.download_queue_label = BodyLabel("任务队列：空")
        self.install_status_label = BodyLabel("等待安装任务")
        self.install_metrics_label = BodyLabel("尚未开始安装")
        self.launch_status_label = BodyLabel("将根据游戏版本自动选择合适的 Java")
        self.version_summary_label = BodyLabel("未选择版本")
        self.version_alias_input = LineEdit()
        self.version_alias_input.setPlaceholderText("给当前版本起一个更容易识别的名称")
        self.version_jvm_args_input = LineEdit()
        self.version_jvm_args_input.setPlaceholderText("-XX:-OmitStackTraceInFastThrow -Djdk.lang.Process.allowAmbiguousCommands=True -Dfml.ignoreInvalidMinecraftCertificates=True -Dfml.ignorePatchDiscrepancies=True")
        self.version_game_args_input = LineEdit()
        self.version_game_args_input.setPlaceholderText("--fullscreen --quickPlaySingleplayer WorldName")
        self.version_pre_launch_input = LineEdit()
        self.version_pre_launch_input.setPlaceholderText("启动前命令，例如备份存档或同步配置")
        self.version_manual_memory_check = CheckBox("手动分配最大内存")
        self.version_manual_memory_check.stateChanged.connect(self.on_manual_memory_changed)
        self.version_memory_label = BodyLabel("最大内存：自动")
        self.version_memory_slider = Slider(Qt.Orientation.Horizontal)
        total_memory_mb, _ = system_memory_mb()
        slider_max_memory = max(8192, min(65536, int((total_memory_mb or 32768) // 1024 * 1024)))
        self.version_memory_slider.setRange(1024, slider_max_memory)
        self.version_memory_slider.setSingleStep(256)
        self.version_memory_slider.setTickInterval(2048)
        self.version_memory_slider.valueChanged.connect(self.on_memory_slider_changed)
        self.version_min_memory_input = SpinBox()
        self.version_min_memory_input.setRange(0, 65536)
        self.version_min_memory_input.setSingleStep(256)
        self.version_min_memory_input.setSuffix(" MB")
        self.version_window_width_input = SpinBox()
        self.version_window_width_input.setRange(0, 16384)
        self.version_window_width_input.setSingleStep(64)
        self.version_window_width_input.setSuffix(" px")
        self.version_window_height_input = SpinBox()
        self.version_window_height_input.setRange(0, 16384)
        self.version_window_height_input.setSingleStep(64)
        self.version_window_height_input.setSuffix(" px")
        self.version_gc_combo = NativeComboBox()
        self.version_gc_combo.addItems(GC_STRATEGIES)
        self.version_custom_dir_input = LineEdit()
        self.version_custom_dir_input.setPlaceholderText("留空则使用默认游戏目录或 versions/<版本名> 资源隔离目录")
        self.version_isolation_check = CheckBox("当前版本单独使用 versions/<版本名> 资源隔离目录")
        self.version_favorite_check = CheckBox("收藏当前版本")
        self.version_hidden_check = CheckBox("隐藏当前版本")
        self.version_icon_combo = NativeComboBox()
        self.version_icon_combo.addItems(VERSION_ICON_LABELS)
        self.version_mods_list = ListWidget()
        self.version_mods_list.setSelectionMode(QAbstractItemView.SelectionMode.SingleSelection)
        self.version_mods_list.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.version_mods_list.setHorizontalScrollMode(QAbstractItemView.ScrollMode.ScrollPerPixel)
        self.version_mods_list.setVerticalScrollMode(QAbstractItemView.ScrollMode.ScrollPerPixel)
        if hasattr(self.version_mods_list, "setSmoothMode"):
            self.version_mods_list.setSmoothMode(SmoothMode.NO_SMOOTH)
        self.version_mods_list.setUniformItemSizes(True)
        self.version_mods_list.setWordWrap(True)
        self.version_mods_list.setMinimumHeight(360)
        self.version_mods_list.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        self.version_mods_list.setSelectRightClickedRow(True)
        self.version_mods_list.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        self.version_mods_list.itemDoubleClicked.connect(self.on_version_mod_item_activated)
        self.version_mods_list.customContextMenuRequested.connect(self.show_version_mod_context_menu)
        self.install_log = TextEdit()
        self.install_log.setReadOnly(True)
        self.install_log.setAcceptRichText(False)
        self.install_log.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.install_log.setLineWrapMode(QTextEdit.LineWrapMode.WidgetWidth)
        if hasattr(self.install_log, "setSmoothMode"):
            self.install_log.setSmoothMode(SmoothMode.NO_SMOOTH)
        self.install_log.document().setMaximumBlockCount(1000)
        self.status_log = TextEdit()
        self.status_log.setReadOnly(True)
        self.status_log.setAcceptRichText(False)
        self.status_log.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.status_log.setLineWrapMode(QTextEdit.LineWrapMode.WidgetWidth)
        if hasattr(self.status_log, "setSmoothMode"):
            self.status_log.setSmoothMode(SmoothMode.NO_SMOOTH)
        self.status_log.document().setMaximumBlockCount(1000)
        self.version_type_combo.currentTextChanged.connect(lambda _: self.log("版本类型已更改，点击“刷新远程版本”重新加载列表。"))
        self.download_fabric_api_check = CheckBox("同时安装 Fabric API")
        self.download_fabric_api_check.stateChanged.connect(lambda _: self.update_download_addon_controls())
        self.download_addon_hint_label = CaptionLabel("可在下载原版后自动继续安装；Fabric API 仅在 Fabric 一起安装时可用。")
        self.download_warning_label = CaptionLabel("")
        self.resource_query_input = LineEdit()
        self.resource_query_input.setPlaceholderText("搜索 Mod、资源包、光影或数据包")
        self.resource_source_combo = NativeComboBox()
        self.resource_source_combo.addItems(["modrinth", "curseforge", "local"])
        self.resource_source_combo.currentTextChanged.connect(self.update_resource_source_controls)
        self.resource_sort_combo = NativeComboBox()
        self.resource_sort_combo.addItems(list(RESOURCE_SEARCH_SORTS.keys()))
        self.resource_dependency_check = CheckBox("安装 Modrinth 必需依赖")
        self.resource_dependency_check.setChecked(True)
        self.resource_type_combo = NativeComboBox()
        self.resource_type_combo.addItems(["mod", "resourcepack", "shader", "datapack"])
        self.resource_type_combo.currentTextChanged.connect(lambda _: self.refresh_resource_target_versions())
        self.resource_version_combo = NativeComboBox()
        self.resource_version_combo.currentTextChanged.connect(lambda _: self.resource_detail_view.clear())
        self.resource_result_list = ListWidget()
        self.resource_result_list.setMinimumHeight(320)
        self.resource_result_list.setWordWrap(True)
        self.resource_result_list.setSelectionMode(QAbstractItemView.SelectionMode.SingleSelection)
        self.resource_result_list.itemDoubleClicked.connect(lambda _: self.install_selected_resource())
        self.resource_result_list.currentItemChanged.connect(lambda *_: self.show_selected_resource_detail())
        self.resource_status_label = BodyLabel("等待搜索")
        self.resource_detail_loading = IndeterminateProgressRing()
        self.resource_detail_loading.setFixedSize(28, 28)
        self.resource_detail_loading.setVisible(False)
        self.resource_detail_view = TextEdit()
        self.resource_detail_view.setReadOnly(True)
        self.resource_detail_view.setAcceptRichText(True)
        self.resource_detail_view.setMinimumHeight(220)

    def build_pages(self):
        self.home_page = Page("homePage", "McGo", "一个 Fluent 风格的 Minecraft 启动器")
        self.launch_page = Page("launchPage", "启动游戏", "选择 Java 和本地版本，然后启动 Minecraft")
        self.download_page = Page("downloadPage", "下载游戏", "选择版本类型、镜像源和目标版本")
        self.manage_page = Page("managePage", "管理中心", "将账号、环境和日志按任务分组，减少来回切页")
        for page in (self.home_page, self.launch_page, self.download_page, self.manage_page):
            page.breadcrumb_bar.currentItemChanged.connect(self.on_breadcrumb_changed)
        self.page_breadcrumb_labels = {
            self.home_page: "首页",
            self.launch_page: "启动",
            self.download_page: "下载",
            self.manage_page: "管理",
        }
        self.version_section_labels = {
            "selector": "选择版本",
            "settings": "版本设置",
        }
        self.download_section_labels = {
            "vanilla": "下载原版",
            "addons": "安装扩展",
            "modpack": "导入整合包",
            "resources": "资源市场",
        }
        self.manage_section_labels = {
            "accounts": "账号",
            "environment": "环境",
            "logs": "日志",
            "help": "帮助",
        }
        self.account_section_labels = {
            "overview": "当前账号",
            "offline": "离线账号",
            "microsoft": "Microsoft",
            "external": "外置登录",
        }
        self.current_version_section = "selector"
        self.current_download_section = "vanilla"
        self.current_manage_section = "accounts"
        self.current_account_section = "overview"
        self.current_version_category = ""
        self.current_download_category = ""
        self.current_manage_category = ""
        self.current_account_category = ""

        self.build_home_page()
        self.build_launch_page()
        self.build_download_page()
        self.build_manage_page()
        self.update_breadcrumbs()

    def init_navigation(self):
        self.addSubInterface(self.home_page, FluentIcon.HOME, "首页")
        self.addSubInterface(self.launch_page, FluentIcon.GAME, "启动")
        self.addSubInterface(self.download_page, FluentIcon.DOWNLOAD, "下载")
        self.addSubInterface(self.manage_page, FluentIcon.SETTING, "管理")
        self.navigation_pages = {
            "download": self.download_page,
            "manage": self.manage_page,
        }
        self.navigation_visible_keys = {
            "download": True,
            "manage": True,
        }
        self.configure_navigation_animation()
        if hasattr(self, "stackedWidget"):
            self.stackedWidget.currentChanged.connect(self.on_main_stack_changed)

    def configure_navigation_animation(self):
        navigation = getattr(self, "navigationInterface", None)
        panel = getattr(navigation, "panel", None) if navigation else None
        if not panel or not hasattr(panel, "expandAni"):
            return
        panel.expandAni.setDuration(240)
        panel.expandAni.setEasingCurve(FluentAnimation.createBezierCurve(0.2, 0.0, 0.0, 1.0))
        navigation.setExpandWidth(312)
        navigation.setMinimumExpandWidth(100000)
        if not hasattr(panel, "_mcgo_original_collapse"):
            panel._mcgo_original_collapse = panel.collapse
            panel.collapse = types.MethodType(self._navigation_panel_collapse, panel)

    def _navigation_panel_collapse(self, panel_self):
        sender = panel_self.sender()
        if (
            sender is not None
            and isinstance(sender, NavigationTreeWidgetBase)
            and panel_self.displayMode == NavigationDisplayMode.MENU
        ):
            return
        return panel_self._mcgo_original_collapse()

    def on_main_stack_changed(self, index):
        if not hasattr(self, "stackedWidget"):
            return
        page = self.stackedWidget.widget(index)
        if page is None:
            return
        self.update_breadcrumbs(page)
        if page is self.home_page:
            self.animate_card_group(getattr(self, "home_cards", []))
        elif page is self.launch_page:
            self.animate_card_group(getattr(self, "launch_cards", []))
        elif page is self.download_page:
            self.animate_card_group(getattr(self, "download_cards", []))
        elif page is self.manage_page:
            self.animate_card_group(getattr(self, "manage_cards", []))

    def breadcrumb_crumb(self, label, route_key):
        return {"label": label, "route_key": route_key}

    def update_breadcrumbs(self, page=None):
        if not all(hasattr(self, name) for name in ("home_page", "launch_page", "download_page", "manage_page")):
            return
        if page is None:
            page = self.stackedWidget.currentWidget() if hasattr(self, "stackedWidget") else self.home_page

        home_crumb = self.breadcrumb_crumb("首页", "home")
        page_label = self.page_breadcrumb_labels.get(page, "")
        if page is self.home_page:
            crumbs = [self.breadcrumb_crumb("首页", "home")]
        elif page is self.launch_page:
            section = getattr(self, "current_version_section", "selector")
            crumbs = [home_crumb, self.breadcrumb_crumb("启动", "launch")]
            category = getattr(self, "current_version_category", "")
            if category:
                crumbs.append(self.breadcrumb_crumb(category, f"launch_category_{category}"))
            if hasattr(self, "version_stack") and self.version_stack.isVisible():
                crumbs.append(self.breadcrumb_crumb(self.version_section_labels.get(section, "选择版本"), f"launch_{section}"))
        elif page is self.download_page:
            section = getattr(self, "current_download_section", "vanilla")
            crumbs = [home_crumb, self.breadcrumb_crumb("下载", "download")]
            category = getattr(self, "current_download_category", "")
            if category:
                crumbs.append(self.breadcrumb_crumb(category, f"download_category_{category}"))
            if hasattr(self, "download_stack") and self.download_stack.isVisible():
                crumbs.append(self.breadcrumb_crumb(self.download_section_labels.get(section, "下载原版"), f"download_{section}"))
        elif page is self.manage_page:
            section = getattr(self, "current_manage_section", "accounts")
            crumbs = [home_crumb, self.breadcrumb_crumb("管理", "manage")]
            category = getattr(self, "current_manage_category", "")
            if category:
                crumbs.append(self.breadcrumb_crumb(category, f"manage_category_{category}"))
            if section == "accounts":
                account_section = getattr(self, "current_account_section", "overview")
                account_category = getattr(self, "current_account_category", "")
                if account_category:
                    crumbs.append(self.breadcrumb_crumb(account_category, f"account_category_{account_category}"))
                if hasattr(self, "account_stack") and self.account_stack.isVisible():
                    crumbs.append(self.breadcrumb_crumb(self.account_section_labels.get(account_section, "当前账号"), f"account_{account_section}"))
            else:
                if hasattr(self, "manage_stack") and self.manage_stack.isVisible():
                    crumbs.append(self.breadcrumb_crumb(self.manage_section_labels.get(section, "账号"), f"manage_{section}"))
        elif page_label:
            crumbs = [home_crumb, self.breadcrumb_crumb(page_label, page_label)]
        else:
            crumbs = []

        if hasattr(page, "set_breadcrumbs"):
            page.set_breadcrumbs(crumbs)

    def on_breadcrumb_changed(self, route_key):
        if route_key == "home":
            self.switch_main_page(self.home_page, self.home_cards)
        elif route_key == "launch":
            self.open_version_overview()
        elif route_key.startswith("launch_category_"):
            self.open_version_overview()
        elif route_key.startswith("launch_") and not route_key.startswith("launch_category_"):
            self.open_version_section(route_key.removeprefix("launch_"))
        elif route_key == "download":
            self.open_download_overview()
        elif route_key.startswith("download_category_"):
            self.open_download_category(route_key.removeprefix("download_category_"))
        elif route_key.startswith("download_") and not route_key.startswith("download_category_"):
            self.open_download_section(route_key.removeprefix("download_"))
        elif route_key == "manage":
            self.open_manage_overview()
        elif route_key.startswith("manage_category_"):
            category = route_key.removeprefix("manage_category_")
            if category == "诊断与帮助":
                self.open_manage_category(category)
            elif category == "账号与登录":
                self.switch_manage_section("accounts", category)
            elif category == "环境与界面":
                self.switch_manage_section("environment", category)
        elif route_key.startswith("manage_") and not route_key.startswith("manage_category_"):
            self.open_manage_section(route_key.removeprefix("manage_"))
        elif route_key.startswith("account_category_"):
            self.open_account_category(route_key.removeprefix("account_category_"))
        elif route_key.startswith("account_") and not route_key.startswith("account_category_"):
            self.open_account_section(route_key.removeprefix("account_"))

    def make_card(self, title, subtitle=None):
        card = CardWidget()
        layout = QVBoxLayout(card)
        layout.setContentsMargins(22, 18, 22, 18)
        layout.setSpacing(12)
        layout.addWidget(SubtitleLabel(title))
        if subtitle:
            layout.addWidget(CaptionLabel(subtitle))
        return card, layout

    def make_choice_card(self, title, subtitle, choices):
        card, layout = self.make_card(title, subtitle)
        card.choice_layout = layout
        for label, description, callback in choices:
            self.add_choice_option(layout, label, description, callback)
        return card

    def add_choice_option(self, layout, label, description, callback):
        button = PushButton(label)
        button.setCursor(Qt.CursorShape.PointingHandCursor)
        button.clicked.connect(callback)
        layout.addWidget(button)
        if description:
            layout.addWidget(CaptionLabel(description))

    def reset_choice_card(self, card, title, subtitle, choices):
        layout = card.choice_layout
        while layout.count():
            item = layout.takeAt(0)
            widget = item.widget()
            if widget is not None:
                widget.deleteLater()
        layout.addWidget(SubtitleLabel(title))
        if subtitle:
            layout.addWidget(CaptionLabel(subtitle))
        for label, description, callback in choices:
            self.add_choice_option(layout, label, description, callback)

    def add_labeled_control(self, layout, label, control):
        container = QWidget()
        row = QVBoxLayout(container)
        row.setContentsMargins(0, 0, 0, 0)
        row.setSpacing(6)
        row.addWidget(CaptionLabel(label))
        row.addWidget(control)
        layout.addWidget(container)
        return container

    def build_home_page(self):
        quick_card, quick_layout = self.make_card("开始使用", "按顺序完成账号、环境、下载和启动，更容易定位问题")
        row = QHBoxLayout()
        manage_button = PrimaryPushButton("1. 管理账号与环境")
        download_button = PushButton("2. 下载版本")
        launch_button = PushButton("3. 启动游戏")
        refresh_button = PushButton("刷新本地状态")
        manage_button.clicked.connect(lambda: self.open_manage_section("accounts"))
        refresh_button.clicked.connect(self.refresh_all)
        download_button.clicked.connect(lambda: self.open_download_section("vanilla"))
        launch_button.clicked.connect(self.open_version_overview)
        row.addWidget(manage_button)
        row.addWidget(download_button)
        row.addWidget(launch_button)
        row.addWidget(refresh_button)
        row.addStretch()
        quick_layout.addLayout(row)

        self.home_custom_card, home_custom_layout = self.make_card("自定义主页", "仅显示本地或受信任的纯文本内容")
        self.home_custom_text = TextEdit()
        self.home_custom_text.setReadOnly(True)
        self.home_custom_text.setAcceptRichText(False)
        self.home_custom_text.setMinimumHeight(180)
        home_custom_layout.addWidget(self.home_custom_text)

        overview_card, overview_layout = self.make_card("当前状态", "启动前只需要确认下面四项是否准备完毕")
        grid = QGridLayout()
        grid.setHorizontalSpacing(24)
        grid.setVerticalSpacing(12)
        self.home_account_label = BodyLabel("账号：未选择")
        self.home_java_label = BodyLabel("Java：未检测")
        self.home_local_label = BodyLabel("本地版本：0")
        self.home_remote_label = BodyLabel("远程版本：0")
        self.home_dir_label = BodyLabel(f"游戏目录：{self.current_game_dir()}")
        grid.addWidget(self.home_account_label, 0, 0)
        grid.addWidget(self.home_java_label, 0, 1)
        grid.addWidget(self.home_local_label, 1, 0)
        grid.addWidget(self.home_remote_label, 1, 1)
        grid.addWidget(self.home_dir_label, 2, 0, 1, 2)
        overview_layout.addLayout(grid)

        self.home_page.layout.addWidget(quick_card)
        self.home_page.layout.addWidget(self.home_custom_card)
        self.home_page.layout.addWidget(overview_card)
        self.home_page.layout.addStretch()
        self.home_cards = [quick_card, self.home_custom_card, overview_card]
        self.refresh_home_content()

    def build_launch_page(self):
        start_card, start_layout = self.make_card("立即启动", "先选账号与版本分类，再从版本中心确认要启动的版本")
        self.add_labeled_control(start_layout, "当前账号", self.account_combo)
        self.add_labeled_control(start_layout, "版本分类", self.version_category_combo)
        self.add_labeled_control(start_layout, "当前版本", self.version_display_combo)
        start_layout.addWidget(self.version_summary_label)
        start_layout.addWidget(self.launch_status_label)
        self.launch_progress_bar = ProgressBar()
        self.launch_progress_bar.setRange(0, 100)
        self.launch_progress_bar.setValue(0)
        self.launch_stage_label = CaptionLabel("当前步骤：等待启动")
        self.launch_method_label = CaptionLabel("登录方式：未选择")
        self.launch_progress_label = CaptionLabel("启动进度：0%")
        start_layout.addWidget(self.launch_progress_bar)
        launch_info_grid = QGridLayout()
        launch_info_grid.setHorizontalSpacing(18)
        launch_info_grid.setVerticalSpacing(8)
        launch_info_grid.addWidget(self.launch_stage_label, 0, 0)
        launch_info_grid.addWidget(self.launch_method_label, 0, 1)
        launch_info_grid.addWidget(self.launch_progress_label, 1, 0)
        start_layout.addLayout(launch_info_grid)
        start_row = QHBoxLayout()
        refresh_button = PushButton("刷新本地版本")
        self.launch_button = PrimaryPushButton("启动 Minecraft")
        refresh_button.clicked.connect(self.refresh_local_versions)
        self.launch_button.clicked.connect(self.launch_game)
        start_row.addWidget(refresh_button)
        start_row.addWidget(self.launch_button)
        start_row.addStretch()
        start_layout.addLayout(start_row)

        env_card, env_layout = self.make_card("运行环境", "通常无需手动调整；仅在需要切换 Java 或确认版本时查看")
        self.add_labeled_control(env_layout, "Java 路径", self.java_combo)
        env_layout.addWidget(self.java_version_label)
        env_layout.addWidget(self.java_download_progress_bar)
        env_layout.addWidget(self.java_download_status_label)

        nav_card = CardWidget()
        nav_layout = QVBoxLayout(nav_card)
        nav_layout.setContentsMargins(22, 18, 22, 18)
        nav_layout.setSpacing(12)
        nav_layout.addWidget(SubtitleLabel("版本中心"))
        nav_layout.addWidget(CaptionLabel("先选择要处理的版本任务，再进入对应页面"))
        self.version_segment = SegmentedWidget()
        self.version_segment.setVisible(False)
        nav_layout.addWidget(self.version_segment)
        self.version_overview_card = self.make_choice_card(
            "选择版本任务",
            "像做选择题一样进入版本列表或当前版本设置",
            [
                ("查找本地版本", "按分类查看并选择要启动的版本", lambda: self.open_version_section("selector", "本地版本")),
                ("配置当前版本", "设置显示名称、内存、运行目录、快捷方式和 Mod", lambda: self.open_version_section("settings", "版本维护")),
            ],
        )
        nav_layout.addWidget(self.version_overview_card)

        self.version_stack = QStackedWidget()
        self.version_selector_view = QWidget()
        self.version_settings_view = QWidget()

        selector_card, selector_layout = self.make_card("选择版本", "按分类查看本地版本，原版、可安装 Mod 和仅 OptiFine 会单独归类")
        self.add_labeled_control(selector_layout, "版本分类", self.version_category_combo)
        self.add_labeled_control(selector_layout, "快速选择", self.version_display_combo)
        selector_layout.addWidget(self.version_list)
        selector_layout.addWidget(self.version_summary_label)

        personalization_card, personalization_layout = self.make_card("个性化", "显示名称会出现在版本列表中，便于区分 Forge、Fabric 等实例")
        self.add_labeled_control(personalization_layout, "显示名称", self.version_alias_input)
        self.add_labeled_control(personalization_layout, "版本图标", self.version_icon_combo)
        personalization_layout.addWidget(self.version_favorite_check)
        personalization_layout.addWidget(self.version_hidden_check)
        personalization_row = QHBoxLayout()
        self.save_version_settings_button = PrimaryPushButton("保存当前版本设置")
        self.save_version_settings_button.clicked.connect(self.save_current_version_settings)
        personalization_row.addWidget(self.save_version_settings_button)
        personalization_row.addStretch()
        personalization_layout.addLayout(personalization_row)

        launch_settings_card, launch_settings_layout = self.make_card("启动设置", "为当前版本单独设置 JVM 参数和运行目录")
        launch_settings_layout.addWidget(self.version_manual_memory_check)
        launch_settings_layout.addWidget(self.version_memory_label)
        self.version_memory_slider_row = self.add_labeled_control(launch_settings_layout, "最大内存", self.version_memory_slider)
        self.version_min_memory_row = self.add_labeled_control(launch_settings_layout, "最小内存", self.version_min_memory_input)
        window_row = QHBoxLayout()
        width_container = QWidget()
        width_layout = QVBoxLayout(width_container)
        width_layout.setContentsMargins(0, 0, 0, 0)
        width_layout.addWidget(CaptionLabel("窗口宽度"))
        width_layout.addWidget(self.version_window_width_input)
        height_container = QWidget()
        height_layout = QVBoxLayout(height_container)
        height_layout.setContentsMargins(0, 0, 0, 0)
        height_layout.addWidget(CaptionLabel("窗口高度"))
        height_layout.addWidget(self.version_window_height_input)
        window_row.addWidget(width_container)
        window_row.addWidget(height_container)
        self.version_window_row = QWidget()
        self.version_window_row.setLayout(window_row)
        launch_settings_layout.addWidget(self.version_window_row)
        self.version_gc_row = self.add_labeled_control(launch_settings_layout, "GC 策略", self.version_gc_combo)
        self.version_jvm_args_row = self.add_labeled_control(launch_settings_layout, "额外 JVM 参数", self.version_jvm_args_input)
        self.version_game_args_row = self.add_labeled_control(launch_settings_layout, "额外游戏参数", self.version_game_args_input)
        self.version_pre_launch_row = self.add_labeled_control(launch_settings_layout, "启动前命令", self.version_pre_launch_input)
        launch_settings_layout.addWidget(self.version_isolation_check)
        self.version_custom_dir_row = self.add_labeled_control(launch_settings_layout, "自定义运行目录", self.version_custom_dir_input)

        shortcut_card, shortcut_layout = self.make_card("快捷方式", "常用文件夹和启动脚本集中在这里")
        shortcut_row = QHBoxLayout()
        self.open_version_folder_button = PushButton("版本文件夹")
        self.open_saves_button = PushButton("存档文件夹")
        self.open_mods_button = PushButton("Mod 文件夹")
        self.open_resourcepacks_button = PushButton("资源包")
        self.open_shaderpacks_button = PushButton("光影")
        self.open_screenshots_button = PushButton("截图")
        self.export_launch_script_button = PushButton("导出启动脚本")
        self.export_modpack_button = PushButton("导出整合包")
        self.analyze_crash_button = PushButton("分析崩溃")
        self.open_version_folder_button.clicked.connect(self.open_current_version_folder)
        self.open_saves_button.clicked.connect(self.open_current_saves_directory)
        self.open_mods_button.clicked.connect(self.open_current_mods_directory)
        self.open_resourcepacks_button.clicked.connect(self.open_current_resourcepacks_directory)
        self.open_shaderpacks_button.clicked.connect(self.open_current_shaderpacks_directory)
        self.open_screenshots_button.clicked.connect(self.open_current_screenshots_directory)
        self.export_launch_script_button.clicked.connect(self.export_current_launch_script)
        self.export_modpack_button.clicked.connect(self.export_current_modpack)
        self.analyze_crash_button.clicked.connect(self.analyze_current_crash)
        shortcut_row.addWidget(self.open_version_folder_button)
        shortcut_row.addWidget(self.open_saves_button)
        shortcut_row.addWidget(self.open_mods_button)
        shortcut_row.addWidget(self.open_resourcepacks_button)
        shortcut_row.addWidget(self.open_shaderpacks_button)
        shortcut_row.addWidget(self.open_screenshots_button)
        shortcut_row.addWidget(self.export_launch_script_button)
        shortcut_row.addWidget(self.export_modpack_button)
        shortcut_row.addWidget(self.analyze_crash_button)
        shortcut_row.addStretch()
        shortcut_layout.addLayout(shortcut_row)

        manage_card, manage_layout = self.make_card("高级管理", "处理当前版本的本地文件和危险操作")
        manage_row = QHBoxLayout()
        self.repair_version_button = PushButton("补全/校验文件")
        self.delete_version_button = PushButton("删除当前版本")
        self.repair_version_button.clicked.connect(self.repair_current_version)
        self.delete_version_button.clicked.connect(self.delete_current_version)
        manage_row.addWidget(self.repair_version_button)
        manage_row.addWidget(self.delete_version_button)
        manage_row.addStretch()
        manage_layout.addLayout(manage_row)
        self.repair_progress_bar = ProgressBar()
        self.repair_progress_bar.setRange(0, 100)
        self.repair_progress_bar.setValue(0)
        self.repair_status_label = BodyLabel("等待补全任务")
        self.repair_metrics_label = CaptionLabel("会校验客户端、依赖库、资源文件和 natives")
        manage_layout.addWidget(self.repair_progress_bar)
        manage_layout.addWidget(self.repair_status_label)
        manage_layout.addWidget(self.repair_metrics_label)

        mod_card, mod_card_layout = self.make_card("Mod 管理", "可安装 Mod 的版本会显示 mods 文件夹内的 jar")
        self.mod_section = QWidget()
        mod_layout = QVBoxLayout(self.mod_section)
        mod_layout.setContentsMargins(0, 0, 0, 0)
        mod_layout.setSpacing(12)
        mod_layout.setStretch(2, 1)
        self.mod_section_title = BodyLabel("Mod 列表")
        self.mod_section_hint = CaptionLabel("当前版本支持 Mod 管理时，可以直接打开 mods 文件夹并启用、禁用或删除 Mod。")
        mod_layout.addWidget(self.mod_section_title)
        mod_layout.addWidget(self.mod_section_hint)
        mod_layout.addWidget(self.version_mods_list)
        settings_row = QHBoxLayout()
        self.toggle_mod_button = PushButton("启用/禁用所选 Mod")
        self.delete_mod_button = PushButton("删除所选 Mod")
        self.toggle_mod_button.clicked.connect(self.toggle_selected_mod)
        self.delete_mod_button.clicked.connect(self.delete_selected_mod)
        settings_row.addWidget(self.toggle_mod_button)
        settings_row.addWidget(self.delete_mod_button)
        settings_row.addStretch()
        mod_layout.addLayout(settings_row)
        mod_card_layout.addWidget(self.mod_section)

        selector_view_layout = QVBoxLayout(self.version_selector_view)
        selector_view_layout.setContentsMargins(0, 0, 0, 0)
        selector_view_layout.addWidget(selector_card)
        selector_view_layout.addStretch()

        settings_view_layout = QVBoxLayout(self.version_settings_view)
        settings_view_layout.setContentsMargins(0, 0, 0, 0)
        settings_view_layout.addWidget(personalization_card)
        settings_view_layout.addWidget(launch_settings_card)
        settings_view_layout.addWidget(shortcut_card)
        settings_view_layout.addWidget(manage_card)
        settings_view_layout.addWidget(mod_card)
        settings_view_layout.addStretch()

        self.version_stack.addWidget(self.version_selector_view)
        self.version_stack.addWidget(self.version_settings_view)
        self.version_segment.addItem("selector", "选择版本", lambda: self.switch_version_section("selector", "本地版本"))
        self.version_segment.addItem("settings", "版本设置", lambda: self.switch_version_section("settings", "版本维护"))
        self.version_segment.setCurrentItem("selector")

        launch_content = QWidget()
        launch_content_layout = QHBoxLayout(launch_content)
        launch_content_layout.setContentsMargins(0, 0, 0, 0)
        launch_content_layout.setSpacing(18)

        version_column = QWidget()
        version_column_layout = QVBoxLayout(version_column)
        version_column_layout.setContentsMargins(0, 0, 0, 0)
        version_column_layout.setSpacing(18)
        version_column_layout.addWidget(nav_card)
        self.version_stack.setVisible(False)
        version_column_layout.addWidget(self.version_stack)
        version_column_layout.addStretch()

        action_column = QWidget()
        action_column_layout = QVBoxLayout(action_column)
        action_column_layout.setContentsMargins(0, 0, 0, 0)
        action_column_layout.setSpacing(18)
        action_column_layout.addWidget(start_card)
        action_column_layout.addWidget(env_card)
        action_column_layout.addStretch()

        launch_content_layout.addWidget(action_column, 2)
        launch_content_layout.addWidget(version_column, 3)

        self.launch_page.layout.addWidget(launch_content)
        self.launch_page.layout.addStretch()
        self.launch_cards = [nav_card, self.version_stack, start_card, env_card]
        self.open_version_overview()

    def build_download_page(self):
        progress_card, progress_layout = self.make_card("任务进度", "下载和扩展安装共用这一组进度与状态信息")
        progress_layout.addWidget(self.progress_bar)
        progress_layout.addWidget(self.download_metrics_label)
        progress_layout.addWidget(self.download_queue_label)
        task_row = QHBoxLayout()
        self.cancel_task_button = PushButton("取消当前任务")
        self.retry_task_button = PushButton("重试失败任务")
        self.clear_queue_button = PushButton("清空队列")
        self.cancel_task_button.clicked.connect(self.cancel_current_download_task)
        self.retry_task_button.clicked.connect(self.retry_last_failed_download_task)
        self.clear_queue_button.clicked.connect(self.clear_download_queue)
        task_row.addWidget(self.cancel_task_button)
        task_row.addWidget(self.retry_task_button)
        task_row.addWidget(self.clear_queue_button)
        task_row.addStretch()
        progress_layout.addLayout(task_row)

        nav_card = CardWidget()
        nav_layout = QVBoxLayout(nav_card)
        nav_layout.setContentsMargins(22, 18, 22, 18)
        nav_layout.setSpacing(12)
        nav_layout.addWidget(SubtitleLabel("下载任务"))
        nav_layout.addWidget(CaptionLabel("先选择下载目的，再进入具体任务"))
        self.download_segment = SegmentedWidget()
        self.download_segment.setVisible(False)
        nav_layout.addWidget(self.download_segment)
        self.download_overview_card = self.make_choice_card(
            "选择下载目的",
            "每一步只需要在少量选项中选择一个",
            [
                ("获取游戏", "下载原版 Minecraft，或在已有原版上安装加载器", lambda: self.open_download_category("获取游戏")),
                ("导入内容", "导入整合包，或从资源市场安装 Mod、资源包和光影", lambda: self.open_download_category("导入内容")),
            ],
        )
        self.download_category_card = self.make_choice_card(
            "选择具体任务",
            "根据上一步分类继续选择",
            [],
        )
        nav_layout.addWidget(self.download_overview_card)
        nav_layout.addWidget(self.download_category_card)

        self.download_stack = QStackedWidget()
        self.download_vanilla_view = QWidget()
        self.download_install_view = QWidget()
        self.download_modpack_view = QWidget()
        self.download_resource_view = QWidget()

        download_card, download_layout = self.make_card("下载原版", "原版下载支持一并勾选后续安装项，减少重复操作")
        self.add_labeled_control(download_layout, "版本类型", self.version_type_combo)
        self.add_labeled_control(download_layout, "远程版本", self.remote_version_combo)
        self.add_labeled_control(download_layout, "镜像源", self.mirror_combo)
        self.download_preset_row = self.add_labeled_control(download_layout, "下载预设", self.download_preset_combo)
        self.add_labeled_control(download_layout, "下载时安装", self.download_install_combo)
        download_layout.addWidget(self.download_fabric_api_check)
        download_tuning_row = QHBoxLayout()
        self.download_tuning_rows = {}
        for key, label, control in (
            ("core", "核心线程", self.download_core_threads_input),
            ("asset", "资源线程", self.download_asset_threads_input),
            ("speed", "速度限制", self.download_speed_limit_input),
        ):
            container = QWidget()
            container_layout = QVBoxLayout(container)
            container_layout.setContentsMargins(0, 0, 0, 0)
            container_layout.addWidget(CaptionLabel(label))
            container_layout.addWidget(control)
            download_tuning_row.addWidget(container)
            self.download_tuning_rows[key] = container
        download_layout.addLayout(download_tuning_row)
        self.download_cache_row = self.add_labeled_control(download_layout, "缓存策略", self.download_cache_combo)
        download_layout.addWidget(self.download_addon_hint_label)
        download_layout.addWidget(self.download_warning_label)
        row = QHBoxLayout()
        self.refresh_remote_button = PushButton("刷新远程版本")
        self.download_button = PrimaryPushButton("下载所选版本")
        self.refresh_remote_button.clicked.connect(self.refresh_remote_versions)
        self.download_button.clicked.connect(self.start_download)
        row.addWidget(self.refresh_remote_button)
        row.addWidget(self.download_button)
        row.addStretch()
        download_layout.addLayout(row)

        install_card, install_layout = self.make_card("安装扩展", "适合已经有原版或已下载好本地版本后，单独追加安装加载器或 Fabric API")
        self.add_labeled_control(install_layout, "安装类型", self.install_type_combo)
        self.add_labeled_control(install_layout, "目标版本", self.install_version_combo)
        install_row = QHBoxLayout()
        self.refresh_install_versions_button = PushButton("同步本地版本")
        self.install_button = PrimaryPushButton("开始安装")
        self.refresh_install_versions_button.clicked.connect(self.refresh_install_versions)
        self.install_button.clicked.connect(self.start_install)
        install_row.addWidget(self.refresh_install_versions_button)
        install_row.addWidget(self.install_button)
        install_row.addStretch()
        install_layout.addLayout(install_row)
        install_layout.addWidget(self.install_status_label)
        install_layout.addWidget(self.install_metrics_label)
        install_layout.addWidget(self.install_log)

        modpack_card, modpack_layout = self.make_card("导入整合包", "支持 Modrinth .mrpack、CurseForge manifest 包和普通 zip 覆写包")
        modpack_layout.addWidget(CaptionLabel("Modrinth 包会下载 index 中声明的文件；CurseForge 包先导入 overrides，外部 Mod 下载后续补齐。"))
        modpack_row = QHBoxLayout()
        self.import_modpack_button = PrimaryPushButton("选择并导入整合包")
        self.import_modpack_button.clicked.connect(self.import_modpack)
        modpack_row.addWidget(self.import_modpack_button)
        modpack_row.addStretch()
        modpack_layout.addLayout(modpack_row)

        resource_card, resource_layout = self.make_card("资源市场", "从 Modrinth、CurseForge 或本地目录搜索资源")
        self.add_labeled_control(resource_layout, "来源", self.resource_source_combo)
        self.add_labeled_control(resource_layout, "资源类型", self.resource_type_combo)
        self.add_labeled_control(resource_layout, "安装到版本", self.resource_version_combo)
        self.add_labeled_control(resource_layout, "排序", self.resource_sort_combo)
        self.add_labeled_control(resource_layout, "关键词", self.resource_query_input)
        resource_layout.addWidget(self.resource_dependency_check)
        resource_row = QHBoxLayout()
        self.search_resource_button = PrimaryPushButton("搜索资源")
        self.install_resource_button = PushButton("安装选中资源")
        self.resource_detail_button = PushButton("查看详情")
        self.search_resource_button.clicked.connect(self.search_resources)
        self.install_resource_button.clicked.connect(self.install_selected_resource)
        self.resource_detail_button.clicked.connect(self.show_selected_resource_detail)
        resource_row.addWidget(self.search_resource_button)
        resource_row.addWidget(self.resource_detail_button)
        resource_row.addWidget(self.install_resource_button)
        resource_row.addStretch()
        resource_layout.addLayout(resource_row)
        resource_layout.addWidget(self.resource_status_label)
        resource_layout.addWidget(self.resource_result_list)
        detail_header = QHBoxLayout()
        detail_header.addWidget(CaptionLabel("资源详情"))
        detail_header.addWidget(self.resource_detail_loading)
        detail_header.addStretch()
        resource_layout.addLayout(detail_header)
        resource_layout.addWidget(self.resource_detail_view)

        vanilla_layout = QVBoxLayout(self.download_vanilla_view)
        vanilla_layout.setContentsMargins(0, 0, 0, 0)
        vanilla_layout.addWidget(download_card)
        vanilla_layout.addStretch()

        install_view_layout = QVBoxLayout(self.download_install_view)
        install_view_layout.setContentsMargins(0, 0, 0, 0)
        install_view_layout.addWidget(install_card)
        install_view_layout.addStretch()

        modpack_view_layout = QVBoxLayout(self.download_modpack_view)
        modpack_view_layout.setContentsMargins(0, 0, 0, 0)
        modpack_view_layout.addWidget(modpack_card)
        modpack_view_layout.addStretch()

        resource_view_layout = QVBoxLayout(self.download_resource_view)
        resource_view_layout.setContentsMargins(0, 0, 0, 0)
        resource_view_layout.addWidget(resource_card)
        resource_view_layout.addStretch()

        self.download_stack.addWidget(self.download_vanilla_view)
        self.download_stack.addWidget(self.download_install_view)
        self.download_stack.addWidget(self.download_modpack_view)
        self.download_stack.addWidget(self.download_resource_view)
        self.download_segment.addItem("vanilla", "下载原版", lambda: self.switch_download_section("vanilla", "获取游戏"))
        self.download_segment.addItem("addons", "安装扩展", lambda: self.switch_download_section("addons", "获取游戏"))
        self.download_segment.addItem("modpack", "导入整合包", lambda: self.switch_download_section("modpack", "导入内容"))
        self.download_segment.addItem("resources", "资源市场", lambda: self.switch_download_section("resources", "导入内容"))
        self.download_segment.setCurrentItem("vanilla")

        self.update_install_button_text(self.install_type_combo.currentText())
        self.update_download_addon_controls()
        self.update_resource_source_controls()

        self.download_page.layout.addWidget(progress_card)
        self.download_page.layout.addWidget(nav_card)
        self.download_stack.setVisible(False)
        self.download_page.layout.addWidget(self.download_stack)
        self.download_page.layout.addStretch()
        self.download_cards = [progress_card, nav_card, self.download_stack]
        self.open_download_overview()

    def build_account_section(self):
        nav_card = CardWidget()
        nav_layout = QVBoxLayout(nav_card)
        nav_layout.setContentsMargins(22, 18, 22, 18)
        nav_layout.setSpacing(12)
        nav_layout.addWidget(SubtitleLabel("账号操作"))
        nav_layout.addWidget(CaptionLabel("先选择账号目的，再进入对应账号方式"))
        self.account_segment = SegmentedWidget()
        self.account_segment.setVisible(False)
        nav_layout.addWidget(self.account_segment)
        self.account_overview_card = self.make_choice_card(
            "选择账号任务",
            "从账号管理目的开始，逐级缩小范围",
            [
                ("管理当前账号", "切换当前账号、删除账号或保存设置", lambda: self.open_account_section("overview", "账号管理")),
                ("新增或登录账号", "选择离线、Microsoft 或外置登录方式", lambda: self.open_account_category("新增或登录账号")),
            ],
        )
        self.account_category_card = self.make_choice_card(
            "选择登录方式",
            "选择一种账号类型继续",
            [],
        )
        nav_layout.addWidget(self.account_overview_card)
        nav_layout.addWidget(self.account_category_card)

        self.account_stack = QStackedWidget()
        self.account_overview_view = QWidget()
        self.account_offline_view = QWidget()
        self.account_microsoft_view = QWidget()
        self.account_external_view = QWidget()

        overview_card, overview_layout = self.make_card("当前账号", "集中处理当前使用账号、删除目标和全局保存")
        self.account_summary_label = BodyLabel("当前账号：未选择")
        self.add_labeled_control(overview_layout, "当前使用账号", self.manage_account_combo)
        self.add_labeled_control(overview_layout, "账号状态", self.account_summary_label)
        self.add_labeled_control(overview_layout, "删除目标账号", self.delete_account_combo)
        overview_row = QHBoxLayout()
        self.delete_account_button = PushButton("删除选中账号")
        save_button = PrimaryPushButton("保存设置")
        self.delete_account_button.clicked.connect(self.delete_selected_account)
        save_button.clicked.connect(self.save_settings)
        overview_row.addWidget(self.delete_account_button)
        overview_row.addWidget(save_button)
        overview_row.addStretch()
        overview_layout.addLayout(overview_row)

        offline_card, offline_layout = self.make_card("离线账号", "离线模式只需要用户名；高级模式下可补充 UUID 和 Access Token")
        self.username_row = self.add_labeled_control(offline_layout, "离线用户名", self.username_input)
        self.uuid_row = self.add_labeled_control(offline_layout, "UUID", self.uuid_input)
        self.access_token_row = self.add_labeled_control(offline_layout, "Access Token", self.access_token_input)
        offline_row = QHBoxLayout()
        self.add_offline_button = PushButton("添加/更新离线账号")
        self.add_offline_button.clicked.connect(self.add_offline_account)
        offline_row.addWidget(self.add_offline_button)
        offline_row.addStretch()
        offline_layout.addLayout(offline_row)

        microsoft_card, microsoft_layout = self.make_card("Microsoft 登录", "自动打开浏览器或复制链接手动登录都在这里处理")
        microsoft_layout.addWidget(self.auto_open_browser_check)
        self.microsoft_link_row = self.add_labeled_control(microsoft_layout, "登录链接", self.login_link_input)
        link_button_row = QHBoxLayout()
        login_button = PrimaryPushButton("添加 Microsoft 账号")
        copy_link_button = PushButton("复制登录链接")
        self.login_link_button.clicked.connect(self.open_login_link)
        login_button.clicked.connect(self.start_microsoft_login)
        copy_link_button.clicked.connect(self.copy_login_link)
        link_button_row.addWidget(login_button)
        link_button_row.addWidget(copy_link_button)
        link_button_row.addWidget(self.login_link_button)
        link_button_row.addStretch()
        microsoft_layout.addLayout(link_button_row)

        external_card, external_layout = self.make_card("外置登录", "适用于支持 Yggdrasil / Authlib-Injector 的皮肤站或私有验证服务器")
        self.external_server_row = self.add_labeled_control(external_layout, "认证服务器", self.external_server_input)
        self.external_username_row = self.add_labeled_control(external_layout, "用户名/邮箱", self.external_username_input)
        self.external_password_row = self.add_labeled_control(external_layout, "密码", self.external_password_input)
        self.authlib_injector_row = self.add_labeled_control(external_layout, "Authlib Injector", self.authlib_injector_input)
        external_layout.addWidget(self.external_status_label)
        external_row = QHBoxLayout()
        choose_injector_button = PushButton("选择 Jar")
        download_injector_button = PushButton("自动下载")
        probe_server_button = PushButton("测试服务器")
        self.refresh_external_button = PushButton("刷新/验证当前外置账号")
        add_external_button = PrimaryPushButton("登录并添加外置账号")
        choose_injector_button.clicked.connect(self.choose_authlib_injector)
        download_injector_button.clicked.connect(self.download_authlib_injector)
        probe_server_button.clicked.connect(self.probe_external_server)
        self.refresh_external_button.clicked.connect(self.refresh_current_external_account)
        add_external_button.clicked.connect(self.add_external_account)
        external_row.addWidget(choose_injector_button)
        external_row.addWidget(download_injector_button)
        external_row.addWidget(probe_server_button)
        external_row.addWidget(self.refresh_external_button)
        external_row.addWidget(add_external_button)
        external_row.addStretch()
        external_layout.addLayout(external_row)

        overview_view_layout = QVBoxLayout(self.account_overview_view)
        overview_view_layout.setContentsMargins(0, 0, 0, 0)
        overview_view_layout.addWidget(overview_card)
        overview_view_layout.addStretch()

        offline_view_layout = QVBoxLayout(self.account_offline_view)
        offline_view_layout.setContentsMargins(0, 0, 0, 0)
        offline_view_layout.addWidget(offline_card)
        offline_view_layout.addStretch()

        microsoft_view_layout = QVBoxLayout(self.account_microsoft_view)
        microsoft_view_layout.setContentsMargins(0, 0, 0, 0)
        microsoft_view_layout.addWidget(microsoft_card)
        microsoft_view_layout.addStretch()

        external_view_layout = QVBoxLayout(self.account_external_view)
        external_view_layout.setContentsMargins(0, 0, 0, 0)
        external_view_layout.addWidget(external_card)
        external_view_layout.addStretch()

        self.account_stack.addWidget(self.account_overview_view)
        self.account_stack.addWidget(self.account_offline_view)
        self.account_stack.addWidget(self.account_microsoft_view)
        self.account_stack.addWidget(self.account_external_view)
        self.account_segment.addItem("overview", "当前账号", lambda: self.open_account_section("overview", "账号管理"))
        self.account_segment.addItem("offline", "离线账号", lambda: self.open_account_section("offline", "新增或登录账号"))
        self.account_segment.addItem("microsoft", "Microsoft", lambda: self.open_account_section("microsoft", "新增或登录账号"))
        self.account_segment.addItem("external", "外置登录", lambda: self.open_account_section("external", "新增或登录账号"))
        self.account_segment.setCurrentItem("overview")

        container = QWidget()
        container_layout = QVBoxLayout(container)
        container_layout.setContentsMargins(0, 0, 0, 0)
        container_layout.setSpacing(18)
        container_layout.addWidget(nav_card)
        self.account_stack.setVisible(False)
        container_layout.addWidget(self.account_stack)
        self.account_category_card.setVisible(False)
        self.account_overview_card.setVisible(True)
        return container

    def build_environment_section(self):
        game_card, game_layout = self.make_card("环境与目录", "Java、游戏目录和隔离运行都放在这里")
        self.add_labeled_control(game_layout, "游戏目录", self.game_dir_input)
        game_layout.addWidget(self.advanced_mode_check)
        self.add_labeled_control(game_layout, "界面主题", self.theme_combo)
        self.add_labeled_control(game_layout, "主题背景图", self.theme_image_input)
        self.add_labeled_control(game_layout, "主页内容", self.home_content_input)
        game_layout.addWidget(self.home_network_check)
        game_layout.addWidget(self.resource_isolation_check)
        self.add_labeled_control(game_layout, "Java 路径", self.java_combo)
        game_layout.addWidget(self.java_version_label)
        game_row = QHBoxLayout()
        choose_button = PushButton("选择目录")
        open_button = PushButton("打开目录")
        theme_image_button = PushButton("选择背景图")
        home_content_button = PushButton("选择主页文件")
        refresh_home_button = PushButton("刷新主页")
        refresh_java_button = PushButton("刷新 Java")
        self.download_java_button = PushButton("下载推荐 Java")
        refresh_versions_button = PushButton("刷新本地版本")
        choose_button.clicked.connect(self.choose_game_directory)
        open_button.clicked.connect(self.open_game_directory)
        theme_image_button.clicked.connect(self.choose_theme_image)
        home_content_button.clicked.connect(self.choose_home_content)
        refresh_home_button.clicked.connect(self.refresh_home_content)
        refresh_java_button.clicked.connect(self.refresh_java_paths)
        self.download_java_button.clicked.connect(self.download_recommended_java)
        refresh_versions_button.clicked.connect(self.refresh_local_versions)
        game_row.addWidget(choose_button)
        game_row.addWidget(open_button)
        game_row.addWidget(theme_image_button)
        game_row.addWidget(home_content_button)
        game_row.addWidget(refresh_home_button)
        game_row.addWidget(refresh_java_button)
        game_row.addWidget(self.download_java_button)
        game_row.addWidget(refresh_versions_button)
        game_row.addStretch()
        game_layout.addLayout(game_row)
        return game_card

    def build_personalization_section(self):
        card, layout = self.make_card("个性化与功能", "背景音乐、页面隐藏和低频功能集中配置")
        layout.addWidget(self.music_enabled_check)
        self.add_labeled_control(layout, "背景音乐", self.music_path_input)
        self.add_labeled_control(layout, "音乐音量", self.music_volume_input)
        layout.addWidget(self.music_pause_on_launch_check)
        layout.addWidget(self.show_download_check)
        layout.addWidget(self.show_manage_check)
        row = QHBoxLayout()
        choose_music_button = PushButton("选择音乐")
        play_music_button = PushButton("播放/应用")
        stop_music_button = PushButton("停止音乐")
        choose_music_button.clicked.connect(self.choose_music_file)
        play_music_button.clicked.connect(self.apply_music_settings)
        stop_music_button.clicked.connect(self.stop_music)
        row.addWidget(choose_music_button)
        row.addWidget(play_music_button)
        row.addWidget(stop_music_button)
        row.addStretch()
        layout.addLayout(row)
        return card

    def build_server_section(self):
        card, layout = self.make_card("联机入口", "维护常用服务器地址，并可复制到剪贴板")
        layout.addWidget(self.server_list_input)
        row = QHBoxLayout()
        copy_button = PushButton("复制首个服务器")
        save_button = PushButton("保存服务器列表")
        copy_button.clicked.connect(self.copy_first_server)
        save_button.clicked.connect(self.save_settings)
        row.addWidget(copy_button)
        row.addWidget(save_button)
        row.addStretch()
        layout.addLayout(row)
        return card

    def build_log_section(self):
        card, layout = self.make_card("状态日志", "把下载、登录和启动日志集中到一个分页里，避免单独切主菜单")
        layout.addWidget(self.status_log)
        return card

    def build_help_section(self):
        card, layout = self.make_card("帮助与关于", "常见问题、目录说明、版本信息和鸣谢")
        help_text = TextEdit()
        help_text.setReadOnly(True)
        help_text.setAcceptRichText(False)
        help_text.setMinimumHeight(360)
        help_text.setPlainText(
            "常见问题\n"
            "1. 启动失败先检查 Java 版本是否满足当前 Minecraft 需求，再使用“补全/校验文件”。\n"
            "2. 下载失败会自动在 official 与 BMCLAPI 间切换；仍失败时可调低下载线程或切换镜像源。\n"
            "3. Fabric Mod 多数需要 Fabric API，可在下载页安装扩展或资源市场中安装依赖。\n"
            "4. 外置登录需要 authlib-injector.jar，并确保认证服务器地址可访问。\n\n"
            "目录说明\n"
            ".minecraft/versions 保存版本清单与客户端；libraries 保存依赖库；assets 保存资源文件。\n"
            "启用资源隔离后，存档、Mod、资源包、光影和截图会优先放入 versions/<版本名>/。\n\n"
            "关于\n"
            "McGo 是 PyQt6 / QFluentWidgets 编写的 Minecraft 启动器。\n"
            "鸣谢：Mojang 版本元数据、BMCLAPI 镜像、Modrinth、CurseForge、authlib-injector、QFluentWidgets。"
        )
        layout.addWidget(help_text)
        return card

    def build_manage_page(self):
        nav_card = CardWidget()
        nav_layout = QVBoxLayout(nav_card)
        nav_layout.setContentsMargins(22, 18, 22, 18)
        nav_layout.setSpacing(12)
        nav_layout.addWidget(SubtitleLabel("管理分区"))
        nav_layout.addWidget(CaptionLabel("先选择管理目标，再进入具体分区"))

        self.manage_pivot = Pivot()
        self.manage_pivot.setVisible(False)
        nav_layout.addWidget(self.manage_pivot)
        self.manage_overview_card = self.make_choice_card(
            "选择管理目标",
            "按目标逐级进入，减少平铺入口",
            [
                ("账号与登录", "管理当前账号，或新增离线 / Microsoft / 外置账号", lambda: self.open_manage_section("accounts", "账号与登录")),
                ("环境与界面", "游戏目录、Java、主题、主页、音乐和页面显示", lambda: self.open_manage_section("environment", "环境与界面")),
                ("诊断与帮助", "查看日志、常见问题和目录说明", lambda: self.open_manage_category("诊断与帮助")),
            ],
        )
        self.manage_category_card = self.make_choice_card(
            "选择诊断入口",
            "继续选择日志或帮助",
            [],
        )
        nav_layout.addWidget(self.manage_overview_card)
        nav_layout.addWidget(self.manage_category_card)

        self.manage_stack = QStackedWidget()
        self.account_manage_view = QWidget()
        self.environment_manage_view = QWidget()
        self.log_manage_view = QWidget()
        self.help_manage_view = QWidget()

        account_layout = QVBoxLayout(self.account_manage_view)
        account_layout.setContentsMargins(0, 0, 0, 0)
        account_layout.addWidget(self.build_account_section())
        account_layout.addStretch()

        environment_layout = QVBoxLayout(self.environment_manage_view)
        environment_layout.setContentsMargins(0, 0, 0, 0)
        environment_layout.addWidget(self.build_environment_section())
        environment_layout.addWidget(self.build_personalization_section())
        environment_layout.addWidget(self.build_server_section())
        environment_layout.addStretch()

        log_layout = QVBoxLayout(self.log_manage_view)
        log_layout.setContentsMargins(0, 0, 0, 0)
        log_layout.addWidget(self.build_log_section())
        log_layout.addStretch()

        help_layout = QVBoxLayout(self.help_manage_view)
        help_layout.setContentsMargins(0, 0, 0, 0)
        help_layout.addWidget(self.build_help_section())
        help_layout.addStretch()

        self.manage_stack.addWidget(self.account_manage_view)
        self.manage_stack.addWidget(self.environment_manage_view)
        self.manage_stack.addWidget(self.log_manage_view)
        self.manage_stack.addWidget(self.help_manage_view)

        self.manage_pivot.addItem("accounts", "账号", lambda: self.switch_manage_section("accounts", "账号与登录"))
        self.manage_pivot.addItem("environment", "环境", lambda: self.switch_manage_section("environment", "环境与界面"))
        self.manage_pivot.addItem("logs", "日志", lambda: self.switch_manage_section("logs", "诊断与帮助"))
        self.manage_pivot.addItem("help", "帮助", lambda: self.switch_manage_section("help", "诊断与帮助"))
        self.manage_pivot.setCurrentItem("accounts")

        self.manage_page.layout.addWidget(nav_card)
        self.manage_stack.setVisible(False)
        self.manage_page.layout.addWidget(self.manage_stack)
        self.manage_page.layout.addStretch()
        self.manage_cards = [nav_card, self.manage_stack]
        self.manage_category_card.setVisible(False)
        self.open_manage_overview()

    def switch_main_page(self, page, card_group=None):
        self.switchTo(page)
        self.animate_card_group(card_group or [])

    def open_manage_section(self, section_key, category=None):
        if not config.getboolean("FEATURES", "show_manage", fallback=True):
            self.show_warning("页面已隐藏", "请在配置文件中重新启用管理页。")
            return
        self.switch_main_page(self.manage_page, self.manage_cards)
        self.switch_manage_section(section_key, category)

    def open_download_section(self, section_key, category=None):
        if not config.getboolean("FEATURES", "show_download", fallback=True):
            self.show_warning("页面已隐藏", "请在管理中心重新启用下载页。")
            return
        self.switch_main_page(self.download_page, self.download_cards)
        self.switch_download_section(section_key, category)
        if section_key == "resources":
            self.refresh_resource_target_versions()

    def open_version_section(self, section_key, category=None):
        self.switch_main_page(self.launch_page, self.launch_cards)
        self.switch_version_section(section_key, category)

    def log(self, message):
        logger.info("UI: %s", message)
        self.status_log.append(message)
        self.motion.pulse_widget(self.status_log.viewport(), duration=220, start_opacity=0.66, throttle_key="status_log", min_interval=0.18)

    def log_install(self, message):
        logger.info("INSTALL UI: %s", message)
        self.install_log.append(message)
        self.motion.pulse_widget(self.install_log.viewport(), duration=220, start_opacity=0.66, throttle_key="install_log", min_interval=0.12)

    def apply_download_preset(self, preset_name):
        preset = DOWNLOAD_PRESETS.get(preset_name)
        if not preset:
            return
        self.download_core_threads_input.setValue(preset["core"])
        self.download_asset_threads_input.setValue(preset["asset"])
        self.download_speed_limit_input.setValue(preset["speed_kbps"])
        self.download_cache_combo.setCurrentText(preset["cache"])

    def refresh_home_content(self):
        if not hasattr(self, "home_custom_text"):
            return
        source = self.home_content_input.text().strip() if hasattr(self, "home_content_input") else ""
        if not source:
            self.home_custom_text.clear()
            if hasattr(self, "home_custom_card"):
                self.home_custom_card.setVisible(False)
            return
        if hasattr(self, "home_custom_card"):
            self.home_custom_card.setVisible(True)
        try:
            if source.startswith(("http://", "https://")):
                if not self.home_network_check.isChecked():
                    self.home_custom_text.setPlainText("联网主页未启用。")
                    return
                response = requests.get(source, timeout=8)
                response.raise_for_status()
                text = response.text
            else:
                with open(source, "r", encoding="utf-8", errors="replace") as file_handle:
                    text = file_handle.read()
            self.home_custom_text.setPlainText(text[:12000])
        except Exception as exc:
            self.home_custom_text.setPlainText(f"主页内容加载失败：{exc}")

    def choose_home_content(self):
        path, _ = QFileDialog.getOpenFileName(self, "选择主页文本", "", "文本文件 (*.txt *.md);;所有文件 (*.*)")
        if path:
            self.home_content_input.setText(path)
            self.refresh_home_content()

    def choose_music_file(self):
        path, _ = QFileDialog.getOpenFileName(self, "选择背景音乐", "", "音频文件 (*.mp3 *.wav *.ogg *.flac);;所有文件 (*.*)")
        if path:
            self.music_path_input.setText(path)
            self.apply_music_settings()

    def apply_music_settings(self, show_feedback=True):
        if not hasattr(self, "media_player"):
            return
        self.audio_output.setVolume(max(0, min(100, self.music_volume_input.value())) / 100)
        path = self.music_path_input.text().strip()
        if self.music_enabled_check.isChecked() and path and os.path.isfile(path):
            self.media_player.setSource(QUrl.fromLocalFile(os.path.abspath(path)))
            self.media_player.play()
            if show_feedback:
                self.show_success("背景音乐", "已开始播放。")
        else:
            self.media_player.stop()
            if show_feedback and self.music_enabled_check.isChecked():
                self.show_warning("背景音乐", "请选择有效的本地音乐文件。")

    def stop_music(self):
        self.media_player.stop()
        self.music_enabled_check.setChecked(False)

    def copy_first_server(self):
        for line in self.server_list_input.toPlainText().splitlines():
            server = line.split("|", 1)[0].strip()
            if server:
                QApplication.clipboard().setText(server)
                self.show_success("服务器已复制", server)
                return
        self.show_warning("没有服务器", "请先填写服务器地址。")

    def apply_feature_visibility(self):
        if not hasattr(self, "navigation_pages"):
            return
        states = {
            "download": config.getboolean("FEATURES", "show_download", fallback=True),
            "manage": config.getboolean("FEATURES", "show_manage", fallback=True),
        }
        labels = {"download": "下载", "manage": "管理"}
        icons = {"download": FluentIcon.DOWNLOAD, "manage": FluentIcon.SETTING}
        for key, visible in states.items():
            currently_visible = self.navigation_visible_keys.get(key, True)
            if visible == currently_visible:
                continue
            page = self.navigation_pages[key]
            navigation = getattr(self, "navigationInterface", None)
            if visible:
                try:
                    self.addSubInterface(page, icons[key], labels[key])
                    self.navigation_visible_keys[key] = True
                except Exception:
                    logger.exception("Failed to show navigation page: %s", key)
            elif navigation:
                try:
                    if hasattr(self, "stackedWidget") and self.stackedWidget.currentWidget() is page:
                        self.switchTo(self.home_page)
                    self.removeInterface(page)
                    self.navigation_visible_keys[key] = False
                except Exception:
                    logger.exception("Failed to hide navigation page: %s", key)

    def switch_main_page_if_ready(self, page, card_group=None):
        if hasattr(self, "navigation_pages"):
            self.switch_main_page(page, card_group or [])

    def open_version_overview(self):
        self.current_version_category = ""
        self.switch_main_page_if_ready(self.launch_page, self.launch_cards)
        if hasattr(self, "version_overview_card"):
            self.version_overview_card.setVisible(True)
        if hasattr(self, "version_stack"):
            self.version_stack.setVisible(False)
        self.update_breadcrumbs(self.launch_page)

    def open_download_overview(self):
        self.current_download_category = ""
        self.switch_main_page_if_ready(self.download_page, self.download_cards)
        if hasattr(self, "download_overview_card"):
            self.download_overview_card.setVisible(True)
        if hasattr(self, "download_category_card"):
            self.download_category_card.setVisible(False)
        if hasattr(self, "download_stack"):
            self.download_stack.setVisible(False)
        self.update_breadcrumbs(self.download_page)

    def open_download_category(self, category):
        self.switch_main_page_if_ready(self.download_page, self.download_cards)
        self.current_download_category = category
        choices = {
            "获取游戏": [
                ("下载原版", "刷新远程版本并下载 Minecraft", lambda: self.open_download_section("vanilla", category)),
                ("安装扩展", "为已有版本安装 Fabric、Forge、NeoForge、OptiFine 或 Fabric API", lambda: self.open_download_section("addons", category)),
            ],
            "导入内容": [
                ("导入整合包", "导入 Modrinth、CurseForge 或普通 zip 覆写包", lambda: self.open_download_section("modpack", category)),
                ("资源市场", "搜索并安装 Mod、资源包、光影或数据包", lambda: self.open_download_section("resources", category)),
            ],
        }.get(category, [])
        self.reset_choice_card(self.download_category_card, "选择具体任务", category, choices)
        self.download_overview_card.setVisible(False)
        self.download_category_card.setVisible(True)
        self.download_stack.setVisible(False)
        self.update_breadcrumbs(self.download_page)

    def open_manage_overview(self):
        self.current_manage_category = ""
        self.current_account_category = ""
        self.switch_main_page_if_ready(self.manage_page, self.manage_cards)
        if hasattr(self, "manage_overview_card"):
            self.manage_overview_card.setVisible(True)
        if hasattr(self, "manage_category_card"):
            self.manage_category_card.setVisible(False)
        if hasattr(self, "manage_stack"):
            self.manage_stack.setVisible(False)
        self.update_breadcrumbs(self.manage_page)

    def open_manage_category(self, category):
        self.switch_main_page_if_ready(self.manage_page, self.manage_cards)
        self.current_manage_category = category
        choices = {
            "诊断与帮助": [
                ("日志", "查看下载、登录和启动状态日志", lambda: self.open_manage_section("logs", category)),
                ("帮助", "查看常见问题、目录说明和关于信息", lambda: self.open_manage_section("help", category)),
            ],
        }.get(category, [])
        self.reset_choice_card(self.manage_category_card, "选择诊断入口", category, choices)
        self.manage_overview_card.setVisible(False)
        self.manage_category_card.setVisible(True)
        self.manage_stack.setVisible(False)
        self.update_breadcrumbs(self.manage_page)

    def open_account_category(self, category):
        self.switch_main_page_if_ready(self.manage_page, self.manage_cards)
        self.current_account_category = category
        choices = [
            ("离线账号", "只需要用户名即可添加", lambda: self.open_account_section("offline", category)),
            ("Microsoft", "通过 Microsoft OAuth 添加正版账号", lambda: self.open_account_section("microsoft", category)),
            ("外置登录", "使用 Yggdrasil / Authlib-Injector 服务器登录", lambda: self.open_account_section("external", category)),
        ]
        self.reset_choice_card(self.account_category_card, "选择登录方式", category, choices)
        self.account_overview_card.setVisible(False)
        self.account_category_card.setVisible(True)
        self.account_stack.setVisible(False)
        self.current_manage_section = "accounts"
        self.update_breadcrumbs(self.manage_page)

    def open_account_section(self, section_key, category=None):
        self.switch_main_page_if_ready(self.manage_page, self.manage_cards)
        if category is not None:
            self.current_account_category = category
        elif section_key == "overview":
            self.current_account_category = "账号管理"
        if hasattr(self, "account_overview_card"):
            self.account_overview_card.setVisible(False)
        if hasattr(self, "account_category_card"):
            self.account_category_card.setVisible(False)
        if hasattr(self, "account_stack"):
            self.account_stack.setVisible(True)
        self.current_manage_section = "accounts"
        self.switch_account_section(section_key)

    def switch_manage_section(self, section_key, category=None):
        mapping = {
            "accounts": 0,
            "environment": 1,
            "logs": 2,
            "help": 3,
        }
        section_key = section_key if section_key in mapping else "accounts"
        if category is not None:
            self.current_manage_category = category
        elif section_key == "accounts":
            self.current_manage_category = "账号与登录"
        elif section_key == "environment":
            self.current_manage_category = "环境与界面"
        self.current_manage_section = section_key
        if hasattr(self, "manage_overview_card"):
            self.manage_overview_card.setVisible(False)
        if hasattr(self, "manage_category_card"):
            self.manage_category_card.setVisible(False)
        if hasattr(self, "manage_stack"):
            self.manage_stack.setVisible(True)
        index = mapping.get(section_key, 0)
        self.motion.cross_fade_stack(self.manage_stack, index)
        self.manage_pivot.setCurrentItem(section_key)
        if section_key == "accounts" and hasattr(self, "account_overview_card") and not self.account_stack.isVisible():
            self.account_overview_card.setVisible(True)
            self.account_category_card.setVisible(False)
        self.update_breadcrumbs(self.manage_page)

    def switch_version_section(self, section_key, category=None):
        mapping = {
            "selector": 0,
            "settings": 1,
        }
        section_key = section_key if section_key in mapping else "selector"
        if category is not None:
            self.current_version_category = category
        elif not getattr(self, "current_version_category", ""):
            self.current_version_category = "本地版本" if section_key == "selector" else "版本维护"
        self.current_version_section = section_key
        if hasattr(self, "version_overview_card"):
            self.version_overview_card.setVisible(False)
        if hasattr(self, "version_stack"):
            self.version_stack.setVisible(True)
        index = mapping.get(section_key, 0)
        if hasattr(self, "version_stack"):
            self.motion.cross_fade_stack(self.version_stack, index)
        if hasattr(self, "version_segment"):
            self.version_segment.setCurrentItem(section_key if section_key in mapping else "selector")
        self.update_breadcrumbs(self.launch_page)

    def update_version_advanced_visibility(self):
        advanced = self.advanced_mode_check.isChecked() if hasattr(self, "advanced_mode_check") else False
        for name in (
            "version_min_memory_row",
            "version_window_row",
            "version_gc_row",
            "version_game_args_row",
            "version_pre_launch_row",
            "version_custom_dir_row",
        ):
            if hasattr(self, name):
                getattr(self, name).setVisible(advanced)
        if hasattr(self, "version_jvm_args_row"):
            self.version_jvm_args_row.setVisible(advanced)
        self.update_memory_controls()

    def update_download_advanced_visibility(self):
        advanced = self.advanced_mode_check.isChecked() if hasattr(self, "advanced_mode_check") else False
        for key in ("core", "asset"):
            if hasattr(self, "download_tuning_rows") and key in self.download_tuning_rows:
                self.download_tuning_rows[key].setVisible(advanced)
        if hasattr(self, "download_cache_row"):
            self.download_cache_row.setVisible(advanced)

    def update_memory_controls(self):
        if not hasattr(self, "version_memory_slider"):
            return
        manual = self.version_manual_memory_check.isChecked()
        self.version_memory_slider.setEnabled(manual)
        if hasattr(self, "version_memory_slider_row"):
            self.version_memory_slider_row.setEnabled(manual)
        value = self.version_memory_slider.value()
        if manual:
            self.version_memory_label.setText(f"最大内存：{value} MB")
        else:
            self.version_memory_label.setText(f"最大内存：自动（建议 {recommended_memory_mb()} MB）")

    def on_manual_memory_changed(self, *_):
        if self.version_manual_memory_check.isChecked() and self.version_memory_slider.value() < 1024:
            self.version_memory_slider.setValue(recommended_memory_mb())
        self.update_memory_controls()

    def on_memory_slider_changed(self, value):
        if value % 256:
            value = int(value // 256 * 256)
            self.version_memory_slider.blockSignals(True)
            self.version_memory_slider.setValue(value)
            self.version_memory_slider.blockSignals(False)
        self.update_memory_controls()

    def version_settings_entry(self, version_id):
        return version_settings_entry(self.version_settings, version_id)

    def version_display_name(self, version_id):
        return version_display_name(self.version_settings, version_id)

    def runtime_directory_for_version(self, version_id):
        return runtime_directory_for_version(
            self.current_game_dir(),
            self.version_settings,
            version_id,
            global_isolation=self.resource_isolation_check.isChecked(),
        )

    def base_version_for(self, version_id):
        return normalize_minecraft_version_for_api(self.current_game_dir(), version_id)

    def version_matches_category(self, version_id, category):
        return version_matches_category(self.current_game_dir(), version_id, category)

    def version_supports_mod_management(self, version_id):
        return bool(version_id and version_matches_category(self.current_game_dir(), version_id, "可安装 Mod"))

    def current_selected_version(self):
        return self.local_version_combo.currentText().strip()

    def set_selected_version(self, version_id, sync_display=True):
        version_id = (version_id or "").strip()
        if not version_id:
            return
        self.local_version_combo.blockSignals(True)
        if self.local_version_combo.findText(version_id) < 0:
            self.local_version_combo.addItem(version_id)
        self.local_version_combo.setCurrentText(version_id)
        self.local_version_combo.blockSignals(False)

        if sync_display:
            display_index = self.version_display_ids.index(version_id) if version_id in self.version_display_ids else -1
            if display_index >= 0 and self.version_display_combo.currentIndex() != display_index:
                self.version_display_combo.blockSignals(True)
                self.version_display_combo.setCurrentIndex(display_index)
                self.version_display_combo.blockSignals(False)

            list_index = self.version_list_ids.index(version_id) if version_id in self.version_list_ids else -1
            if list_index >= 0 and self.version_list.currentRow() != list_index:
                self.version_list.blockSignals(True)
                self.version_list.setCurrentRow(list_index)
                self.version_list.blockSignals(False)

        self.on_local_version_changed(version_id)

    def current_resource_version(self):
        current_index = self.resource_version_combo.currentIndex() if hasattr(self, "resource_version_combo") else -1
        if 0 <= current_index < len(self.resource_version_ids):
            return self.resource_version_ids[current_index]
        return self.current_selected_version()

    def version_supports_resource_type(self, version_id, resource_type):
        if resource_type == "mod":
            return self.version_supports_mod_management(version_id)
        return bool(version_id)

    def resource_directory_for_version(self, version_id, resource_type):
        return resource_directory_for_type(
            self.current_game_dir(),
            self.version_settings,
            version_id,
            resource_type,
            global_isolation=self.resource_isolation_check.isChecked(),
        )

    def preferred_version_id(self, candidates=None):
        candidates = list(candidates or [])
        last_version = self.version_settings.get("_meta", {}).get("last_launched_version", "")
        current_version = self.current_selected_version()
        for version_id in (last_version, current_version):
            if version_id and (not candidates or version_id in candidates):
                return version_id
        return candidates[0] if candidates else ""

    def refresh_resource_target_versions(self, versions=None):
        if not hasattr(self, "resource_version_combo"):
            return
        all_versions = list(versions) if versions is not None else [
            self.local_version_combo.itemText(index)
            for index in range(self.local_version_combo.count())
        ]
        resource_type = self.resource_type_combo.currentText().strip() if hasattr(self, "resource_type_combo") else "mod"
        filtered = [
            version_id for version_id in all_versions
            if self.version_supports_resource_type(version_id, resource_type)
        ]
        preferred = self.preferred_version_id(filtered)
        self.resource_version_combo.blockSignals(True)
        self.resource_version_combo.clear()
        self.resource_version_ids = list(filtered)
        self.resource_version_combo.addItems([self.version_display_name(version_id) for version_id in filtered])
        if preferred in filtered:
            self.resource_version_combo.setCurrentIndex(filtered.index(preferred))
        self.resource_version_combo.blockSignals(False)

    def on_version_display_selected(self, version_label):
        current_index = self.version_display_combo.currentIndex()
        version_id = self.version_display_ids[current_index] if 0 <= current_index < len(self.version_display_ids) else ""
        self.set_selected_version(version_id)

    def on_version_list_selected(self, current, _previous=None):
        if current is None:
            return
        version_id = current.data(Qt.ItemDataRole.UserRole)
        if isinstance(version_id, str):
            self.set_selected_version(version_id)

    def populate_version_settings_panel(self, version_id):
        entry = self.version_settings_entry(version_id)
        self.version_alias_input.setText(entry.get("alias", ""))
        self.version_jvm_args_input.setText(entry.get("jvm_args", ""))
        self.version_game_args_input.setText(entry.get("game_args", ""))
        self.version_pre_launch_input.setText(entry.get("pre_launch_command", ""))
        self.version_min_memory_input.setValue(max(0, int(entry.get("min_memory_mb", 0) or 0)))
        max_memory = max(0, int(entry.get("max_memory_mb", 0) or 0))
        self.version_manual_memory_check.setChecked(bool(entry.get("manual_memory", False) or max_memory))
        self.version_memory_slider.setValue(max(1024, min(self.version_memory_slider.maximum(), max_memory or recommended_memory_mb())))
        self.version_window_width_input.setValue(max(0, int(entry.get("window_width", 0) or 0)))
        self.version_window_height_input.setValue(max(0, int(entry.get("window_height", 0) or 0)))
        gc_strategy = entry.get("gc_strategy", "G1GC")
        self.version_gc_combo.setCurrentText(gc_strategy if gc_strategy in GC_STRATEGIES else "G1GC")
        self.version_custom_dir_input.setText(entry.get("runtime_directory", ""))
        self.version_favorite_check.setChecked(bool(entry.get("favorite", False)))
        self.version_hidden_check.setChecked(bool(entry.get("hidden", False)))
        icon = entry.get("icon", "自动")
        self.version_icon_combo.setCurrentText(icon if icon in VERSION_ICON_LABELS else "自动")
        forced_isolation = self.resource_isolation_check.isChecked()
        self.version_isolation_check.setChecked(bool(forced_isolation or entry.get("use_isolated_directory", False)))
        self.version_isolation_check.setEnabled(not forced_isolation)
        self.update_version_advanced_visibility()

        runtime_directory = self.runtime_directory_for_version(version_id)
        self.version_mods_list.clear()
        supports_mod_management = self.version_supports_mod_management(version_id)
        self.open_mods_button.setEnabled(supports_mod_management)
        self.toggle_mod_button.setEnabled(supports_mod_management)
        self.delete_mod_button.setEnabled(supports_mod_management)
        has_version = bool(version_id)
        self.open_version_folder_button.setEnabled(has_version)
        self.open_saves_button.setEnabled(has_version)
        self.open_resourcepacks_button.setEnabled(has_version)
        self.open_shaderpacks_button.setEnabled(has_version)
        self.open_screenshots_button.setEnabled(has_version)
        self.export_launch_script_button.setEnabled(has_version)
        self.export_modpack_button.setEnabled(has_version)
        self.analyze_crash_button.setEnabled(has_version)
        self.repair_version_button.setEnabled(has_version)
        self.delete_version_button.setEnabled(has_version)

        if supports_mod_management:
            self.mod_section_hint.setText("当前版本支持 Mod 管理，可以直接打开 mods 文件夹并启用、禁用或删除 Mod。")
            mods_dir = self.current_mods_directory()
            if os.path.isdir(mods_dir):
                for item in sorted(os.listdir(mods_dir)):
                    lowered = item.lower()
                    if not (lowered.endswith(".jar") or lowered.endswith(".jar.disabled")):
                        continue
                    enabled = lowered.endswith(".jar")
                    _, hints = analyze_local_mod_file(os.path.join(mods_dir, item), mods_dir)
                    hint_text = f" | {'；'.join(hints[:2])}" if hints else ""
                    label = f"{'启用' if enabled else '禁用'} | {item}{hint_text}"
                    widget_item = QListWidgetItem(label)
                    widget_item.setData(Qt.ItemDataRole.UserRole, os.path.join(mods_dir, item))
                    self.version_mods_list.addItem(widget_item)
            if self.version_mods_list.count() == 0:
                empty_item = QListWidgetItem("当前没有检测到 Mod 文件")
                empty_item.setFlags(Qt.ItemFlag.NoItemFlags)
                self.version_mods_list.addItem(empty_item)
            self.motion.pulse_list(self.version_mods_list)
        else:
            self.mod_section_hint.setText(f"当前版本类型为 {version_type_label(self.current_game_dir(), version_id)}，不支持 Mod 管理。请切换到 Fabric / Forge / NeoForge / OptiFine 版本。")
            empty_item = QListWidgetItem("当前版本不支持 Mod 管理")
            empty_item.setFlags(Qt.ItemFlag.NoItemFlags)
            self.version_mods_list.addItem(empty_item)

        summary = [
            f"当前版本：{self.version_display_name(version_id)}",
            f"基础版本：{self.base_version_for(version_id)}",
            f"运行目录：{runtime_directory}",
        ]
        summary.append(f"类型：{version_type_label(self.current_game_dir(), version_id)}")
        if entry.get("favorite"):
            summary.append("已收藏")
        if entry.get("hidden"):
            summary.append("已隐藏")
        self.version_summary_label.setText(" | ".join(summary))
        self.motion.fade_slide_in(self.version_summary_label, offset=8, duration=220)

    def save_current_version_settings(self):
        version_id = self.current_selected_version()
        if not version_id:
            self.show_warning("缺少版本", "请先选择一个本地版本。")
            return
        entry = self.version_settings_entry(version_id)
        entry["alias"] = self.version_alias_input.text().strip()
        entry["alias_auto"] = False
        entry["jvm_args"] = self.version_jvm_args_input.text().strip()
        entry["game_args"] = self.version_game_args_input.text().strip()
        entry["pre_launch_command"] = self.version_pre_launch_input.text().strip()
        entry["min_memory_mb"] = self.version_min_memory_input.value()
        entry["manual_memory"] = self.version_manual_memory_check.isChecked()
        entry["max_memory_mb"] = self.version_memory_slider.value() if self.version_manual_memory_check.isChecked() else 0
        entry["window_width"] = self.version_window_width_input.value()
        entry["window_height"] = self.version_window_height_input.value()
        entry["gc_strategy"] = self.version_gc_combo.currentText().strip()
        entry["runtime_directory"] = self.version_custom_dir_input.text().strip()
        entry["use_isolated_directory"] = self.version_isolation_check.isChecked()
        entry["favorite"] = self.version_favorite_check.isChecked()
        entry["hidden"] = self.version_hidden_check.isChecked()
        entry["icon"] = self.version_icon_combo.currentText().strip()
        save_version_settings(self.version_settings)
        self.refresh_local_versions(show_feedback=False)
        self.populate_version_settings_panel(version_id)
        self.show_success("版本设置已保存", self.version_display_name(version_id))

    def build_installed_version_alias(self, version_id, install_types):
        normalized_types = [item for item in install_types if item]
        detected = version_type_label(self.current_game_dir(), version_id)
        if "fabric_api" in normalized_types and detected in {"Fabric", "Forge", "NeoForge", "OptiFine"}:
            detected_key = detected.lower()
            if detected_key not in normalized_types:
                normalized_types.insert(0, detected_key)
        labels = [
            INSTALL_TYPE_LABELS.get(item, item)
            for item in normalized_types
            if item
        ]
        if not labels:
            if detected and detected != "原版":
                labels = [detected]
        if not labels:
            return ""
        base_version = self.base_version_for(version_id)
        return f"Minecraft {base_version} {' + '.join(labels)}"

    def apply_auto_version_alias(self, version_id, install_types):
        if not version_id:
            return ""
        entry = self.version_settings_entry(version_id)
        if (entry.get("alias") or "").strip() and not entry.get("alias_auto", False):
            return ""
        alias = self.build_installed_version_alias(version_id, install_types)
        if not alias:
            return ""
        entry["alias"] = alias
        entry["alias_auto"] = True
        save_version_settings(self.version_settings)
        self.log(f"已自动命名版本：{alias} [{version_id}]")
        return alias

    def current_mods_directory(self):
        version_id = self.current_selected_version()
        return mods_directory_for_version(
            self.current_game_dir(),
            self.version_settings,
            version_id,
            global_isolation=self.resource_isolation_check.isChecked(),
        )

    def current_version_directory(self):
        version_id = self.current_selected_version()
        return os.path.join(self.current_game_dir(), "versions", version_id) if version_id else ""

    def current_saves_directory(self):
        version_id = self.current_selected_version()
        return os.path.join(self.runtime_directory_for_version(version_id), "saves") if version_id else ""

    def current_resourcepack_directory(self):
        version_id = self.current_selected_version()
        return os.path.join(self.runtime_directory_for_version(version_id), "resourcepacks") if version_id else ""

    def current_shaderpack_directory(self):
        version_id = self.current_selected_version()
        return os.path.join(self.runtime_directory_for_version(version_id), "shaderpacks") if version_id else ""

    def current_screenshot_directory(self):
        version_id = self.current_selected_version()
        return os.path.join(self.runtime_directory_for_version(version_id), "screenshots") if version_id else ""

    def current_resource_directory(self, resource_type):
        return self.resource_directory_for_version(self.current_resource_version(), resource_type)

    def current_mod_item_path(self, item=None):
        current_item = item or self.version_mods_list.currentItem()
        if current_item is None:
            return ""
        mod_path = current_item.data(Qt.ItemDataRole.UserRole)
        return mod_path if isinstance(mod_path, str) else ""

    def on_version_mod_item_activated(self, item):
        if not self.current_mod_item_path(item):
            return
        self.toggle_selected_mod()

    def show_version_mod_context_menu(self, pos):
        menu = RoundMenu(parent=self.version_mods_list)
        item = self.version_mods_list.itemAt(pos)
        mod_path = self.current_mod_item_path(item)

        open_dir_action = Action(FluentIcon.FOLDER, "打开 mods 文件夹", triggered=self.open_current_mods_directory)
        menu.addAction(open_dir_action)

        if mod_path:
            if item is not self.version_mods_list.currentItem():
                self.version_mods_list.setCurrentItem(item)

            toggle_text = "启用 Mod" if mod_path.lower().endswith(".jar.disabled") else "禁用 Mod"
            toggle_action = Action(FluentIcon.SYNC, toggle_text, triggered=self.toggle_selected_mod)
            delete_action = Action(FluentIcon.DELETE, "删除 Mod", triggered=self.delete_selected_mod)
            menu.addSeparator()
            menu.addAction(toggle_action)
            menu.addAction(delete_action)

        menu.exec(self.version_mods_list.viewport().mapToGlobal(pos))

    def open_current_mods_directory(self):
        version_id = self.current_selected_version()
        if not version_id:
            self.show_warning("缺少版本", "请先选择一个本地版本。")
            return
        if not self.version_supports_mod_management(version_id):
            self.show_warning("不支持 Mod 管理", f"{self.version_display_name(version_id)} 不是可安装 Mod 的版本。")
            return
        mods_dir = self.current_mods_directory()
        os.makedirs(mods_dir, exist_ok=True)
        os.startfile(os.path.abspath(mods_dir))

    def open_current_version_folder(self):
        version_dir = self.current_version_directory()
        if not version_dir:
            self.show_warning("缺少版本", "请先选择一个本地版本。")
            return
        if not os.path.isdir(version_dir):
            self.show_warning("版本不存在", version_dir)
            return
        os.startfile(os.path.abspath(version_dir))

    def open_current_saves_directory(self):
        saves_dir = self.current_saves_directory()
        if not saves_dir:
            self.show_warning("缺少版本", "请先选择一个本地版本。")
            return
        os.makedirs(saves_dir, exist_ok=True)
        os.startfile(os.path.abspath(saves_dir))

    def open_current_resourcepacks_directory(self):
        path = self.current_resourcepack_directory()
        if not path:
            self.show_warning("缺少版本", "请先选择一个本地版本。")
            return
        os.makedirs(path, exist_ok=True)
        os.startfile(os.path.abspath(path))

    def open_current_shaderpacks_directory(self):
        path = self.current_shaderpack_directory()
        if not path:
            self.show_warning("缺少版本", "请先选择一个本地版本。")
            return
        os.makedirs(path, exist_ok=True)
        os.startfile(os.path.abspath(path))

    def open_current_screenshots_directory(self):
        path = self.current_screenshot_directory()
        if not path:
            self.show_warning("缺少版本", "请先选择一个本地版本。")
            return
        os.makedirs(path, exist_ok=True)
        os.startfile(os.path.abspath(path))

    def export_current_launch_script(self):
        version_id = self.current_selected_version()
        java_path = self.java_combo.currentText().strip()
        account = self.current_account()
        if not version_id:
            self.show_warning("缺少版本", "请先选择一个本地版本。")
            return
        if not java_path:
            self.show_warning("缺少 Java", "请先选择 Java 路径。")
            return
        if not account:
            self.show_warning("缺少账号", "请先选择一个账号。")
            return

        launch_options = launch_options_for_version(
            self.current_game_dir(),
            self.version_settings,
            version_id,
            global_isolation=self.resource_isolation_check.isChecked(),
        )
        if not launch_options.get("manual_memory"):
            launch_options["max_memory_mb"] = recommended_memory_mb()
            launch_options["min_memory_mb"] = 0
        try:
            extra_jvm_args = list(launch_options["extra_jvm_args"])
            if account.get("type") == "external":
                extra_jvm_args = [*authlib_injector_args(account), *extra_jvm_args]
            command = build_launch_command(
                java_path,
                version_id,
                game_directory=self.current_game_dir(),
                minecraft_access_token=account.get("access_token", ""),
                username=account.get("username", ""),
                uuid=account.get("uuid", ""),
                runtime_directory=launch_options["runtime_directory"],
                extra_jvm_args=extra_jvm_args,
                extra_game_args=launch_options.get("extra_game_args") or [],
                min_memory_mb=launch_options.get("min_memory_mb", 0),
                max_memory_mb=launch_options.get("max_memory_mb", 0),
                window_width=launch_options.get("window_width", 0),
                window_height=launch_options.get("window_height", 0),
                gc_strategy=launch_options.get("gc_strategy", "G1GC"),
            )
        except Exception as exc:
            self.show_warning("导出失败", str(exc))
            return

        script_dir = os.path.join(self.current_game_dir(), "mcgo_scripts")
        os.makedirs(script_dir, exist_ok=True)
        safe_name = re.sub(r'[<>:"/\\|?*]+', "_", version_id).strip() or "minecraft"
        script_path = os.path.join(script_dir, f"launch-{safe_name}.bat")
        with open(script_path, "w", encoding="utf-8-sig", newline="\r\n") as file_handle:
            file_handle.write("@echo off\r\n")
            file_handle.write("cd /d %~dp0\\..\r\n")
            pre_launch_command = launch_options.get("pre_launch_command", "")
            if pre_launch_command:
                file_handle.write(f"{pre_launch_command}\r\n")
            file_handle.write(f"{subprocess.list2cmdline(command)}\r\n")
            file_handle.write("pause\r\n")
        self.show_success("启动脚本已导出", script_path)
        self.log(f"已导出启动脚本：{script_path}")

    def export_current_modpack(self):
        version_id = self.current_resource_version()
        if not version_id:
            self.show_warning("缺少版本", "请先在资源市场里选择安装目标版本。")
            return

        default_name = sanitize_filename(self.version_display_name(version_id), version_id)
        target_path, selected_filter = QFileDialog.getSaveFileName(
            self,
            "导出整合包",
            os.path.join(self.current_game_dir(), f"{default_name}.mrpack"),
            "Modrinth 整合包 (*.mrpack);;CurseForge manifest 包 (*.zip);;普通 zip (*.zip)",
        )
        if not target_path:
            return
        selected_filter = selected_filter or ""
        if not os.path.splitext(target_path)[1]:
            target_path += ".mrpack" if "Modrinth" in selected_filter else ".zip"
        pack_format = "modrinth"
        if "CurseForge" in selected_filter:
            pack_format = "curseforge"
        elif "普通" in selected_filter:
            pack_format = "zip"

        try:
            added = export_modpack(
                self.current_game_dir(),
                self.version_settings,
                version_id,
                target_path,
                pack_format=pack_format,
                global_isolation=self.resource_isolation_check.isChecked(),
            )
        except Exception as exc:
            self.show_warning("导出失败", str(exc))
            self.log(f"整合包导出失败：{exc}")
            return

        self.show_success("导出完成", f"已写入 {added} 个文件：{target_path}")
        self.log(f"整合包已导出：{target_path}")

    def analyze_current_crash(self):
        version_id = self.current_resource_version()
        if not version_id:
            self.show_warning("缺少版本", "请先在资源市场里选择安装目标版本。")
            return
        runtime_dir = self.runtime_directory_for_version(version_id)
        result = analyze_crash_logs(self.current_game_dir(), version_id, runtime_dir)
        self.repair_status_label.setText("崩溃分析完成")
        self.repair_metrics_label.setText(result.splitlines()[0] if result else "无结果")
        self.log("崩溃分析结果：\n" + result)
        QMessageBox.information(self, "崩溃分析", result)

    def repair_current_version(self):
        if self.repair_thread and self.repair_thread.isRunning():
            self.show_warning("补全进行中", "当前已有补全任务在运行。")
            return

        version_id = self.current_resource_version()
        if not version_id:
            self.show_warning("缺少版本", "请先在资源市场里选择安装目标版本。")
            return
        mirror_key = self.mirror_combo.currentText()
        game_dir = self.current_game_dir()
        self.save_settings(show_feedback=False)
        task = DownloadTask(
            "repair",
            f"补全版本文件 {version_id}",
            lambda version_id=version_id,
            mirror_key=mirror_key,
            game_dir=game_dir: self._start_repair_task(version_id, mirror_key, game_dir),
        )
        self.queue_download_task(task)

    def _start_repair_task(self, version_id, mirror_key, game_dir):
        logger.info("Repair task starting from UI queue: version=%s mirror=%s game_dir=%s", version_id, mirror_key, game_dir)
        self.repair_progress_bar.setValue(0)
        self.repair_status_label.setText(f"准备补全 {version_id}...")
        self.repair_metrics_label.setText("正在校验版本完整性")
        self.set_download_running(True)
        self.show_info("开始补全", f"正在校验 {version_id} 的缺失文件。")
        self.repair_thread = QThread()
        self.repair_worker = RepairWorker(version_id, mirror_key, game_dir, download_options=read_download_options())
        self.repair_worker.moveToThread(self.repair_thread)
        self.repair_thread.started.connect(self.repair_worker.run)
        self.repair_worker.progress.connect(self.repair_progress_bar.setValue)
        self.repair_worker.metrics.connect(self.update_repair_metrics)
        self.repair_worker.status.connect(lambda message: self.repair_status_label.setText(message))
        self.repair_worker.finished.connect(self.on_repair_finished)
        self.repair_worker.failed.connect(self.on_repair_failed)
        self.repair_worker.finished.connect(self.repair_thread.quit)
        self.repair_worker.failed.connect(self.repair_thread.quit)
        self.repair_thread.start()

    def delete_current_version(self):
        version_id = self.current_selected_version()
        version_dir = self.current_version_directory()
        if not version_id or not version_dir:
            self.show_warning("缺少版本", "请先选择一个本地版本。")
            return
        if not os.path.isdir(version_dir):
            self.show_warning("版本不存在", version_dir)
            return
        reply = QMessageBox.question(
            self,
            "删除版本",
            f"确定要删除版本 {self.version_display_name(version_id)} 吗？\n\n将删除：{os.path.abspath(version_dir)}",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
            QMessageBox.StandardButton.No,
        )
        if reply != QMessageBox.StandardButton.Yes:
            return
        try:
            shutil.rmtree(version_dir)
            self.version_settings.pop(version_id, None)
            if self.version_settings.get("_meta", {}).get("last_launched_version") == version_id:
                self.version_settings["_meta"]["last_launched_version"] = ""
            save_version_settings(self.version_settings)
        except Exception as exc:
            self.show_warning("删除失败", str(exc))
            return
        self.refresh_local_versions(show_feedback=False)
        self.show_success("版本已删除", version_id)
        self.log(f"已删除版本：{version_id}")

    def toggle_selected_mod(self):
        version_id = self.current_selected_version()
        if not self.version_supports_mod_management(version_id):
            self.show_warning("不支持 Mod 管理", f"{self.version_display_name(version_id)} 不是可安装 Mod 的版本。")
            return
        mod_path = self.current_mod_item_path()
        if not mod_path or not os.path.isfile(mod_path):
            self.show_warning("缺少 Mod", "请先在列表中选择一个 Mod。")
            return

        if mod_path.lower().endswith(".jar.disabled"):
            target_path = mod_path[:-9]
        else:
            target_path = f"{mod_path}.disabled"
        os.replace(mod_path, target_path)
        self.populate_version_settings_panel(self.current_selected_version())
        self.show_success("Mod 状态已更新", os.path.basename(target_path))

    def delete_selected_mod(self):
        version_id = self.current_selected_version()
        if not self.version_supports_mod_management(version_id):
            self.show_warning("不支持 Mod 管理", f"{self.version_display_name(version_id)} 不是可安装 Mod 的版本。")
            return
        mod_path = self.current_mod_item_path()
        if not mod_path or not os.path.isfile(mod_path):
            self.show_warning("缺少 Mod", "请先在列表中选择一个 Mod。")
            return

        os.remove(mod_path)
        self.populate_version_settings_panel(self.current_selected_version())
        self.show_success("Mod 已删除", os.path.basename(mod_path))

    def format_bytes(self, byte_count):
        units = ["B", "KB", "MB", "GB"]
        value = float(max(0, byte_count))
        for unit in units:
            if value < 1024 or unit == units[-1]:
                return f"{value:.1f} {unit}" if unit != "B" else f"{int(value)} {unit}"
            value /= 1024

    def update_download_metrics(self, snapshot):
        progress = snapshot.get("progress", 0.0) * 100
        phase = snapshot.get("phase", "下载中")
        current_file = snapshot.get("current_file", "")
        completed_files = snapshot.get("completed_files", 0)
        total_files = snapshot.get("total_files", 0)
        reused_files = snapshot.get("reused_files", 0)
        downloaded_bytes = snapshot.get("downloaded_bytes", 0)
        total_bytes = snapshot.get("total_bytes", 0)
        speed_bytes = snapshot.get("speed_bytes", 0)

        details = (
            f"{phase} | {progress:.1f}% | "
            f"{self.format_bytes(downloaded_bytes)} / {self.format_bytes(total_bytes)} | "
            f"{self.format_bytes(speed_bytes)}/s | "
            f"{completed_files}/{total_files} 个文件"
        )
        if reused_files:
            details += f" | 复用 {reused_files} 个"
        if current_file:
            details += f" | 当前: {current_file}"
        self.download_metrics_label.setText(details)
        self.motion.pulse_widget(self.download_metrics_label, duration=210, start_opacity=0.5, throttle_key="download_metrics", min_interval=0.18)

    def update_repair_metrics(self, snapshot):
        progress = snapshot.get("progress", 0.0) * 100
        phase = snapshot.get("phase", "补全中")
        current_file = snapshot.get("current_file", "")
        completed_files = snapshot.get("completed_files", 0)
        total_files = snapshot.get("total_files", 0)
        reused_files = snapshot.get("reused_files", 0)
        downloaded_bytes = snapshot.get("downloaded_bytes", 0)
        total_bytes = snapshot.get("total_bytes", 0)
        speed_bytes = snapshot.get("speed_bytes", 0)

        self.repair_status_label.setText(f"{phase} | {progress:.1f}%")
        details = (
            f"{self.format_bytes(downloaded_bytes)} / {self.format_bytes(total_bytes)} | "
            f"{self.format_bytes(speed_bytes)}/s | "
            f"{completed_files}/{total_files} 个文件"
        )
        if reused_files:
            details += f" | 复用 {reused_files} 个"
        if current_file:
            details += f" | 当前: {current_file}"
        self.repair_metrics_label.setText(details)
        self.motion.pulse_widget(self.repair_status_label, duration=210, start_opacity=0.5, throttle_key="repair_status", min_interval=0.18)

    def update_install_metrics(self, snapshot):
        progress = snapshot.get("progress", 0.0) * 100
        phase = snapshot.get("phase", "安装中")
        current_file = snapshot.get("current_file", "")
        completed_files = snapshot.get("completed_files", 0)
        total_files = snapshot.get("total_files", 0)
        reused_files = snapshot.get("reused_files", 0)
        downloaded_bytes = snapshot.get("downloaded_bytes", 0)
        total_bytes = snapshot.get("total_bytes", 0)
        speed_bytes = snapshot.get("speed_bytes", 0)

        details = (
            f"{phase} | {progress:.1f}% | "
            f"{self.format_bytes(downloaded_bytes)} / {self.format_bytes(total_bytes)} | "
            f"{self.format_bytes(speed_bytes)}/s | "
            f"{completed_files}/{total_files} 个文件"
        )
        if reused_files:
            details += f" | 复用 {reused_files} 个"
        if current_file:
            details += f" | 当前: {current_file}"
        self.install_metrics_label.setText(details)
        self.motion.pulse_widget(self.install_metrics_label, duration=210, start_opacity=0.5, throttle_key="install_metrics", min_interval=0.14)

    def update_resource_install_metrics(self, snapshot):
        progress = snapshot.get("progress", 0.0) * 100
        phase = snapshot.get("phase", "下载资源")
        current_file = snapshot.get("current_file", "")
        downloaded_bytes = snapshot.get("downloaded_bytes", 0)
        total_bytes = snapshot.get("total_bytes", 0)
        speed_bytes = snapshot.get("speed_bytes", 0)
        details = (
            f"{phase} | {progress:.1f}% | "
            f"{self.format_bytes(downloaded_bytes)} / {self.format_bytes(total_bytes)} | "
            f"{self.format_bytes(speed_bytes)}/s"
        )
        if current_file:
            details += f" | 当前: {current_file}"
        self.resource_status_label.setText(details)
        self.download_metrics_label.setText(details)
        self.motion.pulse_widget(self.resource_status_label, duration=210, start_opacity=0.5, throttle_key="resource_install_metrics", min_interval=0.16)

    def is_download_task_running(self):
        threads = (
            self.download_thread,
            self.install_thread,
            self.modpack_thread,
            self.resource_install_thread,
            self.repair_thread,
        )
        return any(thread and thread.isRunning() for thread in threads)

    def queue_download_task(self, task):
        if self.is_download_task_running() or self.active_download_task:
            self.download_task_queue.append(task)
            logger.info(
                "Download task queued: type=%s title=%s queue_size=%d active=%s",
                task.task_type,
                task.title,
                len(self.download_task_queue),
                self.active_download_task.title if self.active_download_task else "<thread-running>",
            )
            self.update_download_queue_label()
            self.show_info("已加入队列", task.title)
            return
        logger.info("Download task starting immediately: type=%s title=%s", task.task_type, task.title)
        self.start_download_task(task)

    def start_download_task(self, task):
        self.active_download_task = task
        self.canceling_download_task = False
        logger.info("Download task started: type=%s title=%s", task.task_type, task.title)
        self.update_download_queue_label()
        task.start()

    def finish_download_task(self, failed=False):
        task = self.active_download_task
        if failed and self.active_download_task and not self.canceling_download_task:
            self.last_failed_download_task = self.active_download_task
        logger.info(
            "Download task finished: type=%s title=%s failed=%s canceled=%s remaining_queue=%d",
            task.task_type if task else "<none>",
            task.title if task else "<none>",
            failed,
            self.canceling_download_task,
            len(self.download_task_queue),
        )
        self.active_download_task = None
        self.canceling_download_task = False
        self.update_download_queue_label()
        QTimer.singleShot(0, self.start_next_download_task)

    def start_next_download_task(self):
        if self.is_download_task_running():
            if self.download_task_queue and not self.active_download_task:
                logger.debug("Download task thread still running; retrying queue dispatch soon")
                QTimer.singleShot(200, self.start_next_download_task)
            self.update_download_queue_label()
            return
        if self.active_download_task or not self.download_task_queue:
            self.update_download_queue_label()
            return
        logger.info("Dispatching next queued download task: queue_size=%d", len(self.download_task_queue))
        self.start_download_task(self.download_task_queue.popleft())

    def update_download_queue_label(self):
        if not hasattr(self, "download_queue_label"):
            return
        active = self.active_download_task.title if self.active_download_task else "无"
        queued = len(self.download_task_queue)
        next_title = self.download_task_queue[0].title if self.download_task_queue else "无"
        self.download_queue_label.setText(f"当前任务：{active} | 队列：{queued} | 下一个：{next_title}")
        if hasattr(self, "cancel_task_button"):
            self.cancel_task_button.setEnabled(self.is_download_task_running())
        if hasattr(self, "retry_task_button"):
            self.retry_task_button.setEnabled(self.last_failed_download_task is not None and not self.is_download_task_running())
        if hasattr(self, "clear_queue_button"):
            self.clear_queue_button.setEnabled(bool(self.download_task_queue))

    def clear_download_queue(self):
        count = len(self.download_task_queue)
        self.download_task_queue.clear()
        logger.info("Download task queue cleared: removed=%d", count)
        self.update_download_queue_label()
        self.show_info("队列已清空", f"已移除 {count} 个等待任务。")

    def retry_last_failed_download_task(self):
        if not self.last_failed_download_task:
            self.show_warning("没有失败任务", "当前没有可以重试的下载任务。")
            return
        task = self.last_failed_download_task
        self.last_failed_download_task = None
        logger.info("Retrying failed download task: type=%s title=%s", task.task_type, task.title)
        self.queue_download_task(task)
        self.update_download_queue_label()

    def cancel_current_download_task(self):
        if not self.is_download_task_running():
            self.show_warning("没有运行任务", "当前没有可取消的下载任务。")
            return
        self.canceling_download_task = True
        logger.warning(
            "Canceling current download task: active=%s queue_size=%d",
            self.active_download_task.title if self.active_download_task else "<none>",
            len(self.download_task_queue),
        )
        stopped = []
        for name in ("download", "install", "modpack", "resource_install", "repair"):
            thread = getattr(self, f"{name}_thread", None)
            if thread and thread.isRunning():
                thread.requestInterruption()
                thread.quit()
                if not thread.wait(1500):
                    thread.terminate()
                    thread.wait(1500)
                stopped.append(name)
        self.set_download_running(False)
        self.progress_bar.setValue(0)
        self.download_metrics_label.setText("任务已取消")
        self.install_status_label.setText("当前任务已取消")
        self.log(f"已取消任务：{', '.join(stopped) if stopped else '未知'}")
        self.finish_download_task(failed=False)

    def set_download_running(self, running):
        if hasattr(self, "download_button"):
            self.download_button.setEnabled(not running)
        if hasattr(self, "refresh_remote_button"):
            self.refresh_remote_button.setEnabled(not running)
        if hasattr(self, "install_button"):
            self.install_button.setEnabled(not running)
        if hasattr(self, "refresh_install_versions_button"):
            self.refresh_install_versions_button.setEnabled(not running)
        if hasattr(self, "import_modpack_button"):
            self.import_modpack_button.setEnabled(not running)
        if hasattr(self, "search_resource_button"):
            self.search_resource_button.setEnabled(not running)
        if hasattr(self, "resource_detail_button"):
            self.resource_detail_button.setEnabled(not running)
        if hasattr(self, "install_resource_button"):
            self.install_resource_button.setEnabled(not running)
            if not running:
                self.update_resource_source_controls()
        if hasattr(self, "resource_version_combo"):
            self.resource_version_combo.setEnabled(not running)
        if hasattr(self, "repair_version_button"):
            self.repair_version_button.setEnabled(not running)
        if hasattr(self, "export_modpack_button"):
            self.export_modpack_button.setEnabled(not running)
        if hasattr(self, "analyze_crash_button"):
            self.analyze_crash_button.setEnabled(not running)
        if hasattr(self, "install_type_combo"):
            self.install_type_combo.setEnabled(not running)
        if hasattr(self, "install_version_combo"):
            self.install_version_combo.setEnabled(not running)
        if hasattr(self, "download_java_button"):
            self.download_java_button.setEnabled(not running)
        if hasattr(self, "download_install_combo"):
            self.download_install_combo.setEnabled(not running)
        if hasattr(self, "download_fabric_api_check"):
            self.download_fabric_api_check.setEnabled(not running)
        self.update_download_addon_controls()
        for name in (
            "download_preset_combo",
            "download_core_threads_input",
            "download_asset_threads_input",
            "download_speed_limit_input",
            "download_cache_combo",
        ):
            if hasattr(self, name):
                getattr(self, name).setEnabled(not running)
        self.update_download_queue_label()

    def set_launch_running(self, running):
        if hasattr(self, "launch_button"):
            self.launch_button.setEnabled(not running)
        self.java_combo.setEnabled(not running)
        self.local_version_combo.setEnabled(not running)
        self.account_combo.setEnabled(not running)

    def show_success(self, title, content):
        InfoBar.success(title, content, duration=2500, position=InfoBarPosition.TOP_RIGHT, parent=self)

    def show_info(self, title, content):
        InfoBar.info(title, content, duration=2200, position=InfoBarPosition.TOP_RIGHT, parent=self)

    def show_warning(self, title, content):
        InfoBar.warning(title, content, duration=3500, position=InfoBarPosition.TOP_RIGHT, parent=self)

    def current_account(self):
        for account in self.accounts:
            if account.get("id") == self.selected_account_id:
                return account
        return None

    def current_delete_account(self):
        current_index = self.delete_account_combo.currentIndex()
        account_id = self.delete_account_index_ids[current_index] if 0 <= current_index < len(self.delete_account_index_ids) else ""
        for account in self.accounts:
            if account.get("id") == account_id:
                return account
        return None

    def find_duplicate_account(self, account_type, username="", uuid="", exclude_id=None):
        normalized_username = username.strip().lower()
        normalized_uuid = uuid.strip().lower()
        for account in self.accounts:
            if exclude_id and account.get("id") == exclude_id:
                continue
            if account.get("type") != account_type:
                continue

            existing_username = account.get("username", "").strip().lower()
            existing_uuid = account.get("uuid", "").strip().lower()
            if normalized_username and existing_username == normalized_username:
                return account
            if account_type == "microsoft" and normalized_uuid and existing_uuid == normalized_uuid:
                return account
        return None

    def refresh_account_selector(self):
        self.account_combo.blockSignals(True)
        self.manage_account_combo.blockSignals(True)
        self.delete_account_combo.blockSignals(True)
        self.account_combo.clear()
        self.manage_account_combo.clear()
        self.delete_account_combo.clear()
        self.account_index_ids = []
        self.manage_account_index_ids = []
        self.delete_account_index_ids = []
        for account in self.accounts:
            account_id = account.get("id", "")
            self.account_combo.addItem(account_label(account))
            self.manage_account_combo.addItem(account_label(account))
            self.delete_account_combo.addItem(account_label(account))
            self.account_index_ids.append(account_id)
            self.manage_account_index_ids.append(account_id)
            self.delete_account_index_ids.append(account_id)

        if self.accounts:
            selected_index = 0
            for index, account in enumerate(self.accounts):
                if account.get("id") == self.selected_account_id:
                    selected_index = index
                    break
            self.account_combo.setCurrentIndex(selected_index)
            self.manage_account_combo.setCurrentIndex(selected_index)
            self.delete_account_combo.setCurrentIndex(selected_index)
            self.selected_account_id = self.accounts[selected_index].get("id", "")
            config["ACCOUNTS"]["selected_account_id"] = self.selected_account_id or ""
            save_config()
        else:
            self.selected_account_id = ""
            config["ACCOUNTS"]["selected_account_id"] = ""
            save_config()
        self.account_combo.blockSignals(False)
        self.manage_account_combo.blockSignals(False)
        self.delete_account_combo.blockSignals(False)
        self.populate_selected_account_fields()
        self.update_home_summary()

    def on_account_selected(self, *_):
        current_index = self.account_combo.currentIndex()
        account_id = self.account_index_ids[current_index] if 0 <= current_index < len(self.account_index_ids) else ""
        self.sync_selected_account(account_id or "")

    def on_manage_account_selected(self, *_):
        current_index = self.manage_account_combo.currentIndex()
        account_id = self.manage_account_index_ids[current_index] if 0 <= current_index < len(self.manage_account_index_ids) else ""
        self.sync_selected_account(account_id or "")

    def sync_selected_account(self, account_id):
        self.selected_account_id = account_id or ""
        self.account_combo.blockSignals(True)
        self.manage_account_combo.blockSignals(True)

        launch_index = self.account_index_ids.index(self.selected_account_id) if self.selected_account_id in self.account_index_ids else -1
        manage_index = self.manage_account_index_ids.index(self.selected_account_id) if self.selected_account_id in self.manage_account_index_ids else -1
        if launch_index >= 0:
            self.account_combo.setCurrentIndex(launch_index)
        if manage_index >= 0:
            self.manage_account_combo.setCurrentIndex(manage_index)

        self.account_combo.blockSignals(False)
        self.manage_account_combo.blockSignals(False)
        config["ACCOUNTS"]["selected_account_id"] = self.selected_account_id
        save_config()
        self.populate_selected_account_fields()
        self.update_home_summary()
        if hasattr(self, "account_summary_label"):
            self.motion.fade_slide_in(self.account_summary_label, offset=10, duration=210)

    def populate_selected_account_fields(self):
        account = self.current_account()
        if not account:
            self.update_account_field_visibility()
            return
        self.login_mode_combo.setCurrentText(account.get("type", "offline"))
        self.username_input.setText(account.get("username", ""))
        self.uuid_input.setText(account.get("uuid", ""))
        self.access_token_input.setText(account.get("access_token", ""))
        if account.get("type") == "external":
            self.external_server_input.setText(account.get("auth_server", ""))
            self.external_username_input.setText(account.get("username", ""))
            self.authlib_injector_input.setText(account.get("authlib_injector_path", ""))
            self.external_status_label.setText(f"当前外置账号：{account_label(account)}")
        self.update_account_field_visibility()

    def update_account_field_visibility(self, *_):
        account = self.current_account()
        selected_mode = self.login_mode_combo.currentText()
        account_type = selected_mode if selected_mode in {"offline", "external"} else (account.get("type") if account else selected_mode)
        is_offline = account_type == "offline"
        is_microsoft = account_type == "microsoft"
        is_external = account_type == "external"
        advanced = self.advanced_mode_check.isChecked() if hasattr(self, "advanced_mode_check") else False

        if hasattr(self, "username_row"):
            self.username_row.setVisible(is_offline)
            self.add_offline_button.setVisible(is_offline)
            self.uuid_row.setVisible(is_offline and advanced)
            self.access_token_row.setVisible(is_offline and advanced)
        if hasattr(self, "auto_open_browser_check"):
            self.auto_open_browser_check.setVisible(is_microsoft)
        if hasattr(self, "microsoft_link_row"):
            self.microsoft_link_row.setVisible(is_microsoft)
        if hasattr(self, "login_link_button"):
            has_link = bool(self.login_link_input.text().strip()) if hasattr(self, "login_link_input") else False
            self.login_link_button.setVisible(is_microsoft and has_link)
        for row_name in ("external_server_row", "external_username_row", "external_password_row", "authlib_injector_row"):
            if hasattr(self, row_name):
                getattr(self, row_name).setVisible(is_external)
        if hasattr(self, "account_segment"):
            target = "external" if is_external else ("offline" if is_offline else "microsoft")
            current_item = self.account_segment.currentItem()
            current_route = getattr(current_item, "routeKey", "") if current_item else ""
            if callable(current_route):
                current_route = current_route()
            if current_route in {"offline", "microsoft", "external"}:
                self.open_account_section(target, "新增或登录账号")

    def switch_download_section(self, section_key, category=None):
        mapping = {
            "vanilla": 0,
            "addons": 1,
            "modpack": 2,
            "resources": 3,
        }
        section_key = section_key if section_key in mapping else "vanilla"
        if category is not None:
            self.current_download_category = category
        elif not getattr(self, "current_download_category", ""):
            self.current_download_category = "获取游戏" if section_key in {"vanilla", "addons"} else "导入内容"
        self.current_download_section = section_key
        if hasattr(self, "download_overview_card"):
            self.download_overview_card.setVisible(False)
        if hasattr(self, "download_category_card"):
            self.download_category_card.setVisible(False)
        if hasattr(self, "download_stack"):
            self.download_stack.setVisible(True)
        index = mapping.get(section_key, 0)
        if hasattr(self, "download_stack"):
            self.motion.cross_fade_stack(self.download_stack, index)
        if hasattr(self, "download_segment"):
            self.download_segment.setCurrentItem(section_key if section_key in mapping else "vanilla")
        self.update_breadcrumbs(self.download_page)

    def switch_account_section(self, section_key):
        mapping = {
            "overview": 0,
            "offline": 1,
            "microsoft": 2,
            "external": 3,
        }
        section_key = section_key if section_key in mapping else "overview"
        self.current_account_section = section_key
        index = mapping.get(section_key, 0)
        if section_key in {"offline", "microsoft", "external"} and hasattr(self, "login_mode_combo"):
            self.login_mode_combo.blockSignals(True)
            self.login_mode_combo.setCurrentText(section_key)
            self.login_mode_combo.blockSignals(False)
        if hasattr(self, "account_stack"):
            self.motion.cross_fade_stack(self.account_stack, index)
        if hasattr(self, "account_segment"):
            self.account_segment.setCurrentItem(section_key if section_key in mapping else "overview")
        self.update_account_field_visibility()
        self.update_breadcrumbs(self.manage_page)

    def on_advanced_mode_changed(self, *_):
        config["UI"]["advanced_mode"] = str(self.advanced_mode_check.isChecked())
        save_config()
        self.update_account_field_visibility()
        self.update_version_advanced_visibility()
        self.update_download_advanced_visibility()

    def apply_theme(self, theme_name):
        normalized = (theme_name or "dark").strip().lower()
        if normalized == "light":
            setTheme(Theme.LIGHT)
        elif normalized == "auto":
            setTheme(Theme.AUTO)
        else:
            setTheme(Theme.DARK)

    def on_theme_changed(self, theme_name):
        config["UI"]["theme"] = theme_name or "dark"
        save_config()
        self.apply_theme(theme_name)

    def apply_theme_image(self):
        path = self.theme_image_input.text().strip() if hasattr(self, "theme_image_input") else ""
        if not path or not os.path.isfile(path):
            self.setStyleSheet("""
                Page, QWidget#homePage, QWidget#launchPage, QWidget#downloadPage, QWidget#settingsPage, QWidget#logPage {
                    background: transparent;
                }
                CardWidget {
                    border-radius: 12px;
                }
            """)
            return
        normalized = os.path.abspath(path).replace("\\", "/")
        self.setStyleSheet(f"""
            FluentWindow {{
                border-image: url("{normalized}") 0 0 0 0 stretch stretch;
            }}
            Page, QWidget#homePage, QWidget#launchPage, QWidget#downloadPage, QWidget#settingsPage, QWidget#logPage {{
                background: transparent;
            }}
            CardWidget {{
                border-radius: 12px;
            }}
        """)

    def choose_theme_image(self):
        image_path, _ = QFileDialog.getOpenFileName(
            self,
            "选择主题背景图",
            "",
            "图片文件 (*.png *.jpg *.jpeg *.webp *.bmp);;所有文件 (*.*)",
        )
        if image_path:
            self.theme_image_input.setText(image_path)
            config["UI"]["theme_image"] = image_path
            save_config()
            self.apply_theme_image()

    def upsert_account(self, account):
        for index, existing in enumerate(self.accounts):
            if existing.get("id") == account.get("id"):
                self.accounts[index] = account
                break
        else:
            self.accounts.append(account)
        self.selected_account_id = account.get("id", "")
        save_accounts(self.accounts)
        self.refresh_account_selector()

    def add_offline_account(self):
        username = self.username_input.text().strip()
        if not username:
            self.show_warning("缺少用户名", "添加离线账号需要填写用户名。")
            return

        current = self.current_account()
        account_id = current.get("id") if current and current.get("type") == "offline" else str(uuidlib.uuid4())
        duplicate = self.find_duplicate_account("offline", username, exclude_id=account_id)
        if duplicate:
            self.selected_account_id = duplicate.get("id", "")
            self.refresh_account_selector()
            self.show_warning("重复用户名", f"离线账号 {username} 已存在，未重复添加。")
            return

        self.upsert_account({
            "id": account_id,
            "type": "offline",
            "display_name": username,
            "username": username,
            "uuid": self.uuid_input.text().strip(),
            "access_token": self.access_token_input.text().strip(),
            "refresh_token": "",
        })
        self.show_success("账号已保存", f"离线账号 {username} 已保存。")

    def choose_authlib_injector(self):
        jar_path, _ = QFileDialog.getOpenFileName(
            self,
            "选择 authlib-injector.jar",
            "",
            "Jar 文件 (*.jar);;所有文件 (*.*)",
        )
        if jar_path:
            self.authlib_injector_input.setText(jar_path)

    def download_authlib_injector(self):
        if self.authlib_download_thread and self.authlib_download_thread.isRunning():
            self.show_warning("下载进行中", "authlib-injector 正在下载。")
            return
        install_dir = os.path.join(self.current_game_dir(), "authlib-injector")
        self.authlib_download_thread = QThread()
        self.authlib_download_worker = AuthlibInjectorDownloadWorker(install_dir)
        self.authlib_download_worker.moveToThread(self.authlib_download_thread)
        self.authlib_download_thread.started.connect(self.authlib_download_worker.run)
        self.authlib_download_worker.progress.connect(self.progress_bar.setValue)
        self.authlib_download_worker.status.connect(self.log)
        self.authlib_download_worker.finished.connect(self.on_authlib_injector_download_finished)
        self.authlib_download_worker.failed.connect(self.on_authlib_injector_download_failed)
        self.authlib_download_worker.finished.connect(self.authlib_download_thread.quit)
        self.authlib_download_worker.failed.connect(self.authlib_download_thread.quit)
        self.authlib_download_thread.start()

    def on_authlib_injector_download_finished(self, payload):
        path = payload.get("path", "")
        self.authlib_injector_input.setText(path)
        self.show_success("下载完成", f"authlib-injector 已保存：{path}")
        self.log(f"authlib-injector 已下载：{path}")

    def on_authlib_injector_download_failed(self, message):
        self.show_warning("下载失败", message)
        self.log(f"authlib-injector 下载失败：{message}")

    def on_external_form_changed(self, *_):
        if not hasattr(self, "external_status_label"):
            return
        try:
            server = normalize_auth_server(self.external_server_input.text().strip()) if self.external_server_input.text().strip() else ""
        except Exception as exc:
            self.external_status_label.setText(f"服务器地址无效：{exc}")
            return
        injector_path = self.authlib_injector_input.text().strip()
        parts = []
        if server:
            parts.append(f"服务器：{server}")
        else:
            parts.append("服务器：未填写")
        parts.append("Authlib：已选择" if injector_path and os.path.isfile(injector_path) else "Authlib：未选择或不存在")
        if self.external_username_input.text().strip():
            parts.append(f"用户：{self.external_username_input.text().strip()}")
        self.external_status_label.setText(" | ".join(parts))

    def set_external_auth_running(self, running):
        for widget_name in (
            "external_server_input",
            "external_username_input",
            "external_password_input",
            "authlib_injector_input",
            "refresh_external_button",
        ):
            if hasattr(self, widget_name):
                getattr(self, widget_name).setEnabled(not running)

    def start_external_auth_worker(self, worker):
        if self.external_auth_thread and self.external_auth_thread.isRunning():
            self.show_warning("外置登录进行中", "请等待当前外置登录任务完成。")
            return False
        self.set_external_auth_running(True)
        self.external_auth_thread = QThread()
        self.external_auth_worker = worker
        worker.moveToThread(self.external_auth_thread)
        self.external_auth_thread.started.connect(worker.run)
        worker.status.connect(self.on_external_auth_status)
        worker.finished.connect(self.on_external_auth_finished)
        worker.failed.connect(self.on_external_auth_failed)
        worker.finished.connect(self.external_auth_thread.quit)
        worker.failed.connect(self.external_auth_thread.quit)
        self.external_auth_thread.finished.connect(lambda: self.set_external_auth_running(False))
        self.external_auth_thread.start()
        return True

    def on_external_auth_status(self, message):
        self.external_status_label.setText(message)
        self.log(message)

    def probe_external_server(self):
        server = self.external_server_input.text().strip()
        if not server:
            self.show_warning("缺少服务器", "请先填写外置登录服务器地址。")
            return
        self.start_external_auth_worker(ExternalAuthWorker("probe", server=server))

    def refresh_current_external_account(self):
        account = self.current_account()
        if not account or account.get("type") != "external":
            self.show_warning("缺少外置账号", "请先选择一个外置登录账号。")
            return
        try:
            account["auth_server"] = normalize_auth_server(self.external_server_input.text().strip() or account.get("auth_server", ""))
            account["authlib_injector_path"] = self.authlib_injector_input.text().strip() or account.get("authlib_injector_path", "")
        except Exception as exc:
            self.show_warning("外置账号配置无效", str(exc))
            return
        self.start_external_auth_worker(ExternalAuthWorker("refresh", account=account))

    def add_external_account(self):
        server = self.external_server_input.text().strip()
        username = self.external_username_input.text().strip()
        password = self.external_password_input.text()
        injector_path = self.authlib_injector_input.text().strip()
        if not injector_path or not os.path.isfile(injector_path):
            self.show_warning("缺少 Authlib Injector", "请选择 authlib-injector.jar。")
            return

        self.start_external_auth_worker(ExternalAuthWorker(
            "login",
            server=server,
            username=username,
            password=password,
            injector_path=injector_path,
        ))

    def on_external_auth_finished(self, payload):
        action = payload.get("action", "")
        if action == "probe":
            self.external_status_label.setText(payload.get("message", "服务器可访问"))
            self.show_success("服务器可访问", payload.get("server", ""))
            return

        if action == "refresh":
            self.upsert_account(payload)
            self.external_status_label.setText(f"外置登录有效：{account_label(payload)}")
            self.show_success("外置登录有效", account_label(payload))
            self.log(f"外置登录已刷新/验证：{account_label(payload)}")
            return

        if action == "login":
            account_id = str(uuidlib.uuid4())
            duplicate = self.find_duplicate_account("external", payload.get("username", ""), payload.get("uuid", ""))
            if duplicate:
                account_id = duplicate.get("id", account_id)
            account = {
                "id": account_id,
                "type": "external",
                "display_name": payload.get("display_name", payload.get("username", "")),
                "username": payload.get("username", ""),
                "uuid": payload.get("uuid", ""),
                "access_token": payload.get("access_token", ""),
                "refresh_token": "",
                "client_token": payload.get("client_token", ""),
                "auth_server": payload.get("server", normalize_auth_server(self.external_server_input.text().strip())),
                "authlib_injector_path": payload.get("authlib_injector_path", self.authlib_injector_input.text().strip()),
            }
            self.external_password_input.clear()
            self.upsert_account(account)
            self.external_status_label.setText(f"外置登录成功：{account_label(account)}")
            self.log(f"外置登录成功：{account_label(account)}")
            self.show_success("外置登录成功", account_label(account))

    def on_external_auth_failed(self, message):
        self.external_status_label.setText(f"外置登录失败：{message}")
        self.show_warning("外置登录失败", message)
        self.log(f"外置登录失败：{message}")

    def delete_selected_account(self):
        account = self.current_delete_account()
        if not account:
            self.show_warning("没有账号", "当前没有可删除的账号。")
            return
        self.accounts = [item for item in self.accounts if item.get("id") != account.get("id")]
        self.selected_account_id = self.accounts[0].get("id") if self.accounts else ""
        save_accounts(self.accounts)
        config["ACCOUNTS"]["selected_account_id"] = self.selected_account_id
        save_config()
        self.refresh_account_selector()
        self.show_success("账号已删除", account_label(account))

    def copy_login_link(self):
        login_url = self.login_link_input.text().strip()
        if not login_url:
            login_url = authenticator.get_login_url()
            self.login_link_input.setText(login_url)
        QApplication.clipboard().setText(login_url)
        self.update_account_field_visibility()
        if hasattr(self, "account_stack"):
            self.switch_account_section("microsoft")
        self.show_success("已复制", "Microsoft 登录链接已复制到剪贴板。")

    def open_login_link(self):
        login_url = self.login_link_input.text().strip()
        if not login_url:
            login_url = authenticator.get_login_url()
            self.login_link_input.setText(login_url)
            self.update_account_field_visibility()
        webbrowser.open(login_url)

    def on_login_url_ready(self, login_url):
        self.login_link_input.setText(login_url)
        self.log(f"Microsoft 登录链接：{login_url}")
        self.update_account_field_visibility()
        if hasattr(self, "account_stack"):
            self.switch_account_section("microsoft")
        if not self.auto_open_browser_check.isChecked():
            QMessageBox.information(
                self,
                "Microsoft 登录链接",
                "已生成登录链接。请复制或手动打开管理中心的账号分页中的链接完成登录。",
            )

    def current_game_dir(self):
        return self.game_dir_input.text().strip() or game_directory

    def start_scan_task(self, task_type, game_dir="", version_type="", mirror_source="official", show_feedback=False):
        existing_thread = self.scan_threads.get(task_type)
        if existing_thread and existing_thread.isRunning():
            if show_feedback:
                self.show_info("正在刷新", self.scan_task_running_message(task_type))
                self.scan_feedback_tasks.add(task_type)
            return

        if show_feedback:
            self.scan_feedback_tasks.add(task_type)
            self.show_info("正在刷新", self.scan_task_started_message(task_type))

        thread = QThread()
        worker = ScanWorker(task_type, game_dir=game_dir, version_type=version_type, mirror_source=mirror_source)
        worker.moveToThread(thread)
        thread.started.connect(worker.run)
        worker.status.connect(self.log)
        worker.finished.connect(self.on_scan_finished)
        worker.failed.connect(lambda message, current=task_type: self.on_scan_failed(current, message))
        worker.finished.connect(thread.quit)
        worker.failed.connect(thread.quit)
        thread.finished.connect(lambda current=task_type: self.clear_scan_task(current))
        self.scan_threads[task_type] = thread
        self.scan_workers[task_type] = worker
        thread.start()

    def scan_task_started_message(self, task_type):
        messages = {
            "java": "正在扫描本机 Java 环境...",
            "local_versions": "正在读取本地 Minecraft 版本...",
            "remote_versions": "正在获取远程版本列表...",
        }
        return messages.get(task_type, "正在刷新状态...")

    def scan_task_running_message(self, task_type):
        messages = {
            "java": "Java 环境扫描仍在进行。",
            "local_versions": "本地版本刷新仍在进行。",
            "remote_versions": "远程版本列表刷新仍在进行。",
        }
        return messages.get(task_type, "刷新任务仍在进行。")

    def should_show_scan_feedback(self, task_type):
        return task_type in self.scan_feedback_tasks

    def finish_scan_feedback(self, task_type):
        self.scan_feedback_tasks.discard(task_type)

    def clear_scan_task(self, task_type):
        self.scan_threads.pop(task_type, None)
        self.scan_workers.pop(task_type, None)

    def on_scan_finished(self, payload):
        task = payload.get("task", "")
        if task == "java":
            paths = payload.get("paths", [])
            show_feedback = self.should_show_scan_feedback(task)
            self.java_combo.clear()
            self.java_versions = payload.get("versions", {})
            for path in paths:
                self.java_combo.addItem(path)
            if paths:
                self.apply_recommended_java(self.current_selected_version())
                self.log(f"找到 {len(paths)} 个 Java。")
            else:
                self.java_version_label.setText("未找到 Java，请安装 Java 或手动选择游戏运行环境。")
                self.launch_status_label.setText("未找到可用 Java")
                self.log("未找到 Java。")
            self.update_home_summary()
            if show_feedback:
                self.show_success("刷新完成", f"已找到 {len(paths)} 个 Java 环境。")
                self.finish_scan_feedback(task)
            return

        if task == "local_versions":
            versions = payload.get("versions", [])
            show_feedback = self.should_show_scan_feedback(task)
            previous_version = self.current_selected_version()
            current_category = self.version_category_combo.currentText().strip() if hasattr(self, "version_category_combo") else "全部版本"
            filtered = []
            for version in versions:
                entry = self.version_settings_entry(version)
                hidden = bool(entry.get("hidden", False))
                favorite = bool(entry.get("favorite", False))
                if current_category == "隐藏":
                    include = hidden
                elif current_category == "收藏":
                    include = favorite and not hidden
                else:
                    include = not hidden and self.version_matches_category(version, current_category)
                if include:
                    filtered.append(version)
            filtered.sort(key=lambda item: (not self.version_settings_entry(item).get("favorite", False), item.lower()))
            last_version = self.version_settings.get("_meta", {}).get("last_launched_version", "")
            current_version = previous_version
            self.local_version_combo.blockSignals(True)
            self.local_version_combo.clear()
            self.local_version_combo.addItems(versions)
            self.local_version_combo.blockSignals(False)
            self.version_display_combo.blockSignals(True)
            self.version_display_combo.clear()
            self.version_display_ids = list(filtered)
            self.version_display_combo.addItems([self.version_display_name(version) for version in filtered])
            self.version_list.blockSignals(True)
            self.version_list.clear()
            self.version_list_ids = list(filtered)
            for version in filtered:
                entry = self.version_settings_entry(version)
                badges = []
                if entry.get("favorite"):
                    badges.append("收藏")
                if entry.get("hidden"):
                    badges.append("隐藏")
                version_type = version_type_label(self.current_game_dir(), version)
                base_version = self.base_version_for(version)
                badge_text = f" [{' / '.join(badges)}]" if badges else ""
                item = QListWidgetItem(
                    f"{self.version_display_name(version)}{badge_text}\n"
                    f"{version_type} | Minecraft {base_version} | {self.runtime_directory_for_version(version)}"
                )
                item.setData(Qt.ItemDataRole.UserRole, version)
                self.version_list.addItem(item)
            selected_version = current_version if current_version in filtered else ""
            if not selected_version and last_version in filtered:
                selected_version = last_version
            if not selected_version and filtered:
                selected_version = filtered[0]
            if selected_version:
                display_name = self.version_display_name(selected_version)
                display_index = self.version_display_combo.findText(display_name)
                if display_index >= 0:
                    self.version_display_combo.setCurrentIndex(display_index)
                list_index = self.version_list_ids.index(selected_version) if selected_version in self.version_list_ids else -1
                if list_index >= 0:
                    self.version_list.setCurrentRow(list_index)
            self.version_display_combo.blockSignals(False)
            self.version_list.blockSignals(False)
            if selected_version:
                self.set_selected_version(selected_version, sync_display=False)
            self.refresh_install_versions(versions)
            self.refresh_resource_target_versions(versions)
            self.motion.pulse_list(self.version_list)
            self.log(f"本地版本数量：{len(versions)}")
            if selected_version:
                self.on_local_version_changed(selected_version)
            elif versions:
                self.launch_status_label.setText("当前分类没有可显示的版本")
            else:
                self.launch_status_label.setText("当前游戏目录下没有可启动的本地版本")
            self.update_home_summary()
            if show_feedback:
                self.show_success("刷新完成", f"已同步 {len(versions)} 个本地版本。")
                self.finish_scan_feedback(task)
            return

        if task == "remote_versions":
            versions = payload.get("versions", [])
            show_feedback = self.should_show_scan_feedback(task)
            self.remote_version_combo.clear()
            self.remote_version_combo.addItems(versions)
            self.log(f"远程版本数量：{len(versions)}")
            self.update_home_summary()
            if show_feedback:
                self.show_success("刷新完成", f"已获取 {len(versions)} 个远程版本。")
                self.finish_scan_feedback(task)

    def on_scan_failed(self, task_type, message):
        show_feedback = self.should_show_scan_feedback(task_type)
        if task_type == "remote_versions":
            self.log(f"刷新远程版本失败：{message}")
            if show_feedback:
                self.show_warning("刷新失败", message)
                self.finish_scan_feedback(task_type)
            return
        self.log(f"{task_type} 扫描失败：{message}")
        if show_feedback:
            self.show_warning("刷新失败", message)
            self.finish_scan_feedback(task_type)

    def refresh_all(self):
        self.refresh_java_paths(show_feedback=True)
        self.refresh_local_versions(show_feedback=True)
        self.log("正在刷新本地状态；远程版本请在下载页手动刷新。")

    def update_home_summary(self):
        account = self.current_account()
        account_text = account_label(account) if account else "未选择"
        self.home_account_label.setText(f"账号：{account_text}")
        if hasattr(self, "account_summary_label"):
            self.account_summary_label.setText(f"当前账号：{account_text}")
            self.motion.pulse_widget(self.account_summary_label, duration=200, start_opacity=0.58, throttle_key="account_summary", min_interval=0.15)
        self.home_java_label.setText(f"Java：{self.java_combo.count()} 个")
        self.home_local_label.setText(f"本地版本：{self.local_version_combo.count()}")
        self.home_remote_label.setText(f"远程版本：{self.remote_version_combo.count()}")
        self.home_dir_label.setText(f"游戏目录：{self.current_game_dir()}")
        for key, widget in (
            ("home_account", self.home_account_label),
            ("home_java", self.home_java_label),
            ("home_local", self.home_local_label),
            ("home_remote", self.home_remote_label),
            ("home_dir", self.home_dir_label),
        ):
            self.motion.pulse_widget(widget, duration=190, start_opacity=0.62, throttle_key=key, min_interval=0.18)

    def get_required_java_version(self, version_id):
        if not version_id:
            return None
        try:
            return infer_required_java_version(self.current_game_dir(), version_id)
        except Exception:
            return None

    def choose_java_for_version(self, version_id):
        required = self.get_required_java_version(version_id)
        if not required:
            return None, None

        candidates = []
        for path, major_version in self.java_versions.items():
            if major_version is None:
                continue
            if major_version >= required:
                candidates.append((major_version, path))

        if not candidates:
            fallback = []
            for path, major_version in self.java_versions.items():
                if major_version is not None:
                    fallback.append((major_version, path))
            if not fallback:
                return required, None
            fallback.sort(key=lambda item: (item[0], item[1]))
            return required, fallback[0][1]

        candidates.sort(key=lambda item: (item[0], item[1]))
        return required, candidates[0][1]

    def apply_recommended_java(self, version_id):
        required, java_path = self.choose_java_for_version(version_id)
        if not version_id:
            self.launch_status_label.setText("请选择本地版本")
            return

        if required is None:
            self.launch_status_label.setText("无法判断该版本需要的 Java，保留当前选择")
            return

        if java_path:
            index = self.java_combo.findText(java_path)
            if index >= 0:
                self.java_combo.setCurrentIndex(index)
            major_version = self.java_versions.get(java_path)
            self.launch_status_label.setText(
                f"已自动选择 Java {major_version}，适配 Minecraft {version_id}（至少需要 Java {required}）"
            )
            self.update_java_version(java_path)
        else:
            self.launch_status_label.setText(
                f"当前未找到可用于 Minecraft {version_id} 的 Java（至少需要 Java {required}）"
            )

    def on_local_version_changed(self, version_id):
        version_id = version_id.strip()
        if not version_id:
            return
        index = self.version_display_ids.index(version_id) if version_id in self.version_display_ids else -1
        if index >= 0 and self.version_display_combo.currentIndex() != index:
            self.version_display_combo.blockSignals(True)
            self.version_display_combo.setCurrentIndex(index)
            self.version_display_combo.blockSignals(False)
        self.apply_recommended_java(version_id)
        self.populate_version_settings_panel(version_id)
        self.motion.fade_slide_in(self.launch_status_label, offset=10, duration=210)

    def on_java_selected(self, java_path):
        path = java_path.strip()
        self.update_java_version(path)
        version_id = self.local_version_combo.currentText().strip()
        if not path or not version_id:
            return

        required = self.get_required_java_version(version_id)
        selected_java_major = self.java_versions.get(path)
        if required and selected_java_major:
            if selected_java_major >= required:
                self.launch_status_label.setText(
                    f"当前选择的是 Java {selected_java_major}，可用于 Minecraft {version_id}（至少需要 Java {required}）"
                )
            else:
                self.launch_status_label.setText(
                    f"当前选择的是 Java {selected_java_major}，低于 Minecraft {version_id} 需要的 Java {required}"
                )

    def on_java_download_status(self, message):
        self.java_download_status_label.setText(message)
        self.motion.pulse_widget(self.java_download_status_label, duration=210, start_opacity=0.5, throttle_key="java_download_status", min_interval=0.2)

    def on_java_download_finished(self, payload):
        java_path = payload.get("java_path", "")
        major = payload.get("major", "")
        if java_path:
            self.java_versions[java_path] = major
            if self.java_combo.findText(java_path) < 0:
                self.java_combo.addItem(java_path)
            self.java_combo.setCurrentText(java_path)
        self.java_download_progress_bar.setValue(100)
        self.java_download_status_label.setText(f"Java {major} 已安装：{java_path}")
        self.show_success("Java 已安装", f"Java {major} 已可用于启动。")
        self.log(f"Java {major} 已安装：{java_path}")
        self.update_home_summary()

    def on_java_download_failed(self, message):
        self.java_download_status_label.setText(f"Java 下载失败：{message}")
        self.show_warning("Java 下载失败", message)
        self.log(f"Java 下载失败：{message}")

    def refresh_java_paths(self, _checked=False, *, show_feedback=True):
        self.start_scan_task("java", show_feedback=show_feedback)

    def download_recommended_java(self):
        if self.java_download_thread and self.java_download_thread.isRunning():
            self.show_warning("下载进行中", "当前已有 Java 下载任务在运行。")
            return

        version_id = self.current_selected_version()
        required = self.get_required_java_version(version_id)
        if not version_id:
            self.show_warning("缺少版本", "请先选择一个本地版本。")
            return
        if not required:
            self.show_warning("无法判断 Java", "当前版本没有可识别的 Java 需求。")
            return

        install_root = os.path.join(self.current_game_dir(), "java_runtimes")
        self.java_download_progress_bar.setValue(0)
        self.java_download_status_label.setText(f"准备下载 Java {required}...")
        self.java_download_thread = QThread()
        self.java_download_worker = JavaDownloadWorker(required, install_root)
        self.java_download_worker.moveToThread(self.java_download_thread)
        self.java_download_thread.started.connect(self.java_download_worker.run)
        self.java_download_worker.progress.connect(self.java_download_progress_bar.setValue)
        self.java_download_worker.status.connect(self.on_java_download_status)
        self.java_download_worker.finished.connect(self.on_java_download_finished)
        self.java_download_worker.failed.connect(self.on_java_download_failed)
        self.java_download_worker.finished.connect(self.java_download_thread.quit)
        self.java_download_worker.failed.connect(self.java_download_thread.quit)
        self.java_download_thread.start()

    def update_java_version(self, path):
        if not path:
            self.java_version_label.setText("未选择 Java")
            return
        major = self.java_versions.get(path)
        if major:
            self.java_version_label.setText(f"Java {major}")
            return
        version = get_java_version(path)
        self.java_version_label.setText(version or "无法获取 Java 版本")

    def refresh_local_versions(self, _checked=False, *, show_feedback=True):
        self.start_scan_task("local_versions", game_dir=self.current_game_dir(), show_feedback=show_feedback)

    def refresh_install_versions(self, existing_versions=None):
        versions = existing_versions if existing_versions is not None else get_local_versions(self.current_game_dir())
        install_type = self.install_type_combo.currentText().strip() if hasattr(self, "install_type_combo") else ""
        if install_type == "fabric_api":
            fabric_versions = [version for version in versions if version.startswith("fabric-loader-")]
            if fabric_versions:
                versions = fabric_versions
        current = self.install_version_combo.currentText().strip()
        self.install_version_combo.blockSignals(True)
        self.install_version_combo.clear()
        self.install_version_combo.addItems(versions)
        if current:
            index = self.install_version_combo.findText(current)
            if index >= 0:
                self.install_version_combo.setCurrentIndex(index)
        self.install_version_combo.blockSignals(False)

    def update_download_addon_controls(self):
        selected_loader = self.download_install_combo.currentText().strip() if hasattr(self, "download_install_combo") else "不安装"
        fabric_checked = selected_loader == "fabric"
        if hasattr(self, "download_fabric_api_check"):
            self.download_fabric_api_check.setVisible(fabric_checked)
            self.download_fabric_api_check.setEnabled(fabric_checked and not self.is_download_task_running())
            if not fabric_checked and self.download_fabric_api_check.isChecked():
                self.download_fabric_api_check.blockSignals(True)
                self.download_fabric_api_check.setChecked(False)
                self.download_fabric_api_check.blockSignals(False)

        selected = self.get_selected_download_addons()
        if selected:
            labels = [INSTALL_TYPE_LABELS.get(item, item) for item in selected]
            self.download_addon_hint_label.setText(f"下载完成后将继续安装：{' + '.join(labels)}")
        else:
            self.download_addon_hint_label.setText("可在下载原版后自动继续安装；Fabric API 仅在 Fabric 一起安装时可用。")

        warnings = []
        if fabric_checked and not self.download_fabric_api_check.isChecked():
            warnings.append("提示：大多数 Fabric Mod 需要 Fabric API。")
        if selected_loader == "optifine":
            warnings.append("提示：OptiFine 与部分 Mod 兼容性不佳，整合包建议优先考虑 Fabric/Forge。")
        if selected_loader in {"forge", "neoforge"}:
            warnings.append("提示：Forge / NeoForge 安装依赖 Java，安装过程可能需要更久。")
        if hasattr(self, "download_warning_label"):
            self.download_warning_label.setText(" ".join(warnings))

    def get_selected_download_addons(self):
        install_types = []
        selected_loader = self.download_install_combo.currentText().strip() if hasattr(self, "download_install_combo") else ""
        if selected_loader in {"fabric", "forge", "neoforge", "optifine"}:
            install_types.append(selected_loader)
        if selected_loader == "fabric" and self.download_fabric_api_check.isChecked():
            install_types.append("fabric_api")
        return install_types

    def update_install_button_text(self, install_type):
        if not hasattr(self, "install_button"):
            return
        self.install_button.setText("开始安装")

    def refresh_remote_versions(self, _checked=False, *, show_feedback=True):
        self.start_scan_task(
            "remote_versions",
            version_type=self.version_type_combo.currentText(),
            mirror_source=self.mirror_combo.currentText(),
            show_feedback=show_feedback,
        )

    def save_settings(self, show_feedback=True):
        account = self.current_account()
        if account:
            if account.get("type") == "offline":
                username = self.username_input.text().strip()
                if username:
                    duplicate = self.find_duplicate_account("offline", username, exclude_id=account.get("id"))
                    if duplicate:
                        self.show_warning("重复用户名", f"离线账号 {username} 已存在，无法保存为重复账号。")
                        return False
                account["display_name"] = username or account.get("display_name", "离线账号")
                account["username"] = username
                account["uuid"] = self.uuid_input.text().strip()
                account["access_token"] = self.access_token_input.text().strip()
                self.upsert_account(account)
            elif account.get("type") == "external":
                try:
                    account["auth_server"] = normalize_auth_server(self.external_server_input.text().strip() or account.get("auth_server", ""))
                except Exception as exc:
                    self.show_warning("外置服务器地址无效", str(exc))
                    return False
                account["authlib_injector_path"] = self.authlib_injector_input.text().strip() or account.get("authlib_injector_path", "")
                self.upsert_account(account)
            config["ACCOUNTS"]["selected_account_id"] = account.get("id", "")
        config["DOWNLOAD"]["mirror_source"] = self.mirror_combo.currentText()
        config["DOWNLOAD"]["max_core_threads"] = str(self.download_core_threads_input.value())
        config["DOWNLOAD"]["max_asset_threads"] = str(self.download_asset_threads_input.value())
        config["DOWNLOAD"]["speed_limit_kbps"] = str(self.download_speed_limit_input.value())
        config["DOWNLOAD"]["cache_strategy"] = self.download_cache_combo.currentText()
        config["AUTH"]["auto_open_browser"] = str(self.auto_open_browser_check.isChecked())
        config["GAME"]["directory"] = self.current_game_dir()
        config["GAME"]["enable_resource_isolation"] = str(self.resource_isolation_check.isChecked())
        config["UI"]["advanced_mode"] = str(self.advanced_mode_check.isChecked())
        config["UI"]["theme"] = self.theme_combo.currentText()
        config["UI"]["theme_image"] = self.theme_image_input.text().strip()
        config["HOME"]["content_source"] = self.home_content_input.text().strip()
        config["HOME"]["allow_network"] = str(self.home_network_check.isChecked())
        config["MUSIC"]["path"] = self.music_path_input.text().strip()
        config["MUSIC"]["enabled"] = str(self.music_enabled_check.isChecked())
        config["MUSIC"]["volume"] = str(self.music_volume_input.value())
        config["MUSIC"]["pause_on_launch"] = str(self.music_pause_on_launch_check.isChecked())
        config["FEATURES"]["show_download"] = str(self.show_download_check.isChecked())
        config["FEATURES"]["show_manage"] = str(self.show_manage_check.isChecked())
        config["SERVERS"]["items"] = self.server_list_input.toPlainText().strip()
        save_config()
        self.apply_theme_image()
        self.refresh_home_content()
        self.apply_music_settings(show_feedback=False)
        self.apply_feature_visibility()
        self.log("设置已保存。")
        self.update_home_summary()
        if show_feedback:
            self.show_success("已保存", "启动器设置已写入配置文件。")
        return True

    def choose_game_directory(self):
        directory = QFileDialog.getExistingDirectory(self, "选择游戏目录", self.current_game_dir())
        if directory:
            self.game_dir_input.setText(directory)
            self.refresh_local_versions(show_feedback=False)
            self.update_home_summary()

    def open_game_directory(self):
        path = os.path.abspath(self.current_game_dir())
        os.makedirs(path, exist_ok=True)
        os.startfile(path)

    def start_microsoft_login(self):
        if self.auth_thread and self.auth_thread.isRunning():
            self.show_warning("登录进行中", "当前已有 Microsoft 登录流程在运行。")
            return

        self.save_settings(show_feedback=False)
        self.auth_thread = QThread()
        self.auth_worker = AuthWorker(self.auto_open_browser_check.isChecked())
        self.auth_worker.moveToThread(self.auth_thread)
        self.auth_thread.started.connect(self.auth_worker.run)
        self.auth_worker.login_url_ready.connect(self.on_login_url_ready)
        self.auth_worker.status.connect(self.log)
        self.auth_worker.finished.connect(self.on_auth_finished)
        self.auth_worker.failed.connect(self.on_auth_failed)
        self.auth_worker.finished.connect(self.auth_thread.quit)
        self.auth_worker.failed.connect(self.auth_thread.quit)
        self.auth_thread.start()

    def on_auth_finished(self, account):
        logger.info("Auth finished in UI: account=%s", redact_mapping(account))
        duplicate = self.find_duplicate_account("microsoft", account.get("username", ""), account.get("uuid", ""))
        if duplicate:
            duplicate.update({
                "display_name": account.get("display_name", duplicate.get("display_name", "")),
                "username": account.get("username", duplicate.get("username", "")),
                "uuid": account.get("uuid", duplicate.get("uuid", "")),
                "refresh_token": account.get("refresh_token", duplicate.get("refresh_token", "")),
            })
            self.upsert_account(duplicate)
            self.log(f"Microsoft 账号已存在，已更新：{account_label(duplicate)}")
            self.show_success("账号已更新", account_label(duplicate))
            return

        self.upsert_account(account)
        self.log(f"Microsoft 登录成功：{account_label(account)}")
        self.show_success("登录成功", account_label(account))

    def on_auth_failed(self, message):
        logger.warning("Auth failed in UI: %s", message)
        self.log(f"Microsoft 登录失败：{message}")
        self.show_warning("登录失败", message)

    def launch_game(self):
        java_path = self.java_combo.currentText().strip()
        version = self.current_selected_version()
        account = self.current_account()
        logger.info(
            "Launch requested: version=%s java=%s account=%s",
            version,
            java_path,
            redact_mapping({
                "id": account.get("id") if account else "",
                "type": account.get("type") if account else "",
                "username": account.get("username") if account else "",
                "uuid": account.get("uuid") if account else "",
            }),
        )
        if not account:
            self.show_warning("缺少账号", "请先在管理中心的账号分页中添加或选择一个账号。")
            return
        if not java_path:
            self.show_warning("缺少 Java", "请先选择 Java 路径。")
            return
        if not version:
            self.show_warning("缺少版本", "请先选择本地游戏版本。")
            return

        required_java = self.get_required_java_version(version)
        selected_java_major = self.java_versions.get(java_path)
        if required_java and selected_java_major and selected_java_major < required_java:
            self.show_warning(
                "Java 版本不兼容",
                f"Minecraft {version} 至少需要 Java {required_java}，当前选择的是 Java {selected_java_major}。",
            )
            return

        launch_options = launch_options_for_version(
            self.current_game_dir(),
            self.version_settings,
            version,
            global_isolation=self.resource_isolation_check.isChecked(),
        )
        if not launch_options.get("manual_memory"):
            launch_options["max_memory_mb"] = recommended_memory_mb()
            launch_options["min_memory_mb"] = 0
        if self.launch_thread and self.launch_thread.isRunning():
            self.show_warning("启动进行中", "当前已有启动任务在运行。")
            return

        self.set_launch_running(True)
        self.launch_status_label.setText("正在准备启动...")
        self.launch_progress_bar.setValue(5)
        self.launch_stage_label.setText("当前步骤：准备启动")
        account_type_labels = {"microsoft": "Microsoft", "external": "外置登录", "offline": "离线"}
        self.launch_method_label.setText(f"登录方式：{account_type_labels.get(account.get('type'), account.get('type', '未知'))}")
        self.launch_progress_label.setText("启动进度：5%")
        self.launch_thread = QThread()
        self.launch_worker = LaunchWorker(
            java_path,
            version,
            self.current_game_dir(),
            account,
            launch_options,
        )
        self.launch_worker.moveToThread(self.launch_thread)
        self.launch_thread.started.connect(self.launch_worker.run)
        self.launch_worker.status.connect(self.on_launch_status)
        self.launch_worker.stage.connect(self.on_launch_stage)
        self.launch_worker.progress.connect(self.on_launch_progress)
        self.launch_worker.finished.connect(self.on_launch_finished)
        self.launch_worker.failed.connect(self.on_launch_failed)
        self.launch_worker.finished.connect(self.launch_thread.quit)
        self.launch_worker.failed.connect(self.launch_thread.quit)
        self.launch_thread.start()

    def on_launch_status(self, message):
        self.launch_status_label.setText(message)
        self.motion.pulse_widget(self.launch_status_label, duration=210, start_opacity=0.5, throttle_key="launch_status", min_interval=0.12)
        self.log(message)

    def on_launch_stage(self, stage):
        self.launch_stage_label.setText(f"当前步骤：{stage}")
        self.motion.pulse_widget(self.launch_stage_label, duration=190, start_opacity=0.55, throttle_key="launch_stage", min_interval=0.12)

    def on_launch_progress(self, progress):
        self.launch_progress_bar.setValue(progress)
        self.launch_progress_label.setText(f"启动进度：{progress}%")

    def on_install_status(self, message):
        self.install_status_label.setText(message)
        self.motion.pulse_widget(self.install_status_label, duration=210, start_opacity=0.5, throttle_key="install_status", min_interval=0.12)
        self.log_install(message)

    def on_install_status_from_download(self, message):
        self.install_status_label.setText(message)
        self.motion.pulse_widget(self.install_status_label, duration=210, start_opacity=0.5, throttle_key="install_status_from_download", min_interval=0.12)
        self.log_install(message)

    def on_launch_finished(self, payload):
        logger.info("Launch finished in UI: payload=%s", redact_mapping(payload))
        self.set_launch_running(False)
        account = payload.get("account", {})
        if self.music_pause_on_launch_check.isChecked():
            self.media_player.pause()
        if account.get("id"):
            self.upsert_account(account)
        version = payload.get("version", "")
        self.version_settings.setdefault("_meta", {})["last_launched_version"] = version
        save_version_settings(self.version_settings)
        self.on_java_selected(self.java_combo.currentText())
        self.log(f"正在使用 {account_label(account)} 启动 Minecraft {version}...")
        self.show_success("正在启动", f"Minecraft {version} 已开始启动。")

    def on_launch_failed(self, message):
        logger.warning("Launch failed in UI: %s", message)
        self.set_launch_running(False)
        self.launch_status_label.setText(f"启动失败：{message}")
        self.launch_stage_label.setText("当前步骤：启动失败")
        self.log(f"启动失败：{message}")
        self.show_warning("启动失败", message)

    def start_download(self):
        logger.info("Download requested from UI")
        version = self.remote_version_combo.currentText().strip()
        if not version or version == "点击刷新远程版本":
            self.show_warning("缺少版本", "请先刷新并选择要下载的版本。")
            return

        if not self.save_settings(show_feedback=False):
            return
        auto_install_types = self.get_selected_download_addons()
        java_path = self.java_combo.currentText().strip()
        logger.info(
            "Download selection: version=%s mirror=%s game_dir=%s auto_install=%s java=%s",
            version,
            self.mirror_combo.currentText(),
            self.current_game_dir(),
            auto_install_types,
            java_path,
        )
        if any(item in {"forge", "neoforge"} for item in auto_install_types) and not java_path:
            self.show_warning("缺少 Java", "自动安装 Forge 或 NeoForge 需要可用的 Java。")
            return
        mirror_key = self.mirror_combo.currentText()
        game_dir = self.current_game_dir()
        if auto_install_types:
            install_text = " + ".join(INSTALL_TYPE_LABELS[item] for item in auto_install_types)
            title = f"下载 {version} 并安装 {install_text}"
        else:
            title = f"下载 {version}"
        task = DownloadTask(
            "download",
            title,
            lambda version=version,
            mirror_key=mirror_key,
            game_dir=game_dir,
            auto_install_types=list(auto_install_types),
            java_path=java_path: self._start_download_task(
                version,
                mirror_key,
                game_dir,
                auto_install_types,
                java_path,
            ),
        )
        self.queue_download_task(task)

    def _start_download_task(self, version, mirror_key, game_dir, auto_install_types, java_path):
        logger.info(
            "Download task starting from UI queue: version=%s mirror=%s game_dir=%s auto_install=%s java=%s",
            version,
            mirror_key,
            game_dir,
            auto_install_types,
            java_path,
        )
        self.progress_bar.setValue(0)
        self.install_log.clear()
        self.install_status_label.setText("当前任务以下载原版为主")
        self.install_metrics_label.setText("如勾选附加安装，完成原版下载后会在这里显示安装进度")
        if auto_install_types:
            install_text = " + ".join(INSTALL_TYPE_LABELS[item] for item in auto_install_types)
            self.download_metrics_label.setText(f"准备下载并安装：{install_text}")
        else:
            self.download_metrics_label.setText("准备下载...")
        self.set_download_running(True)
        self.download_thread = QThread()
        self.download_worker = DownloadWorker(
            version,
            mirror_key,
            game_dir,
            auto_install_types=auto_install_types,
            java_path=java_path,
            download_options=read_download_options(),
        )
        self.download_worker.moveToThread(self.download_thread)
        self.download_thread.started.connect(self.download_worker.run)
        self.download_worker.progress.connect(self.progress_bar.setValue)
        self.download_worker.metrics.connect(self.update_download_metrics)
        self.download_worker.status.connect(self.log)
        self.download_worker.install_metrics.connect(self.update_install_metrics)
        self.download_worker.install_status.connect(self.on_install_status_from_download)
        self.download_worker.finished.connect(self.on_download_finished)
        self.download_worker.failed.connect(self.on_download_failed)
        self.download_worker.finished.connect(self.download_thread.quit)
        self.download_worker.failed.connect(self.download_thread.quit)
        self.download_thread.start()

    def start_install(self):
        minecraft_version = self.install_version_combo.currentText().strip()
        install_type = self.install_type_combo.currentText().strip()
        if not minecraft_version:
            self.show_warning("缺少目标版本", "请先下载并选择目标 Minecraft 版本。")
            return

        java_path = self.java_combo.currentText().strip()
        if install_type in {"forge", "neoforge"} and not java_path:
            self.show_warning("缺少 Java", f"安装 {install_type} 需要可用的 Java。")
            return

        self.save_settings(show_feedback=False)
        mirror_key = self.mirror_combo.currentText()
        game_dir = self.current_game_dir()
        title = f"安装 {INSTALL_TYPE_LABELS.get(install_type, install_type)} 到 {minecraft_version}"
        task = DownloadTask(
            "install",
            title,
            lambda install_type=install_type,
            minecraft_version=minecraft_version,
            mirror_key=mirror_key,
            game_dir=game_dir,
            java_path=java_path: self._start_install_task(
                install_type,
                minecraft_version,
                mirror_key,
                game_dir,
                java_path,
            ),
        )
        self.queue_download_task(task)

    def _start_install_task(self, install_type, minecraft_version, mirror_key, game_dir, java_path):
        logger.info(
            "Install task starting from UI queue: install_type=%s minecraft_version=%s mirror=%s game_dir=%s java=%s",
            install_type,
            minecraft_version,
            mirror_key,
            game_dir,
            java_path,
        )
        self.progress_bar.setValue(0)
        self.install_log.clear()
        self.install_status_label.setText(f"正在准备安装 {INSTALL_TYPE_LABELS.get(install_type, install_type)}...")
        self.install_metrics_label.setText("正在等待安装器返回进度")
        self.download_metrics_label.setText(f"准备安装 {install_type}...")
        self.set_download_running(True)
        self.install_thread = QThread()
        self.install_worker = InstallWorker(
            install_type,
            minecraft_version,
            mirror_key,
            game_dir,
            java_path,
        )
        self.install_worker.moveToThread(self.install_thread)
        self.install_thread.started.connect(self.install_worker.run)
        self.install_worker.progress.connect(self.progress_bar.setValue)
        self.install_worker.metrics.connect(self.update_install_metrics)
        self.install_worker.status.connect(self.on_install_status)
        self.install_worker.finished.connect(self.on_install_finished)
        self.install_worker.failed.connect(self.on_install_failed)
        self.install_worker.finished.connect(self.install_thread.quit)
        self.install_worker.failed.connect(self.install_thread.quit)
        self.install_thread.start()

    def import_modpack(self):
        pack_path, _ = QFileDialog.getOpenFileName(
            self,
            "选择整合包文件",
            "",
            "整合包文件 (*.mrpack *.zip);;所有文件 (*.*)",
        )
        if not pack_path:
            return
        game_dir = self.current_game_dir()
        mirror_key = self.mirror_combo.currentText()
        java_path = self.java_combo.currentText().strip()
        title = f"导入整合包 {os.path.basename(pack_path)}"
        task = DownloadTask(
            "modpack",
            title,
            lambda pack_path=pack_path,
            game_dir=game_dir,
            mirror_key=mirror_key,
            java_path=java_path: self._start_modpack_import_task(pack_path, game_dir, mirror_key, java_path),
        )
        self.queue_download_task(task)

    def _start_modpack_import_task(self, pack_path, game_dir, mirror_key, java_path):
        logger.info(
            "Modpack import task starting from UI queue: pack=%s game_dir=%s mirror=%s java=%s",
            pack_path,
            game_dir,
            mirror_key,
            java_path,
        )
        self.progress_bar.setValue(0)
        self.download_metrics_label.setText("准备导入整合包...")
        self.install_status_label.setText("正在识别整合包")
        self.install_metrics_label.setText(os.path.basename(pack_path))
        self.set_download_running(True)
        self.modpack_thread = QThread()
        self.modpack_worker = ModpackImportWorker(
            pack_path,
            game_dir,
            mirror_source=mirror_key,
            java_path=java_path,
        )
        self.modpack_worker.moveToThread(self.modpack_thread)
        self.modpack_thread.started.connect(self.modpack_worker.run)
        self.modpack_worker.progress.connect(self.progress_bar.setValue)
        self.modpack_worker.status.connect(self.on_install_status)
        self.modpack_worker.finished.connect(self.on_modpack_import_finished)
        self.modpack_worker.failed.connect(self.on_modpack_import_failed)
        self.modpack_worker.finished.connect(self.modpack_thread.quit)
        self.modpack_worker.failed.connect(self.modpack_thread.quit)
        self.modpack_thread.start()

    def search_resources(self):
        if self.resource_search_thread and self.resource_search_thread.isRunning():
            self.show_warning("搜索进行中", "请等待当前资源搜索完成。")
            return

        query = self.resource_query_input.text().strip()
        resource_type = self.resource_type_combo.currentText().strip() or "mod"
        source = self.resource_source_combo.currentText().strip() or "modrinth"
        version_id = self.current_resource_version()
        if not version_id:
            self.show_warning("缺少版本", "请先在资源市场里选择安装目标版本。")
            return
        if not query and source != "local":
            self.show_warning("缺少关键词", "请输入要搜索的资源名称。")
            return
        if resource_type == "mod" and not self.version_supports_mod_management(version_id):
            self.show_warning("版本不支持 Mod", "资源市场的安装目标需要选择 Fabric / Forge / NeoForge / OptiFine 版本。")
            return

        game_version = self.base_version_for(version_id)
        loader = modrinth_loader_for_version(self.current_game_dir(), version_id)
        target_dir = self.resource_directory_for_version(version_id, resource_type)
        sort_index = RESOURCE_SEARCH_SORTS.get(self.resource_sort_combo.currentText().strip(), "relevance")
        logger.info(
            "Resource search requested from UI: source=%s query=%s type=%s version_id=%s game=%s loader=%s target=%s sort=%s",
            source,
            query,
            resource_type,
            version_id,
            game_version,
            loader or "<none>",
            target_dir,
            sort_index,
        )
        self.resource_result_list.clear()
        self.resource_search_hits = []
        self.resource_detail_view.clear()
        self.resource_search_generation += 1
        if self.resource_compat_thread and self.resource_compat_thread.isRunning():
            self.resource_compat_thread.requestInterruption()
            self.resource_compat_thread.quit()
            self.resource_compat_thread.wait(500)
        self.resource_status_label.setText(f"正在搜索 {RESOURCE_TYPE_LABELS.get(resource_type, resource_type)}...")
        self.resource_search_thread = QThread()
        self.resource_search_worker = ResourceSearchWorker(
            query,
            resource_type,
            game_version,
            loader,
            source=source,
            sort_index=sort_index,
            target_dir=target_dir,
        )
        self.resource_search_worker.moveToThread(self.resource_search_thread)
        self.resource_search_thread.started.connect(self.resource_search_worker.run)
        self.resource_search_worker.status.connect(lambda message: self.resource_status_label.setText(message))
        self.resource_search_worker.finished.connect(self.on_resource_search_finished)
        self.resource_search_worker.failed.connect(self.on_resource_search_failed)
        self.resource_search_worker.finished.connect(self.resource_search_thread.quit)
        self.resource_search_worker.failed.connect(self.resource_search_thread.quit)
        self.resource_search_thread.start()

    def install_selected_resource(self):
        current_item = self.resource_result_list.currentItem()
        if current_item is None:
            self.show_warning("缺少资源", "请先选择一个搜索结果。")
            return
        hit = current_item.data(Qt.ItemDataRole.UserRole)
        if not isinstance(hit, dict):
            self.show_warning("缺少资源", "请先选择一个有效搜索结果。")
            return
        if hit.get("source", "modrinth") != "modrinth":
            self.show_warning("不支持一键安装", "当前只支持从 Modrinth 一键安装；CurseForge 或本地来源请查看详情。")
            return
        if not hit.get("compatible", True):
            self.show_warning("不兼容当前目标版本", "这个资源没有适配当前安装目标的可下载文件，请换一个版本或资源。")
            return

        version_id = self.current_resource_version()
        resource_type = self.resource_type_combo.currentText().strip() or "mod"
        if not version_id:
            self.show_warning("缺少版本", "请先在资源市场里选择安装目标版本。")
            return
        if resource_type == "mod" and not self.version_supports_mod_management(version_id):
            self.show_warning("版本不支持 Mod", "资源市场的安装目标需要选择可安装 Mod 的版本。")
            return

        game_version = self.base_version_for(version_id)
        loader = modrinth_loader_for_version(self.current_game_dir(), version_id)
        target_dir = self.resource_directory_for_version(version_id, resource_type)
        install_dependencies = self.resource_dependency_check.isChecked()
        title = f"安装资源 {hit.get('title') or hit.get('slug', '资源')}"
        task = DownloadTask(
            "resource_install",
            title,
            lambda hit=dict(hit),
            resource_type=resource_type,
            game_version=game_version,
            loader=loader,
            target_dir=target_dir,
            install_dependencies=install_dependencies: self._start_resource_install_task(
                hit,
                resource_type,
                game_version,
                loader,
                target_dir,
                install_dependencies,
            ),
        )
        self.queue_download_task(task)

    def _start_resource_install_task(self, hit, resource_type, game_version, loader, target_dir, install_dependencies):
        logger.info(
            "Resource install task starting from UI queue: source=%s project=%s title=%s type=%s game=%s loader=%s target=%s dependencies=%s",
            hit.get("source", "modrinth"),
            hit.get("project_id") or hit.get("slug"),
            hit.get("title") or hit.get("slug", "资源"),
            resource_type,
            game_version,
            loader or "<none>",
            target_dir,
            install_dependencies,
        )
        self.progress_bar.setValue(0)
        self.set_download_running(True)
        self.resource_status_label.setText(f"正在安装：{hit.get('title', hit.get('slug', 'resource'))}")
        self.resource_install_thread = QThread()
        self.resource_install_worker = ResourceInstallWorker(
            hit.get("project_id") or hit.get("slug"),
            hit.get("title") or hit.get("slug", "资源"),
            resource_type,
            game_version,
            loader,
            target_dir,
            source=hit.get("source", "modrinth"),
            install_dependencies=install_dependencies,
        )
        self.resource_install_worker.moveToThread(self.resource_install_thread)
        self.resource_install_thread.started.connect(self.resource_install_worker.run)
        self.resource_install_worker.progress.connect(self.progress_bar.setValue)
        self.resource_install_worker.status.connect(lambda message: self.resource_status_label.setText(message))
        self.resource_install_worker.metrics.connect(self.update_resource_install_metrics)
        self.resource_install_worker.finished.connect(self.on_resource_install_finished)
        self.resource_install_worker.failed.connect(self.on_resource_install_failed)
        self.resource_install_worker.finished.connect(self.resource_install_thread.quit)
        self.resource_install_worker.failed.connect(self.resource_install_thread.quit)
        self.resource_install_thread.start()

    def update_resource_source_controls(self, *_):
        source = self.resource_source_combo.currentText().strip() if hasattr(self, "resource_source_combo") else "modrinth"
        is_modrinth = source == "modrinth"
        if hasattr(self, "resource_sort_combo"):
            self.resource_sort_combo.setEnabled(is_modrinth)
        if hasattr(self, "resource_dependency_check"):
            self.resource_dependency_check.setEnabled(is_modrinth)
        if hasattr(self, "install_resource_button"):
            self.install_resource_button.setEnabled(is_modrinth and not self.is_download_task_running())
        if hasattr(self, "resource_status_label"):
            if source == "curseforge":
                self.resource_status_label.setText("CurseForge 搜索需要设置 CURSEFORGE_API_KEY；当前支持查看详情，安装请手动下载或导入整合包。")
            elif source == "local":
                self.resource_status_label.setText("本地来源会扫描当前版本对应资源目录。")
            else:
                self.resource_status_label.setText("等待搜索")

    def show_selected_resource_detail(self):
        current_item = self.resource_result_list.currentItem()
        if current_item is None:
            return
        hit = current_item.data(Qt.ItemDataRole.UserRole)
        if not isinstance(hit, dict):
            return
        if self.resource_detail_thread and self.resource_detail_thread.isRunning():
            self.resource_detail_thread.quit()
            self.resource_detail_thread.wait(500)

        self.resource_detail_loading.setVisible(True)
        self.resource_detail_view.setPlainText("正在加载详情...")
        resource_type = self.resource_type_combo.currentText().strip() or "mod"
        version_id = self.current_resource_version()
        game_version = self.base_version_for(version_id) if version_id else ""
        loader = modrinth_loader_for_version(self.current_game_dir(), version_id) if version_id else ""
        self.resource_detail_thread = QThread()
        self.resource_detail_worker = ResourceDetailWorker(hit, resource_type, game_version, loader)
        self.resource_detail_worker.moveToThread(self.resource_detail_thread)
        self.resource_detail_thread.started.connect(self.resource_detail_worker.run)
        self.resource_detail_worker.finished.connect(self.on_resource_detail_finished)
        self.resource_detail_worker.failed.connect(self.on_resource_detail_failed)
        self.resource_detail_worker.finished.connect(self.resource_detail_thread.quit)
        self.resource_detail_worker.failed.connect(self.resource_detail_thread.quit)
        self.resource_detail_thread.start()

    def on_resource_detail_finished(self, detail):
        self.resource_detail_loading.setVisible(False)
        source = RESOURCE_SOURCE_LABELS.get(detail.get("source", ""), detail.get("source", ""))
        parts = [
            "<div style='font-family: Microsoft YaHei, Segoe UI, sans-serif;'>",
            f"<h3>{html.escape(str(detail.get('title', '资源详情')))} ({html.escape(source)})</h3>",
            f"<p>下载：{html.escape(str(detail.get('downloads', 0)))} | 收藏/关注：{html.escape(str(detail.get('followers', 0)))}</p>",
        ]
        if detail.get("project_url"):
            url = html.escape(str(detail.get("project_url")))
            parts.append(f"<p>链接：<a href='{url}'>{url}</a></p>")
        if detail.get("description"):
            parts.append(f"<p>{html.escape(str(detail.get('description', '')))}</p>")
        if detail.get("status"):
            parts.append(f"<p>状态：{html.escape(str(detail.get('status')))}</p>")
        screenshots = detail.get("screenshots", [])
        if screenshots:
            parts.append("<p>截图：</p>")
            parts.append("<div>")
            for screenshot in screenshots[:5]:
                path = screenshot.get("path") if isinstance(screenshot, dict) else ""
                url = screenshot.get("url") if isinstance(screenshot, dict) else screenshot
                src = html.escape(os.path.abspath(path).replace("\\", "/") if path else str(url))
                parts.append(f"<img src='{src}' width='360' style='margin: 0 10px 10px 0;' />")
                if not path and url:
                    escaped_url = html.escape(str(url))
                    parts.append(f"<p>{escaped_url}</p>")
            parts.append("</div>")
        dependencies = detail.get("dependencies", [])
        if dependencies:
            parts.append("<p>依赖：</p><ul>")
            for dependency in dependencies[:12]:
                dep_type = dependency.get("dependency_type", "unknown")
                dep_id = dependency.get("project_id") or dependency.get("version_id") or dependency.get("file_name", "")
                parts.append(f"<li>{html.escape(str(dep_type))}: {html.escape(str(dep_id))}</li>")
            parts.append("</ul>")
        versions = detail.get("versions", [])
        if versions:
            parts.append("<p>可用版本/文件：</p><ul>")
            for version in versions[:8]:
                number = version.get("version_number") or version.get("displayName") or version.get("fileName") or version.get("name", "")
                release_type = version.get("version_type") or version.get("releaseType", "")
                parts.append(f"<li>{html.escape(str(f'{number} {release_type}'.strip()))}</li>")
            parts.append("</ul>")
        parts.append("</div>")
        self.resource_detail_view.setHtml("".join(parts))
        self.motion.pulse_widget(self.resource_detail_view.viewport(), duration=180, start_opacity=0.6, throttle_key="resource_detail", min_interval=0.2)

    def on_resource_detail_failed(self, message):
        self.resource_detail_loading.setVisible(False)
        self.resource_detail_view.setPlainText(f"详情加载失败：{message}")
        self.log(f"资源详情加载失败：{message}")

    def on_download_finished(self, payload):
        self.set_download_running(False)
        self.finish_download_task(failed=False)
        version = payload.get("version", "")
        post_install = payload.get("post_install")
        self.log(f"Minecraft {version} 下载完成。")
        self.refresh_local_versions(show_feedback=False)
        self.local_version_combo.setCurrentText(version)
        if post_install:
            installed_version = post_install.get("installed_version", version)
            alias = self.apply_auto_version_alias(installed_version, post_install.get("steps", []))
            self.download_metrics_label.setText(post_install.get("message", "下载和安装完成"))
            self.install_status_label.setText("附加安装完成")
            self.install_metrics_label.setText(post_install.get("message", "附加安装已完成"))
            index = self.local_version_combo.findText(installed_version)
            if index >= 0:
                self.local_version_combo.setCurrentIndex(index)
            if alias:
                self.show_success("下载和安装完成", f"已自动命名为：{alias}")
            else:
                self.show_success("下载和安装完成", f"Minecraft {version} 已下载，并完成附加安装。")
        else:
            self.download_metrics_label.setText("下载完成")
            self.install_status_label.setText("本次未执行附加安装")
            self.install_metrics_label.setText("如需 Fabric / Forge / NeoForge / OptiFine，可切到“安装扩展”")
            self.show_success("下载完成", f"Minecraft {version} 已下载完成。")

    def on_download_failed(self, message):
        self.set_download_running(False)
        self.finish_download_task(failed=True)
        self.log(f"下载失败：{message}")
        self.download_metrics_label.setText(f"下载失败：{message}")
        self.install_status_label.setText("附加安装未开始")
        self.show_warning("下载失败", message)

    def on_install_finished(self, payload):
        self.set_download_running(False)
        self.finish_download_task(failed=False)
        installed_version = payload.get("installed_version", "")
        message = payload.get("message", "安装完成")
        self.log(f"安装完成：{message}")
        self.install_status_label.setText("安装完成")
        self.install_metrics_label.setText(message)
        self.download_metrics_label.setText(message)
        if installed_version:
            install_types = [payload.get("install_type", "")]
            alias = self.apply_auto_version_alias(installed_version, install_types)
            self.refresh_local_versions(show_feedback=False)
            index = self.local_version_combo.findText(installed_version)
            if index >= 0:
                self.local_version_combo.setCurrentIndex(index)
            if alias:
                self.show_success("安装完成", f"已自动命名为：{alias}")
                return
        else:
            self.refresh_local_versions(show_feedback=False)
        self.show_success("安装完成", message)

    def on_install_failed(self, message):
        self.set_download_running(False)
        self.finish_download_task(failed=True)
        self.log(f"安装失败：{message}")
        self.download_metrics_label.setText(f"安装失败：{message}")
        self.install_status_label.setText("安装失败")
        self.install_metrics_label.setText(message)
        self.show_warning("安装失败", message)

    def on_repair_finished(self, payload):
        self.set_download_running(False)
        self.finish_download_task(failed=False)
        version = payload.get("version", "")
        missing_before = payload.get("missing_before", 0)
        missing_after = payload.get("missing_after", 0)
        report_path = payload.get("report_path", "")
        self.repair_progress_bar.setValue(100)
        if missing_after:
            self.repair_status_label.setText("补全完成，但仍有缺失文件")
            detail = f"修复前 {missing_before} 项，剩余 {missing_after} 项"
            if report_path:
                detail += f" | 清单：{report_path}"
            self.repair_metrics_label.setText(detail)
            self.show_warning("补全未完全完成", detail)
            self.log(f"版本补全仍有缺失：{detail}")
        else:
            self.repair_status_label.setText("版本文件已校验并补全")
            self.repair_metrics_label.setText(f"{version} | 修复前缺失/损坏 {missing_before} 项")
            self.show_success("补全完成", f"{version} 的缺失文件已补齐。")
            self.log(f"版本补全完成：{version}，修复前缺失/损坏 {missing_before} 项")

    def on_repair_failed(self, message):
        self.set_download_running(False)
        self.finish_download_task(failed=True)
        self.repair_status_label.setText("补全失败")
        self.repair_metrics_label.setText(message)
        self.show_warning("补全失败", message)
        self.log(f"版本补全失败：{message}")

    def on_modpack_import_finished(self, payload):
        self.set_download_running(False)
        self.finish_download_task(failed=False)
        version = payload.get("version", "")
        alias = payload.get("alias", "")
        if version:
            entry = self.version_settings_entry(version)
            if alias and not (entry.get("alias") or "").strip():
                entry["alias"] = alias
                entry["alias_auto"] = True
            entry["use_isolated_directory"] = True
            save_version_settings(self.version_settings)
        self.refresh_local_versions(show_feedback=False)
        if version:
            self.local_version_combo.setCurrentText(version)
        message = payload.get("message", "整合包导入完成")
        missing_report = payload.get("missing_report", "")
        self.download_metrics_label.setText("整合包导入完成")
        self.install_status_label.setText(message)
        metrics = f"目标版本：{version or '未识别'}"
        if missing_report:
            metrics += f" | 缺失清单：{missing_report}"
        self.install_metrics_label.setText(metrics)
        self.show_success("导入完成", message)
        self.log(message)
        if missing_report:
            self.log(f"整合包缺失文件清单：{missing_report}")

    def on_modpack_import_failed(self, message):
        self.set_download_running(False)
        self.finish_download_task(failed=True)
        self.download_metrics_label.setText(f"导入失败：{message}")
        self.install_status_label.setText("整合包导入失败")
        self.install_metrics_label.setText(message)
        self.show_warning("导入失败", message)
        self.log(f"整合包导入失败：{message}")

    def resource_hit_label(self, hit):
        title = hit.get("title") or hit.get("slug") or hit.get("project_id", "未命名")
        downloads = hit.get("downloads", 0)
        follows = hit.get("follows", 0)
        source = RESOURCE_SOURCE_LABELS.get(hit.get("source", "modrinth"), hit.get("source", ""))
        description = (hit.get("description") or "").replace("\n", " ").strip()
        downloads_text = self.format_bytes(downloads) if hit.get("source") == "local" else downloads
        compatibility = ""
        if hit.get("source") == "modrinth":
            if hit.get("compatibility_checking"):
                compatibility = f" | 正在验证 {hit.get('target_game_version', '')}"
                if hit.get("target_loader"):
                    compatibility += f" / {hit.get('target_loader')}"
            elif hit.get("compatible", True):
                version_label = hit.get("compatible_version", "")
                compatibility = f" | 兼容 {hit.get('target_game_version', '')}"
                if hit.get("target_loader"):
                    compatibility += f" / {hit.get('target_loader')}"
                if version_label:
                    compatibility += f" | 文件 {version_label}"
                elif hit.get("compatibility_unverified"):
                    compatibility += " | 等待验证"
            else:
                compatibility = f" | 不兼容 {hit.get('target_game_version', '')}"
                if hit.get("target_loader"):
                    compatibility += f" / {hit.get('target_loader')}"
        label = f"[{source}] {title} | 下载 {downloads_text} | 收藏 {follows}{compatibility}"
        if description:
            label += f"\n{description[:160]}"
        return label

    def on_resource_search_finished(self, payload):
        original_hits = payload.get("hits", [])
        hits = original_hits
        self.resource_search_hits = hits
        self.resource_result_list.clear()
        for index, hit in enumerate(hits):
            hit["compatibility_index"] = index
            item = QListWidgetItem(self.resource_hit_label(hit))
            item.setData(Qt.ItemDataRole.UserRole, hit)
            self.resource_result_list.addItem(item)
        if not hits:
            if original_hits:
                empty_text = "找到了资源，但没有适配当前安装目标的可安装文件"
            else:
                empty_text = "没有找到匹配资源"
            empty = QListWidgetItem(empty_text)
            empty.setFlags(Qt.ItemFlag.NoItemFlags)
            self.resource_result_list.addItem(empty)
        relaxed = any(hit.get("relaxed_search") for hit in hits)
        suffix = "（已放宽搜索）" if relaxed and hits else ""
        self.resource_status_label.setText(f"搜索完成：{len(hits)} 个结果{suffix}")
        self.show_success("搜索完成", f"找到 {len(hits)} 个资源。")
        if payload.get("source") == "modrinth" and hits:
            self.start_resource_compatibility_check(hits, payload.get("resource_type", "mod"))

    def start_resource_compatibility_check(self, hits, resource_type):
        if self.resource_compat_thread and self.resource_compat_thread.isRunning():
            self.resource_compat_thread.requestInterruption()
            self.resource_compat_thread.quit()
            self.resource_compat_thread.wait(500)
        game_version = hits[0].get("target_game_version", "") if hits else ""
        loader = hits[0].get("target_loader", "") if hits else ""
        self.resource_status_label.setText(f"已显示 {len(hits)} 个结果，正在后台验证兼容文件...")
        self.resource_compat_thread = QThread()
        generation = self.resource_search_generation
        self.resource_compat_worker = ResourceCompatibilityWorker(hits, resource_type, game_version, loader, generation=generation)
        self.resource_compat_worker.moveToThread(self.resource_compat_thread)
        self.resource_compat_thread.started.connect(self.resource_compat_worker.run)
        self.resource_compat_worker.checked.connect(self.on_resource_compatibility_checked)
        self.resource_compat_worker.finished.connect(self.on_resource_compatibility_finished)
        self.resource_compat_worker.failed.connect(self.on_resource_compatibility_failed)
        self.resource_compat_worker.finished.connect(self.resource_compat_thread.quit)
        self.resource_compat_worker.failed.connect(self.resource_compat_thread.quit)
        self.resource_compat_thread.start()

    def on_resource_compatibility_checked(self, hit):
        if hit.get("compatibility_generation") != self.resource_search_generation:
            return
        index = hit.get("compatibility_index", -1)
        if not (0 <= index < len(self.resource_search_hits)):
            return
        self.resource_search_hits[index] = hit
        item = self.resource_result_list.item(index)
        if item is None:
            return
        item.setText(self.resource_hit_label(hit))
        item.setData(Qt.ItemDataRole.UserRole, hit)

    def on_resource_compatibility_finished(self, payload):
        if payload.get("generation") != self.resource_search_generation:
            return
        checked = payload.get("checked", 0)
        compatible = payload.get("compatible", 0)
        self.resource_status_label.setText(f"兼容性验证完成：{compatible}/{checked} 个可安装")
        logger.info("Resource compatibility UI update finished: checked=%d compatible=%d", checked, compatible)

    def on_resource_compatibility_failed(self, message):
        self.resource_status_label.setText(f"兼容性验证失败：{message}")
        self.log(f"资源兼容性验证失败：{message}")

    def on_resource_search_failed(self, message):
        self.resource_status_label.setText(f"搜索失败：{message}")
        self.show_warning("搜索失败", message)
        self.log(f"资源搜索失败：{message}")

    def on_resource_install_finished(self, payload):
        self.set_download_running(False)
        self.finish_download_task(failed=False)
        filename = payload.get("filename", "")
        path = payload.get("path", "")
        self.resource_status_label.setText(f"安装完成：{filename}")
        dependencies = payload.get("dependencies_installed", [])
        suffix = f"，依赖 {len(dependencies)} 个" if dependencies else ""
        self.download_metrics_label.setText(f"资源已安装：{filename}{suffix}")
        self.show_success("安装完成", filename)
        self.log(f"资源已安装：{path}")
        for dependency in dependencies:
            self.log(f"资源依赖已安装：{dependency}")
        target_version = self.current_resource_version()
        if target_version == self.current_selected_version():
            self.populate_version_settings_panel(target_version)

    def on_resource_install_failed(self, message):
        self.set_download_running(False)
        self.finish_download_task(failed=True)
        self.resource_status_label.setText(f"安装失败：{message}")
        self.download_metrics_label.setText(f"资源安装失败：{message}")
        self.show_warning("安装失败", message)
        self.log(f"资源安装失败：{message}")


def main():
    load_config()
    threading.Thread(target=run_flask_app, daemon=True).start()
    qt_app = QApplication(sys.argv)
    configured_theme = config.get("UI", "theme", fallback="dark").strip().lower()
    if configured_theme == "light":
        setTheme(Theme.LIGHT)
    elif configured_theme == "auto":
        setTheme(Theme.AUTO)
    else:
        setTheme(Theme.DARK)
    window = LauncherWindow()
    window.show()
    sys.exit(qt_app.exec())


if __name__ == "__main__":
    main()
