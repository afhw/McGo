import json

import install_workers
from install_workers import DownloadWorker


def write_version(game_dir, version_id, payload):
    version_dir = game_dir / "versions" / version_id
    version_dir.mkdir(parents=True)
    (version_dir / f"{version_id}.json").write_text(
        json.dumps({"id": version_id, **payload}),
        encoding="utf-8",
    )


def test_download_worker_normalizes_loader_version_before_auto_install(monkeypatch, tmp_path):
    write_version(tmp_path, "1.20.1", {"type": "release"})
    write_version(tmp_path, "fabric-loader-0.16.0-1.20.1", {"inheritsFrom": "1.20.1"})
    calls = {"metadata": [], "download": [], "engine": []}

    monkeypatch.setattr(
        install_workers,
        "get_version_metadata_with_fallback",
        lambda version_id, *_args, **_kwargs: (
            calls["metadata"].append(version_id) or {"id": version_id, "libraries": [], "downloads": {}},
            "official",
        ),
    )

    async def fake_download_game_files(version_json, game_dir, version_id, *_args, **_kwargs):
        calls["download"].append(version_id)

    monkeypatch.setattr(install_workers, "download_game_files", fake_download_game_files)
    monkeypatch.setattr(install_workers, "extract_natives", lambda *_args, **_kwargs: None)

    class FakeInstallerEngine:
        def __init__(self, minecraft_version, *_args, **_kwargs):
            calls["engine"].append(minecraft_version)

        def install_sequence(self, install_types):
            return {"installed_version": "fabric-loader-0.16.0-1.20.1", "steps": list(install_types)}

    monkeypatch.setattr(install_workers, "InstallerEngine", FakeInstallerEngine)

    worker = DownloadWorker(
        "fabric-loader-0.16.0-1.20.1",
        "official",
        str(tmp_path),
        auto_install_types=["fabric"],
    )
    worker.run()

    assert calls["metadata"] == ["1.20.1"]
    assert calls["download"] == ["1.20.1"]
    assert calls["engine"] == ["1.20.1"]
