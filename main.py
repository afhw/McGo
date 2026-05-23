import asyncio
import configparser
import json
import os
import re
import sys
import shutil
import subprocess
import threading
import tempfile
import time
import uuid as uuidlib
import webbrowser
import zipfile
from io import BytesIO

import requests
from flask import Flask, request
from PyQt6.QtCore import QObject, Qt, QThread, pyqtSignal as Signal
from PyQt6.QtWidgets import (
    QApplication,
    QAbstractItemView,
    QFileDialog,
    QFrame,
    QGridLayout,
    QHBoxLayout,
    QLabel,
    QMessageBox,
    QListWidget,
    QListWidgetItem,
    QStackedWidget,
    QVBoxLayout,
    QWidget,
)
from qfluentwidgets import (
    BodyLabel,
    CaptionLabel,
    CardWidget,
    CheckBox,
    ComboBox,
    FluentIcon,
    FluentWindow,
    HyperlinkButton,
    InfoBar,
    InfoBarPosition,
    LineEdit,
    PasswordLineEdit,
    Pivot,
    PrimaryPushButton,
    ProgressBar,
    PushButton,
    ScrollArea,
    SegmentedWidget,
    SubtitleLabel,
    TextEdit,
    Theme,
    TitleLabel,
    setTheme,
)

from auth import MicrosoftAuthenticator
from downloader import download_game_files, extract_natives
from java_utils import find_java_paths, get_java_major_version, get_java_version
from launcher import get_local_versions, infer_required_java_version, launch_minecraft, get_version_json
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

MIRROR_SOURCES = {
    "official": "https://launchermeta.mojang.com",
    "bmclapi": "https://bmclapi2.bangbang93.com",
}

config = configparser.ConfigParser()
authenticator = MicrosoftAuthenticator(client_id, redirect_uri)
app = Flask(__name__)


@app.route("/login/callback")
def login_callback():
    authenticator.authorization_code = request.args.get("code")
    return "登录成功，你可以关闭此窗口"


def load_config():
    config.read(config_file)
    defaults = {
        "USER": {"username": "", "uuid": "", "accessToken": ""},
        "DOWNLOAD": {"mirror_source": "official"},
        "AUTH": {
            "use_microsoft_login": "False",
            "refresh_token": "",
            "auto_open_browser": "True",
        },
        "GAME": {"directory": game_directory, "enable_resource_isolation": "True"},
        "UI": {"advanced_mode": "False"},
        "ACCOUNTS": {"selected_account_id": ""},
    }
    for section, values in defaults.items():
        if not config.has_section(section):
            config.add_section(section)
        for key, value in values.items():
            if not config.has_option(section, key):
                config.set(section, key, value)
    save_config()


def save_config():
    with open(config_file, "w") as f:
        config.write(f)


def account_label(account):
    account_type = "Microsoft" if account.get("type") == "microsoft" else "离线"
    return f"{account.get('display_name', account.get('username', '未命名'))} ({account_type})"


def load_accounts():
    if os.path.exists(accounts_file):
        with open(accounts_file, "r", encoding="utf-8") as f:
            return json.load(f)

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
    return accounts


def save_accounts(accounts):
    with open(accounts_file, "w", encoding="utf-8") as f:
        json.dump(accounts, f, ensure_ascii=False, indent=2)


def get_remote_versions(version_type, mirror_source):
    response = requests.get(f"{MIRROR_SOURCES[mirror_source]}/mc/game/version_manifest.json", timeout=30)
    response.raise_for_status()
    return [v["id"] for v in response.json()["versions"] if v["type"] == version_type]


def get_version_url(version_id, mirror_source):
    response = requests.get(f"{MIRROR_SOURCES[mirror_source]}/mc/game/version_manifest.json", timeout=30)
    response.raise_for_status()
    for version in response.json()["versions"]:
        if version["id"] == version_id:
            return version["url"]
    return None


def mirror_root(mirror_source):
    return MIRROR_SOURCES.get(mirror_source, MIRROR_SOURCES["official"])


def stream_download(url, file_path, progress_callback=None, status_label="下载中"):
    os.makedirs(os.path.dirname(file_path), exist_ok=True)
    with requests.get(url, stream=True, timeout=60) as response:
        response.raise_for_status()
        total = int(response.headers.get("content-length") or 0)
        downloaded = 0
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
        }


def run_flask_app():
    app.run(port=5000, debug=False, use_reloader=False)


