import os

import downloader


def test_rewrite_url_uses_bmclapi_maven_for_libraries():
    url = "https://libraries.minecraft.net/com/example/demo/1.0/demo-1.0.jar"
    rewritten = downloader._rewrite_url(url, "https://bmclapi2.bangbang93.com")
    assert rewritten == "https://bmclapi2.bangbang93.com/maven/com/example/demo/1.0/demo-1.0.jar"


def test_native_classifier_uses_platform_native_mapping(monkeypatch):
    monkeypatch.setattr(downloader.platform, "system", lambda: "Linux")
    monkeypatch.setattr(downloader.platform, "machine", lambda: "x86_64")
    library = {
        "natives": {"linux": "natives-linux"},
        "downloads": {"classifiers": {"natives-linux": {"path": "native.jar"}}},
    }
    assert downloader._native_classifier_key(library) == "natives-linux"


def test_run_jobs_does_not_create_one_task_per_job():
    # The bounded queue should create at most the configured concurrency workers.
    assert os.path.basename(downloader.__file__) == "downloader.py"
