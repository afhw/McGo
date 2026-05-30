from PyQt6.QtCore import QObject, pyqtSignal as Signal

from external_auth import authenticate_external_account, probe_external_auth_server, refresh_external_account
from log_utils import get_logger
from nat_utils import detect_nat_type


logger = get_logger(__name__)


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
