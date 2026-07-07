import base64
import hashlib
import json

import pytest

import updater


class DummyResponse:
    def __init__(self, payload=None, chunks=None, headers=None, status_code=200, url="https://mcgo.flyliq.cn/update.json"):
        self.payload = payload
        self.chunks = chunks or []
        self.headers = headers or {}
        self.status_code = status_code
        self.url = url
        self.text = json.dumps(payload or {})

    def json(self):
        return self.payload

    def raise_for_status(self):
        if self.status_code >= 400:
            raise RuntimeError(self.status_code)

    def iter_content(self, chunk_size=1):
        yield from self.chunks


class InvalidJsonResponse(DummyResponse):
    def json(self):
        raise ValueError("bad json")


def test_is_newer_version_compares_numeric_parts():
    assert updater.is_newer_version("1.10.0", "1.9.9")
    assert not updater.is_newer_version("1.0.0", "1.0.0")
    assert not updater.is_newer_version("1.0.0", "1.0.1")


def test_is_newer_version_treats_release_as_newer_than_prerelease():
    assert updater.is_newer_version("1.0.0", "1.0.0-rc1")
    assert not updater.is_newer_version("1.0.0-rc1", "1.0.0")
    assert updater.is_newer_version("1.0.0-rc2", "1.0.0-rc1")
    assert not updater.is_newer_version("1.0.0+2", "1.0.0+1")


def test_fetch_update_manifest_normalizes_relative_url(monkeypatch):
    monkeypatch.setattr(
        updater.http_client,
        "get",
        lambda *_args, **_kwargs: DummyResponse(
            {"version": "1.2.0", "url": "/download/McGo.exe", "sha256": "a" * 64}
        ),
    )

    manifest = updater.fetch_update_manifest("https://mcgo.flyliq.cn/update.json")

    assert manifest["version"] == "1.2.0"
    assert manifest["url"] == "https://mcgo.flyliq.cn/download/McGo.exe"
    assert manifest["sha256"] == "a" * 64


def test_fetch_update_manifest_requires_sha256(monkeypatch):
    monkeypatch.setattr(
        updater.http_client,
        "get",
        lambda *_args, **_kwargs: DummyResponse({"version": "1.2.0", "url": "/download/McGo.exe"}),
    )

    with pytest.raises(RuntimeError, match="缺少 sha256"):
        updater.fetch_update_manifest("https://mcgo.flyliq.cn/update.json")


def test_fetch_update_manifest_rejects_invalid_sha256(monkeypatch):
    monkeypatch.setattr(
        updater.http_client,
        "get",
        lambda *_args, **_kwargs: DummyResponse(
            {"version": "1.2.0", "url": "/download/McGo.exe", "sha256": "not-a-digest"}
        ),
    )

    with pytest.raises(RuntimeError, match="sha256"):
        updater.fetch_update_manifest("https://mcgo.flyliq.cn/update.json")


def test_fetch_update_manifest_rejects_invalid_json(monkeypatch):
    monkeypatch.setattr(updater.http_client, "get", lambda *_args, **_kwargs: InvalidJsonResponse())

    with pytest.raises(RuntimeError, match="有效 JSON"):
        updater.fetch_update_manifest("https://mcgo.flyliq.cn/update.json")


def test_fetch_update_manifest_rejects_non_http_download_url(monkeypatch):
    monkeypatch.setattr(
        updater.http_client,
        "get",
        lambda *_args, **_kwargs: DummyResponse(
            {"version": "1.2.0", "url": "file:///tmp/McGo.exe", "sha256": "a" * 64}
        ),
    )

    with pytest.raises(RuntimeError, match="HTTP"):
        updater.fetch_update_manifest("https://mcgo.flyliq.cn/update.json")


def test_fetch_update_manifest_rejects_invalid_size(monkeypatch):
    monkeypatch.setattr(
        updater.http_client,
        "get",
        lambda *_args, **_kwargs: DummyResponse(
            {"version": "1.2.0", "url": "/download/McGo.exe", "sha256": "a" * 64, "size": -1}
        ),
    )

    with pytest.raises(RuntimeError, match="size"):
        updater.fetch_update_manifest("https://mcgo.flyliq.cn/update.json")


