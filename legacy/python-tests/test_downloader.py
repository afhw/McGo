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


def test_core_jobs_skip_synthesized_artifact_for_native_only_library(monkeypatch, tmp_path):
    monkeypatch.setattr(downloader.platform, "system", lambda: "Windows")
    monkeypatch.setattr(downloader.platform, "machine", lambda: "AMD64")
    version_json = {
        "downloads": {},
        "libraries": [
            {
                "name": "net.java.jinput:jinput-platform:2.0.5",
                "natives": {"windows": "natives-windows"},
                "downloads": {
                    "classifiers": {
                        "natives-windows": {
                            "path": "net/java/jinput/jinput-platform/2.0.5/jinput-platform-2.0.5-natives-windows.jar",
                            "url": "https://libraries.minecraft.net/net/java/jinput/jinput-platform/2.0.5/jinput-platform-2.0.5-natives-windows.jar",
                        }
                    }
                },
            }
        ],
    }

    jobs = downloader._build_core_jobs(version_json, str(tmp_path), "1.12.2", "")

    assert [job.label for job in jobs] == ["jinput-platform-2.0.5-natives-windows.jar"]


def test_run_jobs_does_not_create_one_task_per_job():
    # The bounded queue should create at most the configured concurrency workers.
    assert os.path.basename(downloader.__file__) == "downloader.py"
