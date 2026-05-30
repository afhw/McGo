import asyncio
import json
import os
import shutil
import tempfile
import threading

from PyQt6.QtCore import QThread, QObject, pyqtSignal as Signal

from downloader import collect_missing_game_files, download_game_files, extract_natives, repair_game_files
from external_auth import authlib_injector_download_url
from file_utils import sanitize_filename
from install_services import MIRROR_SOURCES, get_version_metadata_with_fallback, stream_download
from installer_engine import InstallerEngine
from java_runtime import extract_java_archive, find_java_in_directory, java_runtime_download_url
from launcher import get_version_inheritance_chain
from log_utils import get_logger


logger = get_logger(__name__)


def _progress_percent(snapshot):
    value = snapshot.get("progress", 0.0)
    return max(0, min(100, int(value * 100)))


class DownloadWorker(QObject):
    progress = Signal(int)
    metrics = Signal(dict)
    status = Signal(str)
    install_metrics = Signal(dict)
    install_status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(
        self,
        version_id,
        mirror_source,
        game_dir,
        auto_install_types=None,
        java_path="",
        download_options=None,
        global_isolation=True,
    ):
        super().__init__()
        self.version_id = version_id
        self.mirror_source = mirror_source
        self.game_dir = os.path.abspath(game_dir)
        self.auto_install_types = list(auto_install_types or [])
        self.java_path = java_path
        self.download_options = dict(download_options or {})
        self.global_isolation = global_isolation
        self._cancel_event = threading.Event()

    def request_stop(self):
        self._cancel_event.set()

    def is_cancel_requested(self):
        thread = QThread.currentThread()
        return self._cancel_event.is_set() or (thread and thread.isInterruptionRequested())

    def run(self):
        try:
            logger.info(
                "DownloadWorker started: version=%s mirror=%s game_dir=%s auto_install=%s java=%s",
                self.version_id,
                self.mirror_source,
                self.game_dir,
                self.auto_install_types,
                self.java_path,
            )
            self.status.emit(f"正在获取 {self.version_id} 版本信息...")
            version_json, resolved_source = get_version_metadata_with_fallback(
                self.version_id,
                self.mirror_source,
                status_callback=self.status.emit,
            )
            resolved_mirror_root = MIRROR_SOURCES[resolved_source]
            logger.debug("Version metadata loaded: version=%s keys=%s", self.version_id, sorted(version_json.keys()))

            def on_progress(snapshot):
                self.progress.emit(_progress_percent(snapshot))
                self.metrics.emit(snapshot)

            self.status.emit(f"正在下载 Minecraft {self.version_id}...")
            asyncio.run(download_game_files(
                version_json,
                self.game_dir,
                self.version_id,
                resolved_mirror_root,
                progress_callback=on_progress,
                cancel_callback=self.is_cancel_requested,
                **self.download_options,
            ))
            if self.is_cancel_requested():
                raise RuntimeError("下载任务已取消")
            self.status.emit("正在解压 natives...")
            extract_natives(version_json, self.game_dir, self.version_id)
            payload = {"version": self.version_id}
            if self.auto_install_types:
                self.install_status.emit("原版下载完成，开始安装附加组件...")

                def on_install_progress(snapshot):
                    self.progress.emit(_progress_percent(snapshot))
                    self.install_metrics.emit(snapshot)

                engine = InstallerEngine(
                    self.version_id,
                    self.mirror_source,
                    self.game_dir,
                    self.java_path,
                    status_callback=self.install_status.emit,
                    progress_callback=on_install_progress,
                    global_isolation=self.global_isolation,
                )
                install_payload = engine.install_sequence(self.auto_install_types)
                payload["post_install"] = install_payload
            self.progress.emit(100)
            logger.info("DownloadWorker finished: payload=%s", payload)
            self.finished.emit(payload)
        except Exception as exc:
            logger.exception("DownloadWorker failed: version=%s", self.version_id)
            self.failed.emit(str(exc))


class InstallWorker(QObject):
    progress = Signal(int)
    metrics = Signal(dict)
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, install_type, minecraft_version, mirror_source, game_dir, java_path, global_isolation=True):
        super().__init__()
        self.install_type = install_type
        self.minecraft_version = minecraft_version
        self.mirror_source = mirror_source
        self.game_dir = game_dir
        self.java_path = java_path
        self.global_isolation = global_isolation
        self._cancel_event = threading.Event()

    def request_stop(self):
        self._cancel_event.set()

    def emit_snapshot(self, snapshot):
        self.progress.emit(_progress_percent(snapshot))
        self.metrics.emit(snapshot)

    def run(self):
        try:
            logger.info(
                "InstallWorker started: install_type=%s minecraft_version=%s mirror=%s game_dir=%s java=%s",
                self.install_type,
                self.minecraft_version,
                self.mirror_source,
                self.game_dir,
                self.java_path,
            )
            engine = InstallerEngine(
                self.minecraft_version,
                self.mirror_source,
                self.game_dir,
                self.java_path,
                status_callback=self.status.emit,
                progress_callback=self.emit_snapshot,
                global_isolation=self.global_isolation,
            )
            payload = engine.install(self.install_type)
            payload["install_type"] = self.install_type
            self.progress.emit(100)
            logger.info("InstallWorker finished: payload=%s", payload)
            self.finished.emit(payload)
        except Exception as exc:
            logger.exception(
                "InstallWorker failed: install_type=%s minecraft_version=%s",
                self.install_type,
                self.minecraft_version,
            )
            self.failed.emit(str(exc))


