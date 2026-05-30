import asyncio
import subprocess

from PyQt6.QtCore import QObject, pyqtSignal as Signal

from auth import MicrosoftAuthenticator
from external_auth import authlib_injector_args, refresh_external_account
from launcher import launch_minecraft
from log_utils import get_logger, redact_mapping
from process_utils import hidden_subprocess_kwargs


logger = get_logger(__name__)


class LaunchWorker(QObject):
    status = Signal(str)
    progress = Signal(int)
    stage = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, java_path, version, game_dir, account, launch_options, client_id, redirect_uri):
        super().__init__()
        self.java_path = java_path
        self.version = version
        self.game_dir = game_dir
        self.account = dict(account)
        self.launch_options = dict(launch_options or {})
        self.client_id = client_id
        self.redirect_uri = redirect_uri

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
                session = MicrosoftAuthenticator(self.client_id, self.redirect_uri)
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
