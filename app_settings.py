import configparser
import json
import os
import re
import uuid as uuidlib
from collections.abc import Callable
from dataclasses import dataclass
from urllib.parse import urlparse

from log_utils import get_logger, setup_logging
from secure_store import hydrate_accounts, redact_accounts
from storage_utils import save_config_atomic, save_json_atomic

client_id = "cf1d47c2-2199-495a-9822-a2a2b97cd568"
redirect_uri = "http://localhost:5000/login/callback"

game_directory = ".minecraft"
config_file = "launcher_config.ini"
accounts_file = "accounts.json"

config = configparser.ConfigParser()
LOG_PATH = setup_logging()
logger = get_logger(__name__)
_authenticator = None


def get_authenticator():
    global _authenticator
    if _authenticator is None:
        from auth_server import create_authenticator

        _authenticator = create_authenticator(client_id, redirect_uri)
    return _authenticator


class LazyAuthenticator:
    def __getattr__(self, name):
        return getattr(get_authenticator(), name)


authenticator = LazyAuthenticator()


def ensure_flask_running():
    from auth_server import start_flask_in_thread

    return start_flask_in_thread()


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
        "P2P": {
            "relay_host": "flyliq.cn",
            "relay_port": "10721",
            "room": "",
            "secret": "",
            "host_port": "25565",
            "join_port": "25565",
        },
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
    save_config_atomic(config_file, config)
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
            raw_accounts = json.load(f)
            accounts = hydrate_accounts(raw_accounts)
        if any(isinstance(item, dict) and any(item.get(field) for field in ("access_token", "refresh_token", "client_token")) for item in raw_accounts):
            save_accounts(accounts)
            logger.info("Migrated account tokens to protected storage")
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
    save_json_atomic(accounts_file, redact_accounts(accounts), indent=2)
    logger.debug("Saved %d accounts to %s", len(accounts), os.path.abspath(accounts_file))


VERSION_ICON_LABELS = ["自动", "草方块", "金块", "红石", "命令方块", "Fabric", "Forge", "NeoForge", "OptiFine"]


DOWNLOAD_PRESETS = {
    "保守": {"core": 6, "asset": 12, "speed_kbps": 0, "cache": "reuse"},
    "均衡": {"core": 12, "asset": 24, "speed_kbps": 0, "cache": "reuse"},
    "激进": {"core": 20, "asset": 40, "speed_kbps": 0, "cache": "reuse"},
}

GC_STRATEGIES = ["G1GC", "ZGC", "Shenandoah", "默认"]


@dataclass
class DownloadTask:
    task_type: str
    title: str
    start_callback: Callable[[], None]

    def start(self):
        self.start_callback()


def read_download_options():
    return {
        "max_core_concurrency": max(1, config.getint("DOWNLOAD", "max_core_threads", fallback=12)),
        "max_asset_concurrency": max(1, config.getint("DOWNLOAD", "max_asset_threads", fallback=24)),
        "speed_limit_kbps": max(0, config.getint("DOWNLOAD", "speed_limit_kbps", fallback=0)),
        "cache_strategy": config.get("DOWNLOAD", "cache_strategy", fallback="reuse"),
    }


def concise_download_error(message):
    text = str(message or "").strip()
    if not text:
        return "下载任务失败，请查看日志获取详细信息。"

    parts = [part.strip() for part in text.split("；") if part.strip()]
    if parts and all("404" in part for part in parts):
        urls = re.findall(r"https?://[^\s'；,]+", parts[0])
        url = urls[0].rstrip(",:;，；") if urls else ""
        filename = os.path.basename(urlparse(url).path) if url else ""
        if not filename:
            match = re.search(r"([^/\\；:'\"]+\.jar)", parts[0])
            filename = match.group(1) if match else "目标文件"
        return f"{filename} 在当前镜像源和官方源均不存在（404）。请切换镜像源或重新刷新版本信息后重试。"

    return text if len(text) <= 240 else f"{text[:237]}..."