class RepairWorker(QObject):
    progress = Signal(int)
    metrics = Signal(dict)
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, version_id, mirror_source, game_dir, download_options=None):
        super().__init__()
        self.version_id = version_id
        self.mirror_source = mirror_source
        self.game_dir = os.path.abspath(game_dir)
        self.download_options = dict(download_options or {})
        self._cancel_event = threading.Event()

    def request_stop(self):
        self._cancel_event.set()

    def is_cancel_requested(self):
        thread = QThread.currentThread()
        return self._cancel_event.is_set() or (thread and thread.isInterruptionRequested())

    def run(self):
        try:
            logger.info(
                "RepairWorker started: version=%s mirror=%s game_dir=%s",
                self.version_id,
                self.mirror_source,
                self.game_dir,
            )
            chain = get_version_inheritance_chain(self.game_dir, self.version_id)
            if not chain:
                raise RuntimeError(f"未找到版本清单：{self.version_id}")

            def on_progress(snapshot):
                self.progress.emit(_progress_percent(snapshot))
                self.metrics.emit(snapshot)

            total_missing_before = 0
            total_missing_after = 0
            report_path = ""
            for index, version_json in enumerate(reversed(chain), start=1):
                if self.is_cancel_requested():
                    raise RuntimeError("修复任务已取消")
                version_id = version_json.get("id", self.version_id)
                self.status.emit(f"正在校验并补全 {version_id}（{index}/{len(chain)}）...")
                missing_before = collect_missing_game_files(version_json, self.game_dir, version_id)
                logger.info("Repair scan before: version=%s missing=%d", version_id, len(missing_before))
                total_missing_before += len(missing_before)
                asyncio.run(repair_game_files(
                    version_json,
                    self.game_dir,
                    version_id,
                    MIRROR_SOURCES[self.mirror_source],
                    progress_callback=on_progress,
                    cancel_callback=self.is_cancel_requested,
                    **self.download_options,
                ))
                missing_after = collect_missing_game_files(version_json, self.game_dir, version_id)
                logger.info("Repair scan after: version=%s missing=%d", version_id, len(missing_after))
                total_missing_after += len(missing_after)
                if missing_after:
                    report_path = os.path.join(self.game_dir, "versions", self.version_id, "repair-missing-files.json")
                    os.makedirs(os.path.dirname(report_path), exist_ok=True)
                    with open(report_path, "w", encoding="utf-8") as file_handle:
                        json.dump(missing_after, file_handle, ensure_ascii=False, indent=2)
            self.progress.emit(100)
            logger.info(
                "RepairWorker finished: version=%s checked=%d missing_before=%d missing_after=%d report=%s",
                self.version_id,
                len(chain),
                total_missing_before,
                total_missing_after,
                report_path,
            )
            self.finished.emit({
                "version": self.version_id,
                "checked_versions": len(chain),
                "missing_before": total_missing_before,
                "missing_after": total_missing_after,
                "report_path": report_path,
            })
        except Exception as exc:
            logger.exception("RepairWorker failed: version=%s", self.version_id)
            self.failed.emit(str(exc))


class AuthlibInjectorDownloadWorker(QObject):
    progress = Signal(int)
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, install_dir):
        super().__init__()
        self.install_dir = os.path.abspath(install_dir)

    def run(self):
        try:
            self.status.emit("正在获取 authlib-injector 下载信息...")
            url, filename, _ = authlib_injector_download_url()
            os.makedirs(self.install_dir, exist_ok=True)
            target_path = os.path.join(self.install_dir, sanitize_filename(filename, "authlib-injector.jar"))

            def on_progress(snapshot):
                self.progress.emit(_progress_percent(snapshot))
                self.status.emit(f"{snapshot.get('phase', '下载 authlib-injector')}：{snapshot.get('current_file', filename)}")

            stream_download(url, target_path, on_progress, "下载 authlib-injector")
            self.progress.emit(100)
            self.finished.emit({"path": target_path})
        except Exception as exc:
            self.failed.emit(str(exc))


class JavaDownloadWorker(QObject):
    progress = Signal(int)
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, major_version, install_root):
        super().__init__()
        self.major_version = int(major_version)
        self.install_root = os.path.abspath(install_root)

    def run(self):
        temp_dir = tempfile.mkdtemp(prefix="mcgo-java-")
        try:
            self.status.emit(f"正在获取 Java {self.major_version} 下载信息...")
            url, filename = java_runtime_download_url(self.major_version)
            archive_path = os.path.join(temp_dir, filename)

            def progress_callback(snapshot):
                value = snapshot.get("progress", 0.0)
                self.progress.emit(max(0, min(90, int(value * 90))))
                current = snapshot.get("current_file") or filename
                self.status.emit(f"{snapshot.get('phase', '下载 Java')}：{current}")

            self.status.emit(f"正在下载 Java {self.major_version}...")
            stream_download(url, archive_path, progress_callback, f"下载 Java {self.major_version}")
            target_dir = os.path.join(self.install_root, f"temurin-{self.major_version}")
            if os.path.isdir(target_dir):
                shutil.rmtree(target_dir)
            os.makedirs(target_dir, exist_ok=True)
            self.status.emit("正在解压 Java 运行时...")
            self.progress.emit(94)
            extract_java_archive(archive_path, target_dir)
            java_path = find_java_in_directory(target_dir)
            if not java_path:
                raise RuntimeError("解压完成但未找到 Java 可执行文件。")
            self.progress.emit(100)
            self.finished.emit({
                "major": self.major_version,
                "java_path": os.path.abspath(java_path),
                "install_dir": target_dir,
            })
        except Exception as exc:
            self.failed.emit(str(exc))
        finally:
            shutil.rmtree(temp_dir, ignore_errors=True)
