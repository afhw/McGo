import logging
import os
import re
import sys
from logging.handlers import RotatingFileHandler


LOG_DIR = "logs"
LOG_FILE = "mcgo.log"
_CONFIGURED = False

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

        file_handler = RotatingFileHandler(
            log_path,
            maxBytes=2 * 1024 * 1024,
            backupCount=5,
            encoding="utf-8",
        )
        file_handler.setLevel(level)
        file_handler.setFormatter(formatter)
        root_logger.addHandler(file_handler)

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