def test_check_for_update_detects_same_version_rebuild(monkeypatch):
    monkeypatch.setattr(
        updater.http_client,
        "get",
        lambda *_args, **_kwargs: DummyResponse(
            {
                "version": "1.0.7",
                "url": "https://mcgo.flyliq.cn/download/McGo.exe",
                "sha256": "a" * 64,
            }
        ),
    )
    monkeypatch.setattr(updater, "current_build_sha256", lambda *_args, **_kwargs: "b" * 64)

    manifest = updater.check_for_update("https://mcgo.flyliq.cn/update.json", "1.0.7")

    assert manifest["update_available"]
    assert manifest["build_changed"]


def test_check_for_update_ignores_same_version_without_current_hash(monkeypatch):
    monkeypatch.setattr(
        updater.http_client,
        "get",
        lambda *_args, **_kwargs: DummyResponse(
            {
                "version": "1.0.7",
                "url": "https://mcgo.flyliq.cn/download/McGo.exe",
                "sha256": "a" * 64,
            }
        ),
    )
    monkeypatch.setattr(updater, "current_build_sha256", lambda *_args, **_kwargs: "")

    manifest = updater.check_for_update("https://mcgo.flyliq.cn/update.json", "1.0.7")

    assert not manifest["update_available"]
    assert not manifest["build_changed"]


def test_download_update_rejects_sha256_mismatch(tmp_path, monkeypatch):
    monkeypatch.setattr(
        updater.http_client,
        "get",
        lambda *_args, **_kwargs: DummyResponse(chunks=[b"MZnot-the-build"], headers={"Content-Length": "15"}),
    )
    manifest = {"version": "1.2.0", "url": "https://mcgo.flyliq.cn/download/McGo.exe", "sha256": "0" * 64}

    with pytest.raises(RuntimeError, match="SHA256"):
        updater.download_update(manifest, target_dir=str(tmp_path))


def test_download_update_requires_sha256(tmp_path, monkeypatch):
    def fail_get(*_args, **_kwargs):
        raise AssertionError("download should not start without sha256")

    monkeypatch.setattr(updater.http_client, "get", fail_get)
    manifest = {"version": "1.2.0", "url": "https://mcgo.flyliq.cn/download/McGo.exe"}

    with pytest.raises(RuntimeError, match="缺少 sha256"):
        updater.download_update(manifest, target_dir=str(tmp_path))


def test_download_update_rejects_size_mismatch_and_cleans_temp(tmp_path, monkeypatch):
    monkeypatch.setattr(
        updater.http_client,
        "get",
        lambda *_args, **_kwargs: DummyResponse(chunks=[b"MZ345"], headers={"Content-Length": "5"}),
    )
    manifest = {"version": "1.2.0", "url": "https://mcgo.flyliq.cn/download/McGo.exe", "sha256": "a" * 64, "size": 6}

    with pytest.raises(RuntimeError, match="大小"):
        updater.download_update(manifest, target_dir=str(tmp_path))

    assert not list(tmp_path.glob("*.download"))


def test_download_update_rejects_empty_file(tmp_path, monkeypatch):
    monkeypatch.setattr(
        updater.http_client,
        "get",
        lambda *_args, **_kwargs: DummyResponse(chunks=[], headers={"Content-Length": "0"}),
    )
    manifest = {"version": "1.2.0", "url": "https://mcgo.flyliq.cn/download/McGo.exe", "sha256": "a" * 64}

    with pytest.raises(RuntimeError, match="为空"):
        updater.download_update(manifest, target_dir=str(tmp_path))


def test_download_update_sanitizes_version_suffix(tmp_path, monkeypatch):
    chunks = [b"MZbuild"]
    monkeypatch.setattr(
        updater.http_client,
        "get",
        lambda *_args, **_kwargs: DummyResponse(chunks=chunks, headers={"Content-Length": "7"}),
    )

    payload = updater.download_update(
        {
            "version": r"..\evil/1.2.0",
            "url": "https://mcgo.flyliq.cn/download/McGo",
            "sha256": hashlib.sha256(b"MZbuild").hexdigest(),
        },
        target_dir=str(tmp_path),
    )

    assert payload["path"] == str(tmp_path / "McGo-evil-1.2.0.exe")
    assert (tmp_path / "McGo-evil-1.2.0.exe").read_bytes() == b"MZbuild"


