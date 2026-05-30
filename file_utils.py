import hashlib
import re


def sanitize_filename(value, fallback="ImportedPack"):
    cleaned = re.sub(r'[<>:"/\\|?*]+', "_", str(value or "").strip()).strip(". ")
    return cleaned or fallback


def sha1_file(path):
    digest = hashlib.sha1()
    with open(path, "rb") as file_handle:
        for chunk in iter(lambda: file_handle.read(128 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def sha1_text(value):
    return hashlib.sha1(str(value).encode("utf-8", errors="ignore")).hexdigest()
