import configparser
import json
import os
import tempfile


def atomic_write_bytes(path, data):
    directory = os.path.abspath(os.path.dirname(path) or ".")
    os.makedirs(directory, exist_ok=True)
    fd, temp_path = tempfile.mkstemp(prefix=f".{os.path.basename(path)}.", suffix=".tmp", dir=directory)
    try:
        with os.fdopen(fd, "wb") as file_handle:
            file_handle.write(data)
            file_handle.flush()
            os.fsync(file_handle.fileno())
        os.replace(temp_path, path)
    except Exception:
        try:
            os.remove(temp_path)
        except OSError:
            pass
        raise


def atomic_write_text(path, text, encoding="utf-8"):
    atomic_write_bytes(path, text.encode(encoding))


def save_json_atomic(path, data, indent=2):
    text = json.dumps(data, ensure_ascii=False, indent=indent)
    atomic_write_text(path, text + "\n", encoding="utf-8")


def save_config_atomic(path, parser):
    buffer = tempfile.SpooledTemporaryFile(mode="w+", encoding="utf-8", newline="")
    try:
        parser.write(buffer)
        buffer.seek(0)
        atomic_write_text(path, buffer.read(), encoding="utf-8")
    finally:
        buffer.close()


def load_json_file(path, fallback):
    if not os.path.exists(path):
        return fallback
    with open(path, "r", encoding="utf-8") as file_handle:
        return json.load(file_handle)


def load_config_file(path):
    parser = configparser.ConfigParser()
    parser.read(path, encoding="utf-8")
    return parser