def test_download_update_rejects_non_executable_payload(tmp_path, monkeypatch):
    monkeypatch.setattr(
        updater.http_client,
        "get",
        lambda *_args, **_kwargs: DummyResponse(chunks=[b"<html>"], headers={"Content-Length": "6"}),
    )
    manifest = {"version": "1.2.0", "url": "https://mcgo.flyliq.cn/download/McGo.exe", "sha256": "a" * 64}

    with pytest.raises(RuntimeError, match="Windows 可执行文件"):
        updater.download_update(manifest, target_dir=str(tmp_path))


def test_current_executable_prefers_argv_exe(monkeypatch):
    monkeypatch.setattr(updater.sys, "argv", [r"C:\Apps\McGo.exe"])
    monkeypatch.setattr(updater.sys, "executable", r"C:\Temp\_MEI123\python.exe")

    assert updater.current_executable_path().endswith(r"C:\Apps\McGo.exe")


def _decode_powershell(encoded):
    return base64.b64decode(encoded).decode("utf-16le")


def test_apply_update_uses_encoded_command_without_temp_script(tmp_path, monkeypatch):
    calls = []
    monkeypatch.setattr(updater.tempfile, "gettempdir", lambda: str(tmp_path))
    monkeypatch.setattr(updater.os, "name", "nt")
    monkeypatch.setattr(updater.os, "getpid", lambda: 1234)
    monkeypatch.setattr(updater, "_target_directory_writable", lambda _path: True)
    monkeypatch.setattr(updater.subprocess, "Popen", lambda args, **kwargs: calls.append((args, kwargs)))

    result = updater.schedule_windows_replace_and_restart(
        str(tmp_path / "McGo 1.2.0.exe"),
        executable_path=str(tmp_path / "Program Files" / "McGo.exe"),
    )

    assert result == {"elevated": False, "log_path": str(tmp_path / "mcgo-update.log")}
    assert calls[0][0][0] == "powershell.exe"
    assert "-EncodedCommand" in calls[0][0]
    assert not (tmp_path / "mcgo-apply-update.ps1").exists()
    command = _decode_powershell(calls[0][0][-1])
    assert "Copy-Item -LiteralPath $Source -Destination $Destination -Force" in command
    assert "$Elevated = $false" in command
    assert "McGo 1.2.0.exe" in command


def test_apply_update_elevates_encoded_command_when_target_not_writable(tmp_path, monkeypatch):
    calls = []
    monkeypatch.setattr(updater.tempfile, "gettempdir", lambda: str(tmp_path))
    monkeypatch.setattr(updater.os, "name", "nt")
    monkeypatch.setattr(updater.os, "getpid", lambda: 1234)
    monkeypatch.setattr(updater, "_target_directory_writable", lambda _path: False)
    monkeypatch.setattr(updater.subprocess, "Popen", lambda args, **kwargs: calls.append((args, kwargs)))

    result = updater.schedule_windows_replace_and_restart(
        str(tmp_path / "McGo 1.2.0.exe"),
        executable_path=str(tmp_path / "Program Files" / "McGo.exe"),
    )

    assert result["elevated"] is True
    assert not (tmp_path / "mcgo-apply-update.ps1").exists()
    bootstrap = _decode_powershell(calls[0][0][-1])
    assert "-Verb RunAs" in bootstrap
    assert "-EncodedCommand" in bootstrap
    nested_encoded = bootstrap.split("'-EncodedCommand', '", 1)[1].split("'", 1)[0]
    elevated_command = _decode_powershell(nested_encoded)
    assert "$Elevated = $true" in elevated_command
    assert "Copy-Item -LiteralPath $Source -Destination $Destination -Force" in elevated_command
