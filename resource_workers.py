from PyQt6.QtCore import QThread, QObject, pyqtSignal as Signal

from log_utils import get_logger
from resource_market import (
    cache_resource_screenshot,
    find_compatible_modrinth_version,
    get_curseforge_resource_detail,
    get_local_resource_detail,
    get_modrinth_resource_detail,
    hit_has_modrinth_compatibility,
    install_modrinth_resource,
    list_local_resources,
    search_curseforge_resources,
    search_modrinth_resources,
)


logger = get_logger(__name__)


class ResourceSearchWorker(QObject):
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, query, resource_type, game_version, loader, source="modrinth", sort_index="relevance", target_dir=""):
        super().__init__()
        self.query = query
        self.resource_type = resource_type
        self.game_version = game_version
        self.loader = loader
        self.source = source
        self.sort_index = sort_index
        self.target_dir = target_dir

    def run(self):
        try:
            logger.info(
                "ResourceSearchWorker started: source=%s query=%s type=%s game=%s loader=%s sort=%s target=%s",
                self.source,
                self.query,
                self.resource_type,
                self.game_version or "<any>",
                self.loader or "<any>",
                self.sort_index,
                self.target_dir,
            )
            self.status.emit("正在搜索资源市场...")
            if self.source == "modrinth":
                hits = search_modrinth_resources(
                    self.query,
                    self.resource_type,
                    self.game_version,
                    self.loader,
                    index=self.sort_index,
                )
            elif self.source == "curseforge":
                hits = search_curseforge_resources(self.query, self.resource_type, self.game_version, self.loader)
            elif self.source == "local":
                hits = list_local_resources(self.query, self.resource_type, self.target_dir)
            else:
                raise RuntimeError(f"不支持的资源来源：{self.source}")
            for hit in hits:
                hit["target_game_version"] = self.game_version
                hit["target_loader"] = self.loader
                hit["compatible"] = True
                if self.source == "modrinth":
                    search_hint_compatible = hit_has_modrinth_compatibility(
                        hit,
                        self.resource_type,
                        self.game_version,
                        self.loader,
                    )
                    hit["compatibility_unverified"] = True
                    hit["compatibility_checking"] = True
                    if not search_hint_compatible:
                        hit["compatibility_hint"] = "搜索结果未列出目标版本或加载器，安装时会重新确认"
            compatible_count = sum(1 for hit in hits if hit.get("compatible", True))
            unverified_count = sum(1 for hit in hits if hit.get("compatibility_unverified"))
            logger.info(
                "ResourceSearchWorker finished: source=%s query=%s hits=%d compatible=%d unverified=%d",
                self.source,
                self.query,
                len(hits),
                compatible_count,
                unverified_count,
            )
            self.finished.emit({
                "resource_type": self.resource_type,
                "query": self.query,
                "source": self.source,
                "hits": hits,
            })
        except Exception as exc:
            logger.exception("ResourceSearchWorker failed: source=%s query=%s", self.source, self.query)
            self.failed.emit(str(exc))


class ResourceCompatibilityWorker(QObject):
    checked = Signal(dict)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, hits, resource_type, game_version, loader, generation=0):
        super().__init__()
        self.hits = [dict(hit) for hit in hits]
        self.resource_type = resource_type
        self.game_version = game_version
        self.loader = loader
        self.generation = generation

    def run(self):
        checked_count = 0
        compatible_count = 0
        try:
            logger.info(
                "ResourceCompatibilityWorker started: hits=%d type=%s game=%s loader=%s",
                len(self.hits),
                self.resource_type,
                self.game_version or "<any>",
                self.loader or "<any>",
            )
            for index, hit in enumerate(self.hits):
                thread = QThread.currentThread()
                if thread.isInterruptionRequested():
                    logger.info("ResourceCompatibilityWorker interrupted: checked=%d", checked_count)
                    break
                project_id = hit.get("project_id") or hit.get("slug")
                if hit.get("source") != "modrinth" or not project_id:
                    continue
                updated = dict(hit)
                updated["compatibility_index"] = index
                try:
                    compatible_version = find_compatible_modrinth_version(
                        project_id,
                        self.resource_type,
                        self.game_version,
                        self.loader,
                    )
                except Exception as exc:
                    logger.warning(
                        "Failed to verify Modrinth compatibility: project=%s game=%s loader=%s error=%s",
                        project_id,
                        self.game_version,
                        self.loader,
                        exc,
                    )
                    compatible_version = None
                    updated["compatibility_error"] = str(exc)
                checked_count += 1
                updated["compatibility_generation"] = self.generation
                updated["compatibility_checking"] = False
                updated["compatibility_unverified"] = False
                updated["compatible"] = bool(compatible_version)
                if compatible_version:
                    compatible_count += 1
                    updated["compatible_version"] = compatible_version.get("version_number", "")
                    updated.pop("compatibility_error", None)
                else:
                    updated["compatible_version"] = ""
                self.checked.emit(updated)
            logger.info(
                "ResourceCompatibilityWorker finished: checked=%d compatible=%d",
                checked_count,
                compatible_count,
            )
            self.finished.emit({"checked": checked_count, "compatible": compatible_count, "generation": self.generation})
        except Exception as exc:
            logger.exception("ResourceCompatibilityWorker failed")
            self.failed.emit(str(exc))