class DownloadWorker(QObject):
    progress = Signal(int)
    metrics = Signal(dict)
    status = Signal(str)
    install_metrics = Signal(dict)
    install_status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, version_id, mirror_source, game_dir, auto_install_types=None, java_path=""):
        super().__init__()
        self.version_id = version_id
        self.mirror_source = mirror_source
        self.game_dir = os.path.abspath(game_dir)
        self.auto_install_types = list(auto_install_types or [])
        self.java_path = java_path

    def run(self):
        try:
            self.status.emit(f"正在获取 {self.version_id} 版本信息...")
            version_url = get_version_url(self.version_id, self.mirror_source)
            if not version_url:
                raise Exception(f"无法获取版本 {self.version_id} 的下载地址")

            response = requests.get(version_url, timeout=30)
            response.raise_for_status()
            version_json = response.json()

            def on_progress(snapshot):
                value = snapshot.get("progress", 0.0)
                self.progress.emit(max(0, min(100, int(value * 100))))
                self.metrics.emit(snapshot)

            self.status.emit(f"正在下载 Minecraft {self.version_id}...")
            asyncio.run(download_game_files(
                version_json,
                self.game_dir,
                self.version_id,
                MIRROR_SOURCES[self.mirror_source],
                progress_callback=on_progress,
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
            self.finished.emit(payload)
        except Exception as e:
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
            engine = InstallerEngine(
                self.minecraft_version,
                self.mirror_source,
                self.game_dir,
                self.java_path,
                status_callback=self.status.emit,
                progress_callback=self.emit_snapshot,
            )
            payload = engine.install(self.install_type)
            self.progress.emit(100)
            self.finished.emit(payload)
        except Exception as exc:
            self.failed.emit(str(exc))


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
            asyncio.run(authenticator.authenticate())
            uuid, username, _ = asyncio.run(authenticator.get_minecraft_profile())
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
            self.failed.emit(str(e))


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
            self.finished.emit(payload)
        except Exception as exc:
            self.failed.emit(str(exc))


class LaunchWorker(QObject):
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, java_path, version, game_dir, account, runtime_directory, extra_jvm_args=None):
        super().__init__()
        self.java_path = java_path
        self.version = version
        self.game_dir = game_dir
        self.account = dict(account)
        self.runtime_directory = runtime_directory
        self.extra_jvm_args = list(extra_jvm_args or [])

    def run(self):
        try:
            account = dict(self.account)
            username = account.get("username") or None
            uuid = account.get("uuid") or None
            token = account.get("access_token") or None

            if account.get("type") == "offline":
                if not username:
                    raise Exception("离线账号需要填写用户名。")
            else:
                refresh_token = account.get("refresh_token", "")
                if not refresh_token:
                    raise Exception("该 Microsoft 账号没有可用的刷新令牌，请重新登录。")

                self.status.emit("正在刷新 Microsoft 登录状态...")
                session = MicrosoftAuthenticator(client_id, redirect_uri)
                asyncio.run(session.refresh_access_token(refresh_token))
                uuid, username, _ = asyncio.run(session.get_minecraft_profile())
                token = session.minecraft_access_token
                account["refresh_token"] = session.refresh_token
                account["username"] = username
                account["uuid"] = uuid
                account["display_name"] = username

            self.status.emit(f"正在启动 Minecraft {self.version}...")
            launched = launch_minecraft(
                self.java_path,
                self.version,
                self.game_dir,
                token,
                username,
                uuid,
                runtime_directory=self.runtime_directory,
                extra_jvm_args=self.extra_jvm_args,
            )
            if not launched:
                raise Exception(f"本地未找到版本 {self.version}，请先下载。")

            self.finished.emit({
                "version": self.version,
                "account": account,
                "username": username,
            })
        except Exception as exc:
            self.failed.emit(str(exc))


class Page(ScrollArea):
    def __init__(self, object_name, title, subtitle):
        super().__init__()
        self.setObjectName(object_name)
        self.view = QWidget()
        self.view.setStyleSheet("background: transparent;")
        self.layout = QVBoxLayout(self.view)
        self.layout.setContentsMargins(32, 28, 32, 32)
        self.layout.setSpacing(18)
        self.layout.addWidget(TitleLabel(title))
        self.layout.addWidget(CaptionLabel(subtitle))
        self.setWidget(self.view)
        self.setWidgetResizable(True)
        self.setFrameShape(QFrame.Shape.NoFrame)


class LauncherWindow(FluentWindow):
    def __init__(self):
        super().__init__()
        self.download_thread = None
        self.download_worker = None
        self.install_thread = None
        self.install_worker = None
        self.auth_thread = None
        self.auth_worker = None
        self.launch_thread = None
        self.launch_worker = None
        self.scan_threads = {}
        self.scan_workers = {}
        self.java_versions = {}
        self.accounts = load_accounts()
        self.version_settings = load_version_settings()
        self.account_index_ids = []
        self.manage_account_index_ids = []
        self.delete_account_index_ids = []
        self.version_display_ids = []
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
        self.init_navigation()
        self.refresh_account_selector()
        self.refresh_java_paths()
        self.refresh_local_versions()
        self.remote_version_combo.addItem("点击刷新远程版本")
        self.log("QFluentWidgets 界面已启动。远程版本列表已延后加载。")

    def build_controls(self):
        self.java_combo = ComboBox()
        self.java_combo.currentTextChanged.connect(self.on_java_selected)
        self.java_version_label = BodyLabel("未选择 Java")
        self.version_category_combo = ComboBox()
        self.version_category_combo.addItems(["全部版本", "原版", "仅 OptiFine", "可安装 Mod"])
        self.version_category_combo.currentTextChanged.connect(lambda _: self.refresh_local_versions())
        self.version_display_combo = ComboBox()
        self.version_display_combo.currentTextChanged.connect(self.on_version_display_selected)
        self.local_version_combo = ComboBox()
        self.local_version_combo.currentTextChanged.connect(self.on_local_version_changed)
        self.remote_version_combo = ComboBox()
        self.install_type_combo = ComboBox()
        self.install_type_combo.addItems(["fabric", "forge", "neoforge", "optifine", "fabric_api"])
        self.install_type_combo.currentTextChanged.connect(self.update_install_button_text)
        self.install_type_combo.currentTextChanged.connect(lambda _: self.refresh_install_versions())
        self.install_version_combo = ComboBox()
        self.version_type_combo = ComboBox()
        self.version_type_combo.addItems(["release", "snapshot", "old_alpha", "old_beta"])
        self.mirror_combo = ComboBox()
        self.mirror_combo.addItems(list(MIRROR_SOURCES.keys()))
        self.mirror_combo.setCurrentText(config.get("DOWNLOAD", "mirror_source", fallback="official"))
        self.login_mode_combo = ComboBox()
        self.login_mode_combo.addItems(["offline", "microsoft"])
        self.login_mode_combo.setCurrentText("microsoft" if config.getboolean("AUTH", "use_microsoft_login", fallback=False) else "offline")
        self.login_mode_combo.currentTextChanged.connect(self.update_account_field_visibility)
        self.account_combo = ComboBox()
        self.account_combo.currentTextChanged.connect(self.on_account_selected)
        self.manage_account_combo = ComboBox()
        self.manage_account_combo.currentTextChanged.connect(self.on_manage_account_selected)
        self.username_input = LineEdit()
        self.username_input.setText(config.get("USER", "username", fallback=""))
        self.uuid_input = LineEdit()
        self.uuid_input.setText(config.get("USER", "uuid", fallback=""))
        self.access_token_input = PasswordLineEdit()
        self.access_token_input.setText(config.get("USER", "accessToken", fallback=""))
        self.advanced_mode_check = CheckBox("高级模式：显示更多启动器选项")
        self.advanced_mode_check.setChecked(config.getboolean("UI", "advanced_mode", fallback=False))
        self.advanced_mode_check.stateChanged.connect(self.on_advanced_mode_changed)
        self.auto_open_browser_check = CheckBox("Microsoft 登录时自动打开浏览器")
        self.auto_open_browser_check.setChecked(config.getboolean("AUTH", "auto_open_browser", fallback=True))
        self.resource_isolation_check = CheckBox("启用资源隔离（每个版本使用独立运行目录）")
        self.resource_isolation_check.setChecked(config.getboolean("GAME", "enable_resource_isolation", fallback=False))
        self.resource_isolation_check.stateChanged.connect(lambda _: self.on_local_version_changed(self.current_selected_version()))
        self.game_dir_input = LineEdit()
        self.game_dir_input.setText(config.get("GAME", "directory", fallback=game_directory))
        self.login_link_input = LineEdit()
        self.login_link_input.setReadOnly(True)
        self.login_link_input.setPlaceholderText("关闭自动打开后，Microsoft 登录链接会显示在这里")
        self.login_link_button = HyperlinkButton("", "打开登录链接")
        self.login_link_button.setVisible(False)
        self.delete_account_combo = ComboBox()
        self.progress_bar = ProgressBar()
        self.progress_bar.setRange(0, 100)
        self.download_metrics_label = BodyLabel("等待下载")
        self.install_status_label = BodyLabel("等待安装任务")
        self.install_metrics_label = BodyLabel("尚未开始安装")
        self.launch_status_label = BodyLabel("将根据游戏版本自动选择合适的 Java")
        self.version_summary_label = BodyLabel("未选择版本")
        self.version_alias_input = LineEdit()
        self.version_alias_input.setPlaceholderText("给当前版本起一个更容易识别的名称")
        self.version_jvm_args_input = LineEdit()
        self.version_jvm_args_input.setPlaceholderText("-XX:-OmitStackTraceInFastThrow -Djdk.lang.Process.allowAmbiguousCommands=True -Dfml.ignoreInvalidMinecraftCertificates=True -Dfml.ignorePatchDiscrepancies=True")
        self.version_custom_dir_input = LineEdit()
        self.version_custom_dir_input.setPlaceholderText("留空则使用默认游戏目录或资源隔离目录")
        self.version_isolation_check = CheckBox("当前版本单独使用资源隔离目录")
        self.version_mods_list = QListWidget()
        self.version_mods_list.setSelectionMode(QAbstractItemView.SelectionMode.SingleSelection)
        self.install_log = TextEdit()
        self.install_log.setReadOnly(True)
        self.status_log = TextEdit()
        self.status_log.setReadOnly(True)
        self.version_type_combo.currentTextChanged.connect(lambda _: self.log("版本类型已更改，点击“刷新远程版本”重新加载列表。"))
        self.download_loader_checks = {
            "fabric": CheckBox("下载完成后安装 Fabric"),
            "forge": CheckBox("下载完成后安装 Forge"),
            "neoforge": CheckBox("下载完成后安装 NeoForge"),
            "optifine": CheckBox("下载完成后安装 OptiFine"),
            "fabric_api": CheckBox("若已安装 Fabric，同时安装 Fabric API"),
        }
        for install_type, checkbox in self.download_loader_checks.items():
            checkbox.stateChanged.connect(lambda _, current=install_type: self.on_download_addon_changed(current))
        self.download_addon_hint_label = CaptionLabel("可在下载原版后自动继续安装；Fabric API 仅在 Fabric 一起安装时可用。")

    def build_pages(self):
        self.home_page = Page("homePage", "McGo", "一个 Fluent 风格的 Minecraft 启动器")
        self.launch_page = Page("launchPage", "启动游戏", "选择 Java 和本地版本，然后启动 Minecraft")
        self.download_page = Page("downloadPage", "下载游戏", "选择版本类型、镜像源和目标版本")
        self.manage_page = Page("managePage", "管理中心", "将账号、环境和日志按任务分组，减少来回切页")

        self.build_home_page()
        self.build_launch_page()
        self.build_download_page()
        self.build_manage_page()

    def init_navigation(self):
        self.addSubInterface(self.home_page, FluentIcon.HOME, "首页")
        self.addSubInterface(self.launch_page, FluentIcon.GAME, "启动")
        self.addSubInterface(self.download_page, FluentIcon.DOWNLOAD, "下载")
        self.addSubInterface(self.manage_page, FluentIcon.SETTING, "管理")

    def make_card(self, title, subtitle=None):
        card = CardWidget()
        layout = QVBoxLayout(card)
        layout.setContentsMargins(22, 18, 22, 18)
        layout.setSpacing(12)
        layout.addWidget(SubtitleLabel(title))
        if subtitle:
            layout.addWidget(CaptionLabel(subtitle))
        return card, layout

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
        manage_button.clicked.connect(lambda: self.switchTo(self.manage_page))
        refresh_button.clicked.connect(self.refresh_all)
        download_button.clicked.connect(lambda: self.switchTo(self.download_page))
        launch_button.clicked.connect(lambda: self.switchTo(self.launch_page))
        row.addWidget(manage_button)
        row.addWidget(download_button)
        row.addWidget(launch_button)
        row.addWidget(refresh_button)
        row.addStretch()
        quick_layout.addLayout(row)

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
        self.home_page.layout.addWidget(overview_card)
        self.home_page.layout.addStretch()

    def build_launch_page(self):
        start_card, start_layout = self.make_card("立即启动", "先选账号与版本分类，再从版本中心确认要启动的版本")
        self.add_labeled_control(start_layout, "当前账号", self.account_combo)
        self.add_labeled_control(start_layout, "版本分类", self.version_category_combo)
        self.add_labeled_control(start_layout, "当前版本", self.version_display_combo)
        start_layout.addWidget(self.version_summary_label)
        start_layout.addWidget(self.launch_status_label)
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

        nav_card = CardWidget()
        nav_layout = QVBoxLayout(nav_card)
        nav_layout.setContentsMargins(22, 18, 22, 18)
        nav_layout.setSpacing(12)
        nav_layout.addWidget(SubtitleLabel("版本中心"))
        nav_layout.addWidget(CaptionLabel("把版本选择、版本设置与 Mod 管理分开，避免启动前在一页里找来找去"))
        self.version_segment = SegmentedWidget()
        nav_layout.addWidget(self.version_segment)

        self.version_stack = QStackedWidget()
        self.version_selector_view = QWidget()
        self.version_settings_view = QWidget()

        selector_card, selector_layout = self.make_card("选择版本", "按分类查看本地版本，原版、可安装 Mod 和仅 OptiFine 会单独归类")
        self.add_labeled_control(selector_layout, "版本分类", self.version_category_combo)
        self.add_labeled_control(selector_layout, "版本列表", self.version_display_combo)
        selector_layout.addWidget(self.version_summary_label)

        settings_card, settings_layout = self.make_card("版本设置", "为当前版本单独设置名称、JVM 参数、运行目录与 Mod 列表")
        self.add_labeled_control(settings_layout, "显示名称", self.version_alias_input)
        self.add_labeled_control(settings_layout, "额外 JVM 参数", self.version_jvm_args_input)
        settings_layout.addWidget(self.version_isolation_check)
        self.add_labeled_control(settings_layout, "自定义运行目录", self.version_custom_dir_input)
        settings_layout.addWidget(BodyLabel("Mod 列表"))
        settings_layout.addWidget(self.version_mods_list)
        settings_row = QHBoxLayout()
        self.save_version_settings_button = PrimaryPushButton("保存当前版本设置")
        self.open_mods_button = PushButton("打开 mods 文件夹")
        self.toggle_mod_button = PushButton("启用/禁用所选 Mod")
        self.delete_mod_button = PushButton("删除所选 Mod")
        self.save_version_settings_button.clicked.connect(self.save_current_version_settings)
        self.open_mods_button.clicked.connect(self.open_current_mods_directory)
        self.toggle_mod_button.clicked.connect(self.toggle_selected_mod)
        self.delete_mod_button.clicked.connect(self.delete_selected_mod)
        settings_row.addWidget(self.save_version_settings_button)
        settings_row.addWidget(self.open_mods_button)
        settings_row.addWidget(self.toggle_mod_button)
        settings_row.addWidget(self.delete_mod_button)
        settings_row.addStretch()
        settings_layout.addLayout(settings_row)

        selector_view_layout = QVBoxLayout(self.version_selector_view)
        selector_view_layout.setContentsMargins(0, 0, 0, 0)
        selector_view_layout.addWidget(selector_card)
        selector_view_layout.addStretch()

        settings_view_layout = QVBoxLayout(self.version_settings_view)
        settings_view_layout.setContentsMargins(0, 0, 0, 0)
        settings_view_layout.addWidget(settings_card)
        settings_view_layout.addStretch()

        self.version_stack.addWidget(self.version_selector_view)
        self.version_stack.addWidget(self.version_settings_view)
        self.version_segment.addItem("selector", "选择版本", lambda: self.switch_version_section("selector"))
        self.version_segment.addItem("settings", "版本设置", lambda: self.switch_version_section("settings"))
        self.version_segment.setCurrentItem("selector")

        self.launch_page.layout.addWidget(start_card)
        self.launch_page.layout.addWidget(nav_card)
        self.launch_page.layout.addWidget(self.version_stack)
        self.launch_page.layout.addWidget(env_card)
        self.launch_page.layout.addStretch()

    def build_download_page(self):
        progress_card, progress_layout = self.make_card("任务进度", "下载和扩展安装共用这一组进度与状态信息")
        progress_layout.addWidget(self.progress_bar)
        progress_layout.addWidget(self.download_metrics_label)

        nav_card = CardWidget()
        nav_layout = QVBoxLayout(nav_card)
        nav_layout.setContentsMargins(22, 18, 22, 18)
        nav_layout.setSpacing(12)
        nav_layout.addWidget(SubtitleLabel("下载任务"))
        nav_layout.addWidget(CaptionLabel("先下载原版，再按需要安装 Fabric / Forge / NeoForge / OptiFine 等扩展"))
        self.download_segment = SegmentedWidget()
        nav_layout.addWidget(self.download_segment)

        self.download_stack = QStackedWidget()
        self.download_vanilla_view = QWidget()
        self.download_install_view = QWidget()

        download_card, download_layout = self.make_card("下载原版", "原版下载支持一并勾选后续安装项，减少重复操作")
        self.add_labeled_control(download_layout, "版本类型", self.version_type_combo)
        self.add_labeled_control(download_layout, "远程版本", self.remote_version_combo)
        self.add_labeled_control(download_layout, "镜像源", self.mirror_combo)
        addon_row = QHBoxLayout()
        for checkbox in self.download_loader_checks.values():
            addon_row.addWidget(checkbox)
        addon_row.addStretch()
        addon_container = QWidget()
        addon_container.setLayout(addon_row)
        self.add_labeled_control(download_layout, "下载后继续", addon_container)
        download_layout.addWidget(self.download_addon_hint_label)
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

        vanilla_layout = QVBoxLayout(self.download_vanilla_view)
        vanilla_layout.setContentsMargins(0, 0, 0, 0)
        vanilla_layout.addWidget(download_card)
        vanilla_layout.addStretch()

        install_view_layout = QVBoxLayout(self.download_install_view)
        install_view_layout.setContentsMargins(0, 0, 0, 0)
        install_view_layout.addWidget(install_card)
        install_view_layout.addStretch()

        self.download_stack.addWidget(self.download_vanilla_view)
        self.download_stack.addWidget(self.download_install_view)
        self.download_segment.addItem("vanilla", "下载原版", lambda: self.switch_download_section("vanilla"))
        self.download_segment.addItem("addons", "安装扩展", lambda: self.switch_download_section("addons"))
        self.download_segment.setCurrentItem("vanilla")

        self.update_install_button_text(self.install_type_combo.currentText())
        self.update_download_addon_controls()

        self.download_page.layout.addWidget(progress_card)
        self.download_page.layout.addWidget(nav_card)
        self.download_page.layout.addWidget(self.download_stack)
        self.download_page.layout.addStretch()

    def build_account_section(self):
        nav_card = CardWidget()
        nav_layout = QVBoxLayout(nav_card)
        nav_layout.setContentsMargins(22, 18, 22, 18)
        nav_layout.setSpacing(12)
        nav_layout.addWidget(SubtitleLabel("账号操作"))
        nav_layout.addWidget(CaptionLabel("先选当前账号，再进入离线或 Microsoft 分页处理新增、更新与登录"))
        self.account_segment = SegmentedWidget()
        nav_layout.addWidget(self.account_segment)

        self.account_stack = QStackedWidget()
        self.account_overview_view = QWidget()
        self.account_offline_view = QWidget()
        self.account_microsoft_view = QWidget()

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
        self.login_link_button.clicked.connect(lambda: webbrowser.open(self.login_link_input.text().strip()))
        login_button.clicked.connect(self.start_microsoft_login)
        copy_link_button.clicked.connect(self.copy_login_link)
        link_button_row.addWidget(login_button)
        link_button_row.addWidget(copy_link_button)
        link_button_row.addWidget(self.login_link_button)
        link_button_row.addStretch()
        microsoft_layout.addLayout(link_button_row)

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

        self.account_stack.addWidget(self.account_overview_view)
        self.account_stack.addWidget(self.account_offline_view)
        self.account_stack.addWidget(self.account_microsoft_view)
        self.account_segment.addItem("overview", "当前账号", lambda: self.switch_account_section("overview"))
        self.account_segment.addItem("offline", "离线账号", lambda: self.switch_account_section("offline"))
        self.account_segment.addItem("microsoft", "Microsoft", lambda: self.switch_account_section("microsoft"))
        self.account_segment.setCurrentItem("overview")

        container = QWidget()
        container_layout = QVBoxLayout(container)
        container_layout.setContentsMargins(0, 0, 0, 0)
        container_layout.setSpacing(18)
        container_layout.addWidget(nav_card)
        container_layout.addWidget(self.account_stack)
        return container

    def build_environment_section(self):
        game_card, game_layout = self.make_card("环境与目录", "Java、游戏目录和隔离运行都放在这里")
        self.add_labeled_control(game_layout, "游戏目录", self.game_dir_input)
        game_layout.addWidget(self.advanced_mode_check)
        game_layout.addWidget(self.resource_isolation_check)
        self.add_labeled_control(game_layout, "Java 路径", self.java_combo)
        game_layout.addWidget(self.java_version_label)
        game_row = QHBoxLayout()
        choose_button = PushButton("选择目录")
        open_button = PushButton("打开目录")
        refresh_java_button = PushButton("刷新 Java")
        refresh_versions_button = PushButton("刷新本地版本")
        choose_button.clicked.connect(self.choose_game_directory)
        open_button.clicked.connect(self.open_game_directory)
        refresh_java_button.clicked.connect(self.refresh_java_paths)
        refresh_versions_button.clicked.connect(self.refresh_local_versions)
        game_row.addWidget(choose_button)
        game_row.addWidget(open_button)
        game_row.addWidget(refresh_java_button)
        game_row.addWidget(refresh_versions_button)
        game_row.addStretch()
        game_layout.addLayout(game_row)
        return game_card

    def build_log_section(self):
        card, layout = self.make_card("状态日志", "把下载、登录和启动日志集中到一个分页里，避免单独切主菜单")
        layout.addWidget(self.status_log)
        return card

    def build_manage_page(self):
        nav_card = CardWidget()
        nav_layout = QVBoxLayout(nav_card)
        nav_layout.setContentsMargins(22, 18, 22, 18)
        nav_layout.setSpacing(12)
        nav_layout.addWidget(SubtitleLabel("管理分区"))
        nav_layout.addWidget(CaptionLabel("按用途分区后，常用操作会更容易找到，也更不容易改错"))

        self.manage_pivot = Pivot()
        nav_layout.addWidget(self.manage_pivot)

        self.manage_stack = QStackedWidget()
        self.account_manage_view = QWidget()
        self.environment_manage_view = QWidget()
        self.log_manage_view = QWidget()

        account_layout = QVBoxLayout(self.account_manage_view)
        account_layout.setContentsMargins(0, 0, 0, 0)
        account_layout.addWidget(self.build_account_section())
        account_layout.addStretch()

        environment_layout = QVBoxLayout(self.environment_manage_view)
        environment_layout.setContentsMargins(0, 0, 0, 0)
        environment_layout.addWidget(self.build_environment_section())
        environment_layout.addStretch()

        log_layout = QVBoxLayout(self.log_manage_view)
        log_layout.setContentsMargins(0, 0, 0, 0)
        log_layout.addWidget(self.build_log_section())
        log_layout.addStretch()

        self.manage_stack.addWidget(self.account_manage_view)
        self.manage_stack.addWidget(self.environment_manage_view)
        self.manage_stack.addWidget(self.log_manage_view)

        self.manage_pivot.addItem("accounts", "账号", lambda: self.switch_manage_section("accounts"))
        self.manage_pivot.addItem("environment", "环境", lambda: self.switch_manage_section("environment"))
        self.manage_pivot.addItem("logs", "日志", lambda: self.switch_manage_section("logs"))
        self.manage_pivot.setCurrentItem("accounts")

        self.manage_page.layout.addWidget(nav_card)
        self.manage_page.layout.addWidget(self.manage_stack)
        self.manage_page.layout.addStretch()

    def log(self, message):
        self.status_log.append(message)

    def log_install(self, message):
        self.install_log.append(message)

    def switch_manage_section(self, section_key):
        mapping = {
            "accounts": 0,
            "environment": 1,
            "logs": 2,
        }
        index = mapping.get(section_key, 0)
        self.manage_stack.setCurrentIndex(index)
        self.manage_pivot.setCurrentItem(section_key)

    def switch_version_section(self, section_key):
        mapping = {
            "selector": 0,
            "settings": 1,
        }
        index = mapping.get(section_key, 0)
        if hasattr(self, "version_stack"):
            self.version_stack.setCurrentIndex(index)
        if hasattr(self, "version_segment"):
            self.version_segment.setCurrentItem(section_key if section_key in mapping else "selector")

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
        return resolve_base_minecraft_version(self.current_game_dir(), version_id)

    def version_matches_category(self, version_id, category):
        return version_matches_category(version_id, category)

    def current_selected_version(self):
        return self.local_version_combo.currentText().strip()

    def on_version_display_selected(self, version_label):
        current_index = self.version_display_combo.currentIndex()
        version_id = self.version_display_ids[current_index] if 0 <= current_index < len(self.version_display_ids) else ""
        self.local_version_combo.blockSignals(True)
        self.local_version_combo.setCurrentText(version_id)
        self.local_version_combo.blockSignals(False)
        self.on_local_version_changed(version_id)

    def populate_version_settings_panel(self, version_id):
        entry = self.version_settings_entry(version_id)
        self.version_alias_input.setText(entry.get("alias", ""))
        self.version_jvm_args_input.setText(entry.get("jvm_args", ""))
        self.version_custom_dir_input.setText(entry.get("runtime_directory", ""))
        forced_isolation = self.resource_isolation_check.isChecked()
        self.version_isolation_check.setChecked(bool(forced_isolation or entry.get("use_isolated_directory", False)))
        self.version_isolation_check.setEnabled(not forced_isolation)

        runtime_directory = self.runtime_directory_for_version(version_id)
        mods_dir = self.current_mods_directory()
        self.version_mods_list.clear()
        if os.path.isdir(mods_dir):
            for item in sorted(os.listdir(mods_dir)):
                lowered = item.lower()
                if not (lowered.endswith(".jar") or lowered.endswith(".jar.disabled")):
                    continue
                enabled = lowered.endswith(".jar")
                label = f"{'启用' if enabled else '禁用'} | {item}"
                widget_item = QListWidgetItem(label)
                widget_item.setData(Qt.ItemDataRole.UserRole, os.path.join(mods_dir, item))
                self.version_mods_list.addItem(widget_item)
        if self.version_mods_list.count() == 0:
            empty_item = QListWidgetItem("当前没有检测到 Mod 文件")
            empty_item.setFlags(Qt.ItemFlag.NoItemFlags)
            self.version_mods_list.addItem(empty_item)

        summary = [
            f"当前版本：{self.version_display_name(version_id)}",
            f"基础版本：{self.base_version_for(version_id)}",
            f"运行目录：{runtime_directory}",
        ]
        summary.append(f"类型：{version_type_label(version_id)}")
        self.version_summary_label.setText(" | ".join(summary))

    def save_current_version_settings(self):
        version_id = self.current_selected_version()
        if not version_id:
            self.show_warning("缺少版本", "请先选择一个本地版本。")
            return
        entry = self.version_settings_entry(version_id)
        entry["alias"] = self.version_alias_input.text().strip()
        entry["jvm_args"] = self.version_jvm_args_input.text().strip()
        entry["runtime_directory"] = self.version_custom_dir_input.text().strip()
        entry["use_isolated_directory"] = self.version_isolation_check.isChecked()
        save_version_settings(self.version_settings)
        self.refresh_local_versions()
        self.populate_version_settings_panel(version_id)
        self.show_success("版本设置已保存", self.version_display_name(version_id))

    def current_mods_directory(self):
        version_id = self.current_selected_version()
        return mods_directory_for_version(
            self.current_game_dir(),
            self.version_settings,
            version_id,
            global_isolation=self.resource_isolation_check.isChecked(),
        )

    def open_current_mods_directory(self):
        version_id = self.current_selected_version()
        if not version_id:
            self.show_warning("缺少版本", "请先选择一个本地版本。")
            return
        mods_dir = self.current_mods_directory()
        os.makedirs(mods_dir, exist_ok=True)
        os.startfile(os.path.abspath(mods_dir))

    def toggle_selected_mod(self):
        current_item = self.version_mods_list.currentItem()
        mod_path = current_item.data(Qt.ItemDataRole.UserRole) if current_item else ""
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
        current_item = self.version_mods_list.currentItem()
        mod_path = current_item.data(Qt.ItemDataRole.UserRole) if current_item else ""
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

    def set_download_running(self, running):
        if hasattr(self, "download_button"):
            self.download_button.setEnabled(not running)
        if hasattr(self, "refresh_remote_button"):
            self.refresh_remote_button.setEnabled(not running)
        if hasattr(self, "install_button"):
            self.install_button.setEnabled(not running)
        if hasattr(self, "refresh_install_versions_button"):
            self.refresh_install_versions_button.setEnabled(not running)
        if hasattr(self, "install_type_combo"):
            self.install_type_combo.setEnabled(not running)
        if hasattr(self, "install_version_combo"):
            self.install_version_combo.setEnabled(not running)
        if hasattr(self, "download_loader_checks"):
            for checkbox in self.download_loader_checks.values():
                checkbox.setEnabled(not running)
            if not running:
                self.update_download_addon_controls()

    def set_launch_running(self, running):
        if hasattr(self, "launch_button"):
            self.launch_button.setEnabled(not running)
        self.java_combo.setEnabled(not running)
        self.local_version_combo.setEnabled(not running)
        self.account_combo.setEnabled(not running)

    def show_success(self, title, content):
        InfoBar.success(title, content, duration=2500, position=InfoBarPosition.TOP_RIGHT, parent=self)

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

    def populate_selected_account_fields(self):
        account = self.current_account()
        if not account:
            self.update_account_field_visibility()
            return
        self.login_mode_combo.setCurrentText(account.get("type", "offline"))
        self.username_input.setText(account.get("username", ""))
        self.uuid_input.setText(account.get("uuid", ""))
        self.access_token_input.setText(account.get("access_token", ""))
        self.update_account_field_visibility()

    def update_account_field_visibility(self, *_):
        account = self.current_account()
        selected_mode = self.login_mode_combo.currentText()
        account_type = selected_mode if selected_mode == "offline" else (account.get("type") if account else selected_mode)
        is_offline = account_type == "offline"
        is_microsoft = account_type == "microsoft"
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
        if hasattr(self, "account_segment"):
            target = "offline" if is_offline else "microsoft"
            current_item = self.account_segment.currentItem()
            current_route = getattr(current_item, "routeKey", "") if current_item else ""
            if current_route in {"offline", "microsoft"}:
                self.switch_account_section(target)

    def switch_download_section(self, section_key):
        mapping = {
            "vanilla": 0,
            "addons": 1,
        }
        index = mapping.get(section_key, 0)
        if hasattr(self, "download_stack"):
            self.download_stack.setCurrentIndex(index)
        if hasattr(self, "download_segment"):
            self.download_segment.setCurrentItem(section_key if section_key in mapping else "vanilla")

    def switch_account_section(self, section_key):
        mapping = {
            "overview": 0,
            "offline": 1,
            "microsoft": 2,
        }
        index = mapping.get(section_key, 0)
        if section_key in {"offline", "microsoft"} and hasattr(self, "login_mode_combo"):
            self.login_mode_combo.blockSignals(True)
            self.login_mode_combo.setCurrentText(section_key)
            self.login_mode_combo.blockSignals(False)
        if hasattr(self, "account_stack"):
            self.account_stack.setCurrentIndex(index)
        if hasattr(self, "account_segment"):
            self.account_segment.setCurrentItem(section_key if section_key in mapping else "overview")
        self.update_account_field_visibility()

    def on_advanced_mode_changed(self, *_):
        config["UI"]["advanced_mode"] = str(self.advanced_mode_check.isChecked())
        save_config()
        self.update_account_field_visibility()

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
            if hasattr(self, "login_link_button"):
                self.login_link_button.setUrl(login_url)
        QApplication.clipboard().setText(login_url)
        self.show_success("已复制", "Microsoft 登录链接已复制到剪贴板。")

    def on_login_url_ready(self, login_url):
        self.login_link_input.setText(login_url)
        self.login_link_button.setUrl(login_url)
        self.log(f"Microsoft 登录链接：{login_url}")
        self.update_account_field_visibility()
        if not self.auto_open_browser_check.isChecked():
            QMessageBox.information(
                self,
                "Microsoft 登录链接",
                "已生成登录链接。请复制或手动打开管理中心的账号分页中的链接完成登录。",
            )

    def current_game_dir(self):
        return self.game_dir_input.text().strip() or game_directory

    def start_scan_task(self, task_type, game_dir="", version_type="", mirror_source="official"):
        existing_thread = self.scan_threads.get(task_type)
        if existing_thread and existing_thread.isRunning():
            return

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

    def clear_scan_task(self, task_type):
        self.scan_threads.pop(task_type, None)
        self.scan_workers.pop(task_type, None)

    def on_scan_finished(self, payload):
        task = payload.get("task", "")
        if task == "java":
            paths = payload.get("paths", [])
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
            return

        if task == "local_versions":
            versions = payload.get("versions", [])
            self.local_version_combo.clear()
            self.local_version_combo.addItems(versions)
            current_category = self.version_category_combo.currentText().strip() if hasattr(self, "version_category_combo") else "全部版本"
            filtered = [version for version in versions if self.version_matches_category(version, current_category)]
            last_version = self.version_settings.get("_meta", {}).get("last_launched_version", "")
            current_version = self.current_selected_version()
            self.version_display_combo.blockSignals(True)
            self.version_display_combo.clear()
            self.version_display_ids = list(filtered)
            self.version_display_combo.addItems([self.version_display_name(version) for version in filtered])
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
            self.version_display_combo.blockSignals(False)
            if selected_version:
                local_index = self.local_version_combo.findText(selected_version)
                if local_index >= 0:
                    self.local_version_combo.setCurrentIndex(local_index)
            self.refresh_install_versions(versions)
            self.log(f"本地版本数量：{len(versions)}")
            if versions:
                self.on_local_version_changed(self.local_version_combo.currentText().strip())
            else:
                self.launch_status_label.setText("当前游戏目录下没有可启动的本地版本")
            self.update_home_summary()
            return

        if task == "remote_versions":
            versions = payload.get("versions", [])
            self.remote_version_combo.clear()
            self.remote_version_combo.addItems(versions)
            self.log(f"远程版本数量：{len(versions)}")
            self.update_home_summary()

    def on_scan_failed(self, task_type, message):
        if task_type == "remote_versions":
            self.log(f"刷新远程版本失败：{message}")
            self.show_warning("刷新失败", message)
            return
        self.log(f"{task_type} 扫描失败：{message}")

    def refresh_all(self):
        self.refresh_java_paths()
        self.refresh_local_versions()
        self.log("本地状态已刷新；远程版本请在下载页手动刷新。")

    def update_home_summary(self):
        account = self.current_account()
        account_text = account_label(account) if account else "未选择"
        self.home_account_label.setText(f"账号：{account_text}")
        if hasattr(self, "account_summary_label"):
            self.account_summary_label.setText(f"当前账号：{account_text}")
        self.home_java_label.setText(f"Java：{self.java_combo.count()} 个")
        self.home_local_label.setText(f"本地版本：{self.local_version_combo.count()}")
        self.home_remote_label.setText(f"远程版本：{self.remote_version_combo.count()}")
        self.home_dir_label.setText(f"游戏目录：{self.current_game_dir()}")

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

    def refresh_java_paths(self):
        self.start_scan_task("java")

    def update_java_version(self, path):
        if not path:
            self.java_version_label.setText("未选择 Java")
            return
        version = get_java_version(path)
        self.java_version_label.setText(version or "无法获取 Java 版本")

    def refresh_local_versions(self):
        self.start_scan_task("local_versions", game_dir=self.current_game_dir())

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

    def on_download_addon_changed(self, changed_type):
        if changed_type in {"fabric", "forge", "neoforge", "optifine"} and self.download_loader_checks[changed_type].isChecked():
            for install_type in ("fabric", "forge", "neoforge", "optifine"):
                if install_type != changed_type:
                    checkbox = self.download_loader_checks[install_type]
                    checkbox.blockSignals(True)
                    checkbox.setChecked(False)
                    checkbox.blockSignals(False)

        if changed_type != "fabric" and self.download_loader_checks[changed_type].isChecked():
            fabric_api_check = self.download_loader_checks["fabric_api"]
            fabric_api_check.blockSignals(True)
            fabric_api_check.setChecked(False)
            fabric_api_check.blockSignals(False)

        self.update_download_addon_controls()

    def update_download_addon_controls(self):
        fabric_checked = self.download_loader_checks["fabric"].isChecked()
        fabric_api_check = self.download_loader_checks["fabric_api"]
        fabric_api_check.setEnabled(fabric_checked)
        if not fabric_checked and fabric_api_check.isChecked():
            fabric_api_check.blockSignals(True)
            fabric_api_check.setChecked(False)
            fabric_api_check.blockSignals(False)

        selected = self.get_selected_download_addons()
        if selected:
            labels = [INSTALL_TYPE_LABELS.get(item, item) for item in selected]
            self.download_addon_hint_label.setText(f"下载完成后将继续安装：{' + '.join(labels)}")
        else:
            self.download_addon_hint_label.setText("可在下载原版后自动继续安装；Fabric API 仅在 Fabric 一起安装时可用。")

    def get_selected_download_addons(self):
        install_types = []
        for install_type in ("fabric", "forge", "neoforge", "optifine"):
            if self.download_loader_checks[install_type].isChecked():
                install_types.append(install_type)
                break
        if self.download_loader_checks["fabric_api"].isChecked():
            install_types.append("fabric_api")
        return install_types

    def update_install_button_text(self, install_type):
        if not hasattr(self, "install_button"):
            return
        self.install_button.setText("开始安装")

    def refresh_remote_versions(self):
        self.start_scan_task(
            "remote_versions",
            version_type=self.version_type_combo.currentText(),
            mirror_source=self.mirror_combo.currentText(),
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
            config["ACCOUNTS"]["selected_account_id"] = account.get("id", "")
        config["DOWNLOAD"]["mirror_source"] = self.mirror_combo.currentText()
        config["AUTH"]["auto_open_browser"] = str(self.auto_open_browser_check.isChecked())
        config["GAME"]["directory"] = self.current_game_dir()
        config["GAME"]["enable_resource_isolation"] = str(self.resource_isolation_check.isChecked())
        config["UI"]["advanced_mode"] = str(self.advanced_mode_check.isChecked())
        save_config()
        self.log("设置已保存。")
        self.update_home_summary()
        if show_feedback:
            self.show_success("已保存", "启动器设置已写入配置文件。")
        return True

    def choose_game_directory(self):
        directory = QFileDialog.getExistingDirectory(self, "选择游戏目录", self.current_game_dir())
        if directory:
            self.game_dir_input.setText(directory)
            self.refresh_local_versions()
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
        self.log(f"Microsoft 登录失败：{message}")
        self.show_warning("登录失败", message)

    def launch_game(self):
        java_path = self.java_combo.currentText().strip()
        version = self.current_selected_version()
        account = self.current_account()
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
        runtime_directory = launch_options["runtime_directory"]
        extra_jvm_args = launch_options["extra_jvm_args"]

        if self.launch_thread and self.launch_thread.isRunning():
            self.show_warning("启动进行中", "当前已有启动任务在运行。")
            return

        self.set_launch_running(True)
        self.launch_status_label.setText("正在准备启动...")
        self.launch_thread = QThread()
        self.launch_worker = LaunchWorker(
            java_path,
            version,
            self.current_game_dir(),
            account,
            runtime_directory,
            extra_jvm_args=extra_jvm_args,
        )
        self.launch_worker.moveToThread(self.launch_thread)
        self.launch_thread.started.connect(self.launch_worker.run)
        self.launch_worker.status.connect(self.on_launch_status)
        self.launch_worker.finished.connect(self.on_launch_finished)
        self.launch_worker.failed.connect(self.on_launch_failed)
        self.launch_worker.finished.connect(self.launch_thread.quit)
        self.launch_worker.failed.connect(self.launch_thread.quit)
        self.launch_thread.start()

    def on_launch_status(self, message):
        self.launch_status_label.setText(message)
        self.log(message)

    def on_install_status(self, message):
        self.install_status_label.setText(message)
        self.log_install(message)

    def on_install_status_from_download(self, message):
        self.install_status_label.setText(message)
        self.log_install(message)

    def on_launch_finished(self, payload):
        self.set_launch_running(False)
        account = payload.get("account", {})
        if account.get("id"):
            self.upsert_account(account)
        version = payload.get("version", "")
        self.version_settings.setdefault("_meta", {})["last_launched_version"] = version
        save_version_settings(self.version_settings)
        self.on_java_selected(self.java_combo.currentText())
        self.log(f"正在使用 {account_label(account)} 启动 Minecraft {version}...")
        self.show_success("正在启动", f"Minecraft {version} 已开始启动。")

    def on_launch_failed(self, message):
        self.set_launch_running(False)
        self.launch_status_label.setText(f"启动失败：{message}")
        self.log(f"启动失败：{message}")
        self.show_warning("启动失败", message)

    def start_download(self):
        if self.download_thread and self.download_thread.isRunning():
            self.show_warning("下载进行中", "当前已有下载任务在运行。")
            return
        if self.install_thread and self.install_thread.isRunning():
            self.show_warning("安装进行中", "当前已有扩展安装任务在运行。")
            return

        version = self.remote_version_combo.currentText().strip()
        if not version or version == "点击刷新远程版本":
            self.show_warning("缺少版本", "请先刷新并选择要下载的版本。")
            return

        if not self.save_settings(show_feedback=False):
            return
        auto_install_types = self.get_selected_download_addons()
        java_path = self.java_combo.currentText().strip()
        if any(item in {"forge", "neoforge"} for item in auto_install_types) and not java_path:
            self.show_warning("缺少 Java", "自动安装 Forge 或 NeoForge 需要可用的 Java。")
            return
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
            self.mirror_combo.currentText(),
            self.current_game_dir(),
            auto_install_types=auto_install_types,
            java_path=java_path,
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
        if self.download_thread and self.download_thread.isRunning():
            self.show_warning("下载进行中", "请等待当前下载任务完成后再安装扩展。")
            return
        if self.install_thread and self.install_thread.isRunning():
            self.show_warning("安装进行中", "当前已有扩展安装任务在运行。")
            return

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
            self.mirror_combo.currentText(),
            self.current_game_dir(),
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

    def on_download_finished(self, payload):
        self.set_download_running(False)
        version = payload.get("version", "")
        post_install = payload.get("post_install")
        self.log(f"Minecraft {version} 下载完成。")
        self.refresh_local_versions()
        self.local_version_combo.setCurrentText(version)
        if post_install:
            installed_version = post_install.get("installed_version", version)
            self.download_metrics_label.setText(post_install.get("message", "下载和安装完成"))
            self.install_status_label.setText("附加安装完成")
            self.install_metrics_label.setText(post_install.get("message", "附加安装已完成"))
            index = self.local_version_combo.findText(installed_version)
            if index >= 0:
                self.local_version_combo.setCurrentIndex(index)
            self.show_success("下载和安装完成", f"Minecraft {version} 已下载，并完成附加安装。")
        else:
            self.download_metrics_label.setText("下载完成")
            self.install_status_label.setText("本次未执行附加安装")
            self.install_metrics_label.setText("如需 Fabric / Forge / NeoForge / OptiFine，可切到“安装扩展”")
            self.show_success("下载完成", f"Minecraft {version} 已下载完成。")

    def on_download_failed(self, message):
        self.set_download_running(False)
        self.log(f"下载失败：{message}")
        self.download_metrics_label.setText(f"下载失败：{message}")
        self.install_status_label.setText("附加安装未开始")
        self.show_warning("下载失败", message)

    def on_install_finished(self, payload):
        self.set_download_running(False)
        installed_version = payload.get("installed_version", "")
        message = payload.get("message", "安装完成")
        self.log(f"安装完成：{message}")
        self.install_status_label.setText("安装完成")
        self.install_metrics_label.setText(message)
        self.download_metrics_label.setText(message)
        self.refresh_local_versions()
        if installed_version:
            index = self.local_version_combo.findText(installed_version)
            if index >= 0:
                self.local_version_combo.setCurrentIndex(index)
        self.show_success("安装完成", message)

    def on_install_failed(self, message):
        self.set_download_running(False)
        self.log(f"安装失败：{message}")
        self.download_metrics_label.setText(f"安装失败：{message}")
        self.install_status_label.setText("安装失败")
        self.install_metrics_label.setText(message)
        self.show_warning("安装失败", message)


def main():
    load_config()
    threading.Thread(target=run_flask_app, daemon=True).start()
    qt_app = QApplication(sys.argv)
    setTheme(Theme.DARK)
    window = LauncherWindow()
    window.show()
    sys.exit(qt_app.exec())


if __name__ == "__main__":
    main()
