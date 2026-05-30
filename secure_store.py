import base64
import ctypes
import os
from ctypes import wintypes


TOKEN_FIELDS = {
    "access_token",
    "refresh_token",
    "client_token",
}

_PROTECTED_PREFIX = "dpapi:"
_FALLBACK_PREFIX = "plain64:"


class _DataBlob(ctypes.Structure):
    _fields_ = [
        ("cbData", wintypes.DWORD),
        ("pbData", ctypes.POINTER(ctypes.c_char)),
    ]


def _is_windows():
    return os.name == "nt"


def _blob_from_bytes(data):
    buffer = ctypes.create_string_buffer(data)
    return _DataBlob(len(data), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_char))), buffer


def _dpapi_protect(value):
    raw = value.encode("utf-8")
    in_blob, in_buffer = _blob_from_bytes(raw)
    out_blob = _DataBlob()
    crypt32 = ctypes.windll.crypt32
    kernel32 = ctypes.windll.kernel32
    if not crypt32.CryptProtectData(
        ctypes.byref(in_blob),
        "McGo account token",
        None,
        None,
        None,
        0,
        ctypes.byref(out_blob),
    ):
        raise ctypes.WinError()
    try:
        encrypted = ctypes.string_at(out_blob.pbData, out_blob.cbData)
    finally:
        kernel32.LocalFree(out_blob.pbData)
    # Keep the input buffer alive until after CryptProtectData returns.
    _ = in_buffer
    return _PROTECTED_PREFIX + base64.b64encode(encrypted).decode("ascii")


def _dpapi_unprotect(value):
    encrypted = base64.b64decode(value[len(_PROTECTED_PREFIX):].encode("ascii"))
    in_blob, in_buffer = _blob_from_bytes(encrypted)
    out_blob = _DataBlob()
    crypt32 = ctypes.windll.crypt32
    kernel32 = ctypes.windll.kernel32
    if not crypt32.CryptUnprotectData(
        ctypes.byref(in_blob),
        None,
        None,
        None,
        None,
        0,
        ctypes.byref(out_blob),
    ):
        raise ctypes.WinError()
    try:
        raw = ctypes.string_at(out_blob.pbData, out_blob.cbData)
    finally:
        kernel32.LocalFree(out_blob.pbData)
    _ = in_buffer
    return raw.decode("utf-8")


def protect_secret(value):
    value = str(value or "")
    if not value:
        return ""
    if value.startswith((_PROTECTED_PREFIX, _FALLBACK_PREFIX)):
        return value
    if _is_windows():
        return _dpapi_protect(value)
    return _FALLBACK_PREFIX + base64.b64encode(value.encode("utf-8")).decode("ascii")


def unprotect_secret(value):
    value = str(value or "")
    if not value:
        return ""
    if value.startswith(_PROTECTED_PREFIX):
        return _dpapi_unprotect(value)
    if value.startswith(_FALLBACK_PREFIX):
        return base64.b64decode(value[len(_FALLBACK_PREFIX):].encode("ascii")).decode("utf-8")
    return value


def hydrate_account_tokens(account):
    hydrated = dict(account)
    secure_tokens = hydrated.get("secure_tokens") or {}
    if isinstance(secure_tokens, dict):
        for field in TOKEN_FIELDS:
            if hydrated.get(field):
                continue
            protected = secure_tokens.get(field, "")
            if protected:
                hydrated[field] = unprotect_secret(protected)
    return hydrated


def redact_account_tokens(account):
    redacted = dict(account)
    secure_tokens = dict(redacted.get("secure_tokens") or {})
    for field in TOKEN_FIELDS:
        token = redacted.pop(field, "")
        if token:
            secure_tokens[field] = protect_secret(token)
    if secure_tokens:
        redacted["secure_tokens"] = secure_tokens
    else:
        redacted.pop("secure_tokens", None)
    return redacted


def hydrate_accounts(accounts):
    return [hydrate_account_tokens(account) for account in accounts if isinstance(account, dict)]


def redact_accounts(accounts):
    return [redact_account_tokens(account) for account in accounts if isinstance(account, dict)]
