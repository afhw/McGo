import logging
import os
import re
import sys
import threading
from logging.handlers import RotatingFileHandler


LOG_DIR = "logs"
LOG_FILE = "mcgo.log"
_CONFIGURED = False
_NOTICE_LOCK = threading.Lock()
_PENDING_NOTICES = []
_NOTICE_KEYS = set()

_SENSITIVE_KEYWORDS = (
    "access_token",
    "accesstoken",
    "refresh_token",
    "refreshtoken",
    "minecraft_access_token",
    "authorization",
    "identitytoken",
    "rpsticket",
    "password",
    "token",
)


def _queue_logging_notice(key, message):
    with _NOTICE_LOCK:
        if key in _NOTICE_KEYS:
            return
        _NOTICE_KEYS.add(key)
        _PENDING_NOTICES.append(message)


def consume_logging_notices():
    with _NOTICE_LOCK:
        notices = list(_PENDING_NOTICES)
        _PENDING_NOTICES.clear()
    return notices


class SafeRotatingFileHandler(RotatingFileHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.rollover_blocked = False

    def shouldRollover(self, record):
        if self.rollover_blocked:
            return False
        return super().shouldRollover(record)

    def doRollover(self):
        try:
            super().doRollover()
            self.rollover_blocked = False
        except PermissionError as exc:
            _queue_logging_notice(
                "log-rollover-permission",
                f"日志文件正在被占用，暂时无法轮转：{self.baseFilename}。"
                "请关闭其它 McGo 实例、日志查看器或占用该文件的程序。",
            )
            self.rollover_blocked = True
            self.stream = self._open()

    def handleError(self, record):
        exc = sys.exc_info()[1]
        if isinstance(exc, PermissionError):
            _queue_logging_notice(
                "log-write-permission",
                f"日志文件正在被占用，部分日志可能无法写入：{self.baseFilename}。"
                "请关闭其它 McGo 实例、日志查看器或占用该文件的程序。",
            )
            return
        super().handleError(record)


def setup_logging(log_dir=LOG_DIR, level=logging.DEBUG):
    """Configure application-wide file logging once."""
    global _CONFIGURED
    os.makedirs(log_dir, exist_ok=True)
    log_path = os.path.abspath(os.path.join(log_dir, LOG_FILE))

    root_logger = logging.getLogger()
    root_logger.setLevel(level)

    if not _CONFIGURED:
        formatter = logging.Formatter(
            "%(asctime)s.%(msecs)03d [%(levelname)s] "
            "%(threadName)s %(name)s:%(lineno)d - %(message)s",
            datefmt="%Y-%m-%d %H:%M:%S",
        )

        try:
            file_handler = SafeRotatingFileHandler(
                log_path,
                maxBytes=2 * 1024 * 1024,
                backupCount=5,
                encoding="utf-8",
            )
            file_handler.setLevel(level)
            file_handler.setFormatter(formatter)
            root_logger.addHandler(file_handler)
        except PermissionError:
            _queue_logging_notice(
                "log-open-permission",
                f"日志文件正在被占用，无法打开：{log_path}。"
                "请关闭其它 McGo 实例、日志查看器或占用该文件的程序。",
            )

        console_handler = logging.StreamHandler()
        console_handler.setLevel(logging.INFO)
        console_handler.setFormatter(formatter)
        root_logger.addHandler(console_handler)

        _CONFIGURED = True

    logging.getLogger(__name__).info(
        "Logging initialized: path=%s python=%s cwd=%s",
        log_path,
        sys.version.replace("\n", " "),
        os.getcwd(),
    )
    return log_path


def get_logger(name):
    return logging.getLogger(name)


def redact(value):
    """Redact sensitive tokens while keeping surrounding debug context useful."""
    if value is None:
        return None
    text = str(value)
    if not text:
        return text
    if len(text) <= 8:
        return "<redacted>"
    return f"{text[:4]}...{text[-4:]}"


def redact_mapping(data):
    if not isinstance(data, dict):
        return data
    redacted = {}
    for key, value in data.items():
        key_text = str(key).lower()
        if any(keyword in key_text for keyword in _SENSITIVE_KEYWORDS):
            redacted[key] = redact(value)
        elif isinstance(value, dict):
            redacted[key] = redact_mapping(value)
        elif isinstance(value, list):
            redacted[key] = [
                redact_mapping(item) if isinstance(item, dict) else item
                for item in value
            ]
        else:
            redacted[key] = value
    return redacted


def redact_command(command):
    redacted = []
    redact_next = False
    for item in command:
        text = str(item)
        lowered = text.lower()
        if redact_next:
            redacted.append("<redacted>")
            redact_next = False
            continue
        if lowered in {"--accesstoken", "--uuid"}:
            redacted.append(text)
            redact_next = True
            continue
        if any(keyword in lowered for keyword in _SENSITIVE_KEYWORDS):
            if "=" in text:
                left, _ = text.split("=", 1)
                redacted.append(f"{left}=<redacted>")
            else:
                redacted.append(re.sub(r"(?<=.{4}).(?=.{4})", "*", text))
            continue
        redacted.append(text)
    return redacted
