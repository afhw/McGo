import asyncio
import os
import uuid as uuidlib
import webbrowser

from PyQt6.QtCore import QThread, QObject, pyqtSignal as Signal

from external_auth import authenticate_external_account, probe_external_auth_server, refresh_external_account
from install_services import get_remote_versions
from java_utils import find_java_paths, get_java_major_version
from launcher import get_local_versions
from log_utils import get_logger
from nat_utils import detect_nat_type


logger = get_logger(__name__)


class AuthWorker(QObject):
    login_url_ready = Signal(str)
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, auto_open_browser, authenticator, ensure_flask_running):
        super().__init__()
        self.auto_open_browser = auto_open_browser
        self.authenticator = authenticator
        self.ensure_flask_running = ensure_flask_running

    def run(self):
        try:
            logger.info("AuthWorker started: auto_open_browser=%s", self.auto_open_browser)
            self.status.emit("正在启动本地 Microsoft 登录回调服务...")
            self.ensure_flask_running()
            login_url = self.authenticator.get_login_url()
            self.login_url_ready.emit(login_url)
            self.authenticator.authorization_code = None
            if self.auto_open_browser:
                self.status.emit("正在打开浏览器进行 Microsoft 登录...")
                webbrowser.open(login_url)
            else:
                self.status.emit("请手动打开登录链接完成 Microsoft 登录...")
            while self.authenticator.authorization_code is None:
                QThread.msleep(500)
            logger.info("Microsoft authorization code detected, exchanging tokens")
            asyncio.run(self.authenticator.authenticate())
            uuid, username, _ = asyncio.run(self.authenticator.get_minecraft_profile())
            logger.info("AuthWorker finished: username=%s uuid=%s", username, uuid)
            self.finished.emit({
                "id": str(uuidlib.uuid4()),
                "type": "microsoft",
                "display_name": username,
                "username": username,
                "uuid": uuid,
                "access_token": "",
                "refresh_token": self.authenticator.refresh_token,
            })
        except Exception as exc:
            logger.exception("AuthWorker failed")
            self.failed.emit(str(exc))


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
                import os

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


class NatDetectionWorker(QObject):
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def run(self):
        try:
            self.status.emit("正在通过 STUN 检测 NAT 类型...")
            result = detect_nat_type()
            self.finished.emit(result)
        except Exception as exc:
            logger.exception("NAT detection failed")
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