class ResourceDetailWorker(QObject):
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, hit, resource_type, game_version, loader):
        super().__init__()
        self.hit = dict(hit)
        self.resource_type = resource_type
        self.game_version = game_version
        self.loader = loader

    def run(self):
        try:
            source = self.hit.get("source", "modrinth")
            logger.info(
                "ResourceDetailWorker started: source=%s project=%s type=%s game=%s loader=%s",
                source,
                self.hit.get("project_id") or self.hit.get("slug") or self.hit.get("path"),
                self.resource_type,
                self.game_version or "<any>",
                self.loader or "<any>",
            )
            if source == "modrinth":
                detail = get_modrinth_resource_detail(
                    self.hit.get("project_id") or self.hit.get("slug"),
                    self.resource_type,
                    self.game_version,
                    self.loader,
                )
            elif source == "curseforge":
                detail = get_curseforge_resource_detail(self.hit.get("project_id"))
            elif source == "local":
                detail = get_local_resource_detail(self.hit.get("path") or self.hit.get("project_id"))
            else:
                raise RuntimeError(f"不支持的资源来源：{source}")
            detail["hit"] = self.hit
            cached_screenshots = []
            for screenshot in detail.get("screenshots", [])[:5]:
                try:
                    cached = cache_resource_screenshot(screenshot)
                except Exception:
                    cached = ""
                cached_screenshots.append({
                    "url": screenshot,
                    "path": cached,
                })
            detail["screenshots"] = cached_screenshots
            logger.info(
                "ResourceDetailWorker finished: source=%s title=%s versions=%d screenshots=%d",
                source,
                detail.get("title", ""),
                len(detail.get("versions", [])),
                len(detail.get("screenshots", [])),
            )
            self.finished.emit(detail)
        except Exception as exc:
            logger.exception(
                "ResourceDetailWorker failed: source=%s project=%s",
                self.hit.get("source", "modrinth"),
                self.hit.get("project_id") or self.hit.get("slug") or self.hit.get("path"),
            )
            self.failed.emit(str(exc))


class ResourceInstallWorker(QObject):
    progress = Signal(int)
    metrics = Signal(dict)
    status = Signal(str)
    finished = Signal(dict)
    failed = Signal(str)

    def __init__(self, project_id, title, resource_type, game_version, loader, target_dir, source="modrinth", install_dependencies=False):
        super().__init__()
        self.project_id = project_id
        self.title = title
        self.resource_type = resource_type
        self.game_version = game_version
        self.loader = loader
        self.target_dir = target_dir
        self.source = source
        self.install_dependencies = install_dependencies

    def run(self):
        try:
            logger.info(
                "ResourceInstallWorker started: source=%s project=%s title=%s type=%s game=%s loader=%s target=%s dependencies=%s",
                self.source,
                self.project_id,
                self.title,
                self.resource_type,
                self.game_version or "<any>",
                self.loader or "<any>",
                self.target_dir,
                self.install_dependencies,
            )
            if self.source != "modrinth":
                raise RuntimeError("当前只支持从 Modrinth 一键安装；CurseForge 需要手动下载或使用整合包导入。")
            self.status.emit(f"正在安装资源：{self.title}")
            self.progress.emit(20)

            def on_progress(snapshot):
                value = snapshot.get("progress", 0.0)
                self.progress.emit(max(20, min(96, int(20 + value * 76))))
                self.metrics.emit(snapshot)

            payload = install_modrinth_resource(
                self.project_id,
                self.resource_type,
                self.game_version,
                self.loader,
                self.target_dir,
                install_dependencies=self.install_dependencies,
                status_callback=self.status.emit,
                progress_callback=on_progress,
            )
            payload.update({
                "project_id": self.project_id,
                "title": self.title,
                "resource_type": self.resource_type,
                "source": self.source,
            })
            self.progress.emit(100)
            logger.info("ResourceInstallWorker finished: project=%s payload=%s", self.project_id, payload)
            self.finished.emit(payload)
        except Exception as exc:
            logger.exception("ResourceInstallWorker failed: source=%s project=%s", self.source, self.project_id)
            self.failed.emit(str(exc))
