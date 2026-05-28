# downloader.py
import asyncio
import hashlib
import json
import os
import shutil
import time
import zipfile
from dataclasses import dataclass

import aiohttp

from log_utils import get_logger

CHUNK_SIZE = 128 * 1024
MAX_CORE_CONCURRENCY = 12
MAX_ASSET_CONCURRENCY = 24
REQUEST_TIMEOUT = aiohttp.ClientTimeout(total=None, sock_connect=20, sock_read=60)
logger = get_logger(__name__)


@dataclass
class DownloadJob:
    url: str
    file_path: str
    relative_path: str
    size: int = 0
    sha1: str = ""
    label: str = ""


class DownloadProgress:
    def __init__(self, callback=None, emit_interval=0.2):
        self.callback = callback
        self.emit_interval = emit_interval
        self.total_bytes = 0
        self.ready_bytes = 0
        self.total_files = 0
        self.completed_files = 0
        self.reused_files = 0
        self.phase = "准备下载"
        self.current_file = ""
        self._last_emit = time.monotonic()
        self._last_speed_calc = self._last_emit
        self._network_window_bytes = 0
        self._speed_bytes = 0.0

    def add_totals(self, total_bytes, total_files):
        self.total_bytes += max(0, int(total_bytes or 0))
        self.total_files += max(0, int(total_files or 0))
        self.emit(force=True)

    def set_phase(self, phase, current_file=""):
        self.phase = phase
        if current_file:
            self.current_file = current_file
        self.emit(force=True)

    def set_current_file(self, current_file):
        self.current_file = current_file
        self.emit()

    def advance_network(self, byte_count):
        byte_count = max(0, int(byte_count or 0))
        self.ready_bytes += byte_count
        self._network_window_bytes += byte_count
        self.emit()

    def advance_reused(self, byte_count):
        self.ready_bytes += max(0, int(byte_count or 0))
        self.reused_files += 1
        self.emit(force=True)

    def finish_file(self):
        self.completed_files += 1
        self.emit(force=True)

    def emit(self, force=False):
        if not self.callback:
            return

        now = time.monotonic()
        elapsed = now - self._last_emit
        if not force and elapsed < self.emit_interval:
            return

        speed_elapsed = max(now - self._last_speed_calc, 1e-6)
        self._speed_bytes = self._network_window_bytes / speed_elapsed
        self._network_window_bytes = 0
        self._last_emit = now
        self._last_speed_calc = now
        self.callback(self.snapshot())

    def snapshot(self):
        progress = 0.0
        if self.total_bytes > 0:
            progress = min(1.0, self.ready_bytes / self.total_bytes)
        elif self.total_files > 0:
            progress = min(1.0, self.completed_files / self.total_files)

        return {
            "progress": progress,
            "phase": self.phase,
            "current_file": self.current_file,
            "completed_files": self.completed_files,
            "total_files": self.total_files,
            "reused_files": self.reused_files,
            "downloaded_bytes": self.ready_bytes,
            "total_bytes": self.total_bytes,
            "speed_bytes": self._speed_bytes,
        }


def _ensure_parent(path):
    parent = os.path.dirname(path)
    if parent:
        os.makedirs(parent, exist_ok=True)


def _file_size(path):
    try:
        return os.path.getsize(path)
    except OSError:
        return 0


def _sha1_file(path):
    sha1 = hashlib.sha1()
    with open(path, "rb") as file_handle:
        for chunk in iter(lambda: file_handle.read(CHUNK_SIZE), b""):
            sha1.update(chunk)
    return sha1.hexdigest()


def _matches_file(path, expected_size=0, expected_sha1=""):
    if not os.path.exists(path):
        return False
    if expected_size and _file_size(path) != expected_size:
        return False
    if expected_sha1:
        try:
            if _sha1_file(path).lower() != expected_sha1.lower():
                return False
        except OSError:
            return False
    return os.path.isfile(path)


def _candidate_cache_dirs(game_directory):
    normalized = os.path.abspath(game_directory)
    candidates = []

    def add_candidate(path):
        if not path:
            return
        absolute = os.path.abspath(path)
        if absolute not in candidates:
            candidates.append(absolute)

    add_candidate(normalized)
    add_candidate(os.path.join(os.getcwd(), ".minecraft"))
    appdata = os.getenv("APPDATA")
    if appdata:
        add_candidate(os.path.join(appdata, ".minecraft"))
    add_candidate(os.path.join(os.path.expanduser("~"), ".minecraft"))
    return candidates


def _try_copy_from_cache(job, cache_dirs, target_game_dir):
    target_root = os.path.abspath(target_game_dir)
    for cache_dir in cache_dirs:
        cache_root = os.path.abspath(cache_dir)
        if cache_root == target_root:
            continue

        source_path = os.path.join(cache_root, job.relative_path)
        if not _matches_file(source_path, job.size, job.sha1):
            continue

        _ensure_parent(job.file_path)
        shutil.copy2(source_path, job.file_path)
        logger.debug("Reused cached file: %s <- %s", job.relative_path, source_path)
        return True

    return False


def _rewrite_url(url, mirror_source):
    bmclapi = "https://bmclapi2.bangbang93.com"
    if mirror_source == bmclapi:
        replacements = {
            "https://piston-data.mojang.com": mirror_source,
            "https://launcher.mojang.com": mirror_source,
            "https://libraries.minecraft.net": f"{mirror_source}/maven",
            "https://maven.fabricmc.net": f"{mirror_source}/maven",
            "https://maven.minecraftforge.net": f"{mirror_source}/maven",
            "https://maven.neoforged.net": f"{mirror_source}/maven",
            "https://resources.download.minecraft.net": f"{mirror_source}/assets",
        }
    else:
        replacements = {
            f"{bmclapi}/maven": "https://libraries.minecraft.net",
            f"{bmclapi}/assets": "https://resources.download.minecraft.net",
            bmclapi: "https://piston-data.mojang.com",
        }
    for source, target in replacements.items():
        if url.startswith(source):
            return url.replace(source, target, 1)
    return url


def _alternate_mirror(mirror_source):
    if mirror_source == "https://launchermeta.mojang.com":
        return "https://bmclapi2.bangbang93.com"
    if mirror_source == "https://bmclapi2.bangbang93.com":
        return "https://launchermeta.mojang.com"
    return ""


def _candidate_urls(url, mirror_source):
    urls = []
    for source in (mirror_source, _alternate_mirror(mirror_source)):
        if not source:
            continue
        candidate = _rewrite_url(url, source)
        if candidate not in urls:
            urls.append(candidate)
    if url not in urls:
        urls.append(url)
    return urls


def _maven_library_path(name):
    parts = str(name or "").split(":")
    if len(parts) < 3:
        return None

    group, artifact, version = parts[:3]
    classifier = parts[3] if len(parts) >= 4 else ""
    extension = parts[4] if len(parts) >= 5 else "jar"
    filename = f"{artifact}-{version}"
    if classifier:
        filename = f"{filename}-{classifier}"
    return os.path.join(*group.split("."), artifact, version, f"{filename}.{extension}")


def _maven_library_url(library, relative_path):
    base_url = (library.get("url") or "https://libraries.minecraft.net/").rstrip("/")
    return f"{base_url}/{relative_path.replace(os.sep, '/')}"


async def _throttle_download(progress, speed_limit_bps, started_at):
    if not speed_limit_bps:
        return
    expected_elapsed = progress.ready_bytes / max(speed_limit_bps, 1)
    actual_elapsed = time.monotonic() - started_at
    delay = expected_elapsed - actual_elapsed
    if delay > 0:
        await asyncio.sleep(min(delay, 1.0))


async def _download_with_retries(session, job, progress, semaphore, retries=5, mirror_source="", speed_limit_bps=0):
    for attempt in range(retries):
        try:
            async with semaphore:
                await _download_single(session, job, progress, mirror_source=mirror_source, speed_limit_bps=speed_limit_bps)
            return
        except Exception as exc:
            if attempt == retries - 1:
                logger.exception(
                    "Download failed after %d attempts: label=%s url=%s target=%s",
                    retries,
                    job.label,
                    job.url,
                    job.file_path,
                )
                raise
            logger.warning(
                "Download attempt %d/%d failed: label=%s url=%s error=%s",
                attempt + 1,
                retries,
                job.label,
                job.url,
                exc,
            )
            await asyncio.sleep(min(2 ** attempt, 5))


async def _download_single(session, job, progress, mirror_source="", speed_limit_bps=0):
    _ensure_parent(job.file_path)
    progress.set_current_file(job.label)
    logger.debug("Downloading file: label=%s size=%s url=%s target=%s", job.label, job.size, job.url, job.file_path)

    errors = []
    started_at = time.monotonic()
    for candidate_url in _candidate_urls(job.url, mirror_source):
        try:
            async with session.get(candidate_url) as response:
                response.raise_for_status()
                with open(job.file_path, "wb") as file_handle:
                    async for chunk in response.content.iter_chunked(CHUNK_SIZE):
                        if not chunk:
                            continue
                        file_handle.write(chunk)
                        progress.advance_network(len(chunk))
                        await _throttle_download(progress, speed_limit_bps, started_at)
            logger.debug("Downloaded file: label=%s bytes=%s target=%s", job.label, _file_size(job.file_path), job.file_path)
            return
        except Exception as exc:
            errors.append(f"{candidate_url}: {exc}")
            logger.warning("Download source failed: label=%s url=%s error=%s", job.label, candidate_url, exc)
            try:
                if os.path.exists(job.file_path):
                    os.remove(job.file_path)
            except OSError:
                logger.debug("Failed to remove partial file: %s", job.file_path)
    raise RuntimeError("；".join(errors))


async def _process_job(session, job, progress, semaphore, cache_dirs, target_game_dir, mirror_source="", speed_limit_bps=0, cache_strategy="reuse"):
    if _matches_file(job.file_path, job.size, job.sha1):
        progress.set_current_file(f"{job.label}（已存在）")
        progress.advance_reused(job.size or _file_size(job.file_path))
        progress.finish_file()
        logger.debug("File already valid: %s", job.relative_path)
        return "reused"

    if cache_strategy == "reuse" and _try_copy_from_cache(job, cache_dirs, target_game_dir):
        progress.set_current_file(f"{job.label}（本地复用）")
        progress.advance_reused(job.size or _file_size(job.file_path))
        progress.finish_file()
        return "reused"

    await _download_with_retries(session, job, progress, semaphore, mirror_source=mirror_source, speed_limit_bps=speed_limit_bps)
    progress.finish_file()
    return "downloaded"


async def _run_jobs(session, jobs, progress, concurrency, cache_dirs, target_game_dir, mirror_source="", speed_limit_bps=0, cache_strategy="reuse"):
    if not jobs:
        return
    semaphore = asyncio.Semaphore(concurrency)
    await asyncio.gather(*[
        _process_job(session, job, progress, semaphore, cache_dirs, target_game_dir, mirror_source=mirror_source, speed_limit_bps=speed_limit_bps, cache_strategy=cache_strategy)
        for job in jobs
    ])


def _build_core_jobs(version_json, game_directory, version_id, mirror_source):
    jobs = []

    client_info = version_json.get("downloads", {}).get("client", {})
    if client_info.get("url"):
        client_relative = os.path.join("versions", version_id, f"{version_id}.jar")
        jobs.append(DownloadJob(
            url=_rewrite_url(client_info["url"], mirror_source),
            file_path=os.path.join(game_directory, client_relative),
            relative_path=client_relative,
            size=client_info.get("size", 0),
            sha1=client_info.get("sha1", ""),
            label=f"{version_id}.jar",
        ))

    for library in version_json.get("libraries", []):
        downloads = library.get("downloads", {})
        artifact = downloads.get("artifact")
        if artifact:
            jobs.append(DownloadJob(
                url=_rewrite_url(artifact["url"], mirror_source),
                file_path=os.path.join(game_directory, "libraries", artifact["path"]),
                relative_path=os.path.join("libraries", artifact["path"]),
                size=artifact.get("size", 0),
                sha1=artifact.get("sha1", ""),
                label=os.path.basename(artifact["path"]),
            ))
        else:
            relative_library_path = _maven_library_path(library.get("name"))
            if relative_library_path:
                jobs.append(DownloadJob(
                    url=_rewrite_url(_maven_library_url(library, relative_library_path), mirror_source),
                    file_path=os.path.join(game_directory, "libraries", relative_library_path),
                    relative_path=os.path.join("libraries", relative_library_path),
                    size=library.get("size", 0),
                    sha1=library.get("sha1", ""),
                    label=os.path.basename(relative_library_path),
                ))

        natives = downloads.get("classifiers", {}).get("natives-windows")
        if natives:
            jobs.append(DownloadJob(
                url=_rewrite_url(natives["url"], mirror_source),
                file_path=os.path.join(game_directory, "libraries", natives["path"]),
                relative_path=os.path.join("libraries", natives["path"]),
                size=natives.get("size", 0),
                sha1=natives.get("sha1", ""),
                label=os.path.basename(natives["path"]),
            ))
    return jobs


def _build_asset_index_job(version_json, game_directory, mirror_source):
    asset_index = version_json.get("assetIndex", {})
    if not asset_index.get("id") or not asset_index.get("url"):
        return None
    asset_index_relative = os.path.join("assets", "indexes", f"{asset_index['id']}.json")
    return DownloadJob(
        url=_rewrite_url(asset_index["url"], mirror_source),
        file_path=os.path.join(game_directory, asset_index_relative),
        relative_path=asset_index_relative,
        size=asset_index.get("size", 0),
        sha1=asset_index.get("sha1", ""),
        label=f"{asset_index['id']}.json",
    )


def _build_asset_jobs(asset_index_json, game_directory, mirror_source):
    jobs = []
    for object_hash, object_info in (
        (info["hash"], info) for info in asset_index_json.get("objects", {}).values()
    ):
        prefix = object_hash[:2]
        relative_path = os.path.join("assets", "objects", prefix, object_hash)
        jobs.append(DownloadJob(
            url=_rewrite_url(f"https://resources.download.minecraft.net/{prefix}/{object_hash}", mirror_source),
            file_path=os.path.join(game_directory, relative_path),
            relative_path=relative_path,
            size=object_info.get("size", 0),
            sha1=object_hash,
            label=object_hash,
        ))
    return jobs


def collect_missing_game_files(version_json, game_directory, version_id, include_assets=True):
    """Return files that are missing or fail size/SHA1 validation."""
    logger.info(
        "Collecting missing files: version=%s game_directory=%s include_assets=%s",
        version_id,
        os.path.abspath(game_directory),
        include_assets,
    )
    missing = []
    asset_index_job = _build_asset_index_job(version_json, game_directory, "")
    core_jobs = _build_core_jobs(version_json, game_directory, version_id, "")
    jobs = [*core_jobs]

    if asset_index_job:
        jobs.append(asset_index_job)

    if include_assets and asset_index_job and os.path.isfile(asset_index_job.file_path):
        try:
            with open(asset_index_job.file_path, "r", encoding="utf-8") as file_handle:
                asset_index_json = json.load(file_handle)
            jobs.extend(_build_asset_jobs(asset_index_json, game_directory, ""))
        except Exception as exc:
            missing.append({
                "path": asset_index_job.relative_path,
                "label": asset_index_job.label,
                "reason": f"资源索引无法读取：{exc}",
                "size": asset_index_job.size,
                "sha1": asset_index_job.sha1,
            })

    for job in jobs:
        if _matches_file(job.file_path, job.size, job.sha1):
            continue
        reason = "缺失"
        if os.path.exists(job.file_path):
            reason = "校验失败"
        missing.append({
            "path": job.relative_path,
            "label": job.label,
            "reason": reason,
            "size": job.size,
            "sha1": job.sha1,
            "url": job.url,
        })
    logger.info("Missing file scan finished: version=%s missing=%d", version_id, len(missing))
    return missing


async def download_assets(
    version_json,
    game_directory,
    version_id,
    mirror_source,
    progress_callback=None,
    max_asset_concurrency=MAX_ASSET_CONCURRENCY,
    speed_limit_kbps=0,
    cache_strategy="reuse",
):
    logger.info(
        "Starting asset download: version=%s game_directory=%s mirror=%s",
        version_id,
        os.path.abspath(game_directory),
        mirror_source,
    )
    progress = DownloadProgress(progress_callback)
    cache_dirs = _candidate_cache_dirs(game_directory)
    asset_index_path = os.path.join(
        game_directory, "assets", "indexes", f"{version_json['assetIndex']['id']}.json"
    )

    async with aiohttp.ClientSession(
        timeout=REQUEST_TIMEOUT,
        connector=aiohttp.TCPConnector(limit=max_asset_concurrency * 2, ttl_dns_cache=300),
    ) as session:
        with open(asset_index_path, "r", encoding="utf-8") as file_handle:
            asset_index_json = json.load(file_handle)
        jobs = _build_asset_jobs(asset_index_json, game_directory, mirror_source)
        logger.info("Asset jobs prepared: version=%s jobs=%d bytes=%d", version_id, len(jobs), sum(job.size for job in jobs))
        progress.set_phase("下载资源文件")
        progress.add_totals(sum(job.size for job in jobs), len(jobs))
        await _run_jobs(
            session,
            jobs,
            progress,
            max_asset_concurrency,
            cache_dirs,
            game_directory,
            mirror_source=mirror_source,
            speed_limit_bps=int(speed_limit_kbps or 0) * 1024,
            cache_strategy=cache_strategy,
        )
        progress.set_phase("资源文件下载完成")
        progress.emit(force=True)
    logger.info("Asset download finished: version=%s", version_id)


def extract_natives(version_json, game_directory, version_id):
    natives_directory = os.path.join(
        game_directory, "versions", version_id, f"{version_id}-natives"
    )
    os.makedirs(natives_directory, exist_ok=True)
    logger.info("Extracting natives: version=%s target=%s", version_id, natives_directory)

    for library in version_json.get("libraries", []):
        classifiers = library.get("downloads", {}).get("classifiers", {})
        natives_info = classifiers.get("natives-windows")
        if not natives_info:
            continue

        library_path = os.path.join(game_directory, "libraries", natives_info["path"])
        try:
            with zipfile.ZipFile(library_path, "r") as zip_ref:
                for file_info in zip_ref.infolist():
                    if file_info.filename.startswith("META-INF") or file_info.filename.endswith("/"):
                        continue

                    extract_path = os.path.normpath(os.path.join(natives_directory, file_info.filename))
                    if not extract_path.startswith(os.path.normpath(natives_directory)):
                        logger.warning("Skipping suspicious native path: archive=%s entry=%s", library_path, file_info.filename)
                        continue

                    zip_ref.extract(file_info, natives_directory)
        except FileNotFoundError:
            logger.warning("Native library file does not exist: %s", library_path)
        except Exception as exc:
            logger.exception("Failed to extract natives from %s: %s", library_path, exc)

    file_count = 0
    for _, _, files in os.walk(natives_directory):
        file_count += len(files)
    logger.info("Extracted natives: version=%s files=%d target=%s", version_id, file_count, natives_directory)


async def download_game_files(
    version_json,
    game_directory,
    version,
    mirror_source,
    progress_callback=None,
    max_core_concurrency=MAX_CORE_CONCURRENCY,
    max_asset_concurrency=MAX_ASSET_CONCURRENCY,
    speed_limit_kbps=0,
    cache_strategy="reuse",
):
    os.makedirs(game_directory, exist_ok=True)

    version_id = version_json["id"]
    logger.info(
        "Starting game file download: requested=%s resolved=%s game_directory=%s mirror=%s",
        version,
        version_id,
        os.path.abspath(game_directory),
        mirror_source,
    )
    version_json_relative = os.path.join("versions", version_id, f"{version_id}.json")
    version_json_path = os.path.join(game_directory, version_json_relative)
    _ensure_parent(version_json_path)
    with open(version_json_path, "w", encoding="utf-8") as file_handle:
        json.dump(version_json, file_handle, ensure_ascii=False, indent=4)

    progress = DownloadProgress(progress_callback)
    cache_dirs = _candidate_cache_dirs(game_directory)
    asset_index_job = _build_asset_index_job(version_json, game_directory, mirror_source)
    core_jobs = _build_core_jobs(version_json, game_directory, version_id, mirror_source)
    logger.info(
        "Core jobs prepared: version=%s core_jobs=%d has_asset_index=%s",
        version_id,
        len(core_jobs),
        bool(asset_index_job),
    )

    async with aiohttp.ClientSession(
        timeout=REQUEST_TIMEOUT,
        connector=aiohttp.TCPConnector(limit=max_asset_concurrency * 2, ttl_dns_cache=300),
    ) as session:
        asset_index_result = ""
        asset_jobs = []
        if asset_index_job:
            asset_index_result = await _process_job(
                session,
                asset_index_job,
                DownloadProgress(),
                asyncio.Semaphore(1),
                cache_dirs,
                game_directory,
                mirror_source=mirror_source,
                speed_limit_bps=int(speed_limit_kbps or 0) * 1024,
                cache_strategy=cache_strategy,
            )

            with open(asset_index_job.file_path, "r", encoding="utf-8") as file_handle:
                asset_index_json = json.load(file_handle)
            asset_jobs = _build_asset_jobs(asset_index_json, game_directory, mirror_source)
            logger.info("Asset jobs prepared: version=%s asset_jobs=%d", version_id, len(asset_jobs))

        all_jobs = [job for job in [asset_index_job, *core_jobs, *asset_jobs] if job]
        logger.info("Download totals: version=%s jobs=%d bytes=%d", version_id, len(all_jobs), sum(job.size for job in all_jobs))
        progress.add_totals(sum(job.size for job in all_jobs), len(all_jobs))

        progress.set_phase("准备下载")
        if asset_index_job:
            progress.set_current_file(asset_index_job.label)
            if asset_index_result == "reused":
                progress.advance_reused(asset_index_job.size or _file_size(asset_index_job.file_path))
            else:
                progress.ready_bytes += asset_index_job.size or _file_size(asset_index_job.file_path)
                progress.emit(force=True)
            progress.finish_file()

        progress.set_phase("下载核心文件")
        await _run_jobs(
            session,
            core_jobs,
            progress,
            max_core_concurrency,
            cache_dirs,
            game_directory,
            mirror_source=mirror_source,
            speed_limit_bps=int(speed_limit_kbps or 0) * 1024,
            cache_strategy=cache_strategy,
        )
        progress.set_phase("下载资源文件")
        await _run_jobs(
            session,
            asset_jobs,
            progress,
            max_asset_concurrency,
            cache_dirs,
            game_directory,
            mirror_source=mirror_source,
            speed_limit_bps=int(speed_limit_kbps or 0) * 1024,
            cache_strategy=cache_strategy,
        )

    progress.set_phase("下载完成")
    progress.emit(force=True)
    logger.info("Game file download finished: version=%s", version_id)


async def repair_game_files(
    version_json,
    game_directory,
    version,
    mirror_source,
    progress_callback=None,
    max_core_concurrency=MAX_CORE_CONCURRENCY,
    max_asset_concurrency=MAX_ASSET_CONCURRENCY,
    speed_limit_kbps=0,
    cache_strategy="reuse",
):
    """校验并补齐当前版本的核心文件、资源索引、资源文件和 natives。"""
    os.makedirs(game_directory, exist_ok=True)
    version_id = version_json.get("id") or version
    logger.info(
        "Starting game file repair: requested=%s resolved=%s game_directory=%s mirror=%s",
        version,
        version_id,
        os.path.abspath(game_directory),
        mirror_source,
    )

    progress = DownloadProgress(progress_callback)
    cache_dirs = _candidate_cache_dirs(game_directory)
    asset_index_job = _build_asset_index_job(version_json, game_directory, mirror_source)
    core_jobs = _build_core_jobs(version_json, game_directory, version_id, mirror_source)
    logger.info("Repair jobs prepared: version=%s core_jobs=%d has_asset_index=%s", version_id, len(core_jobs), bool(asset_index_job))

    async with aiohttp.ClientSession(
        timeout=REQUEST_TIMEOUT,
        connector=aiohttp.TCPConnector(limit=max_asset_concurrency * 2, ttl_dns_cache=300),
    ) as session:
        asset_jobs = []
        if asset_index_job:
            progress.set_phase("校验资源索引", asset_index_job.label)
            progress.add_totals(asset_index_job.size, 1)
            await _process_job(
                session,
                asset_index_job,
                progress,
                asyncio.Semaphore(1),
                cache_dirs,
                game_directory,
                mirror_source=mirror_source,
                speed_limit_bps=int(speed_limit_kbps or 0) * 1024,
                cache_strategy=cache_strategy,
            )

            with open(asset_index_job.file_path, "r", encoding="utf-8") as file_handle:
                asset_index_json = json.load(file_handle)
            asset_jobs = _build_asset_jobs(asset_index_json, game_directory, mirror_source)
            logger.info("Repair asset jobs prepared: version=%s asset_jobs=%d", version_id, len(asset_jobs))

        progress.add_totals(sum(job.size for job in core_jobs) + sum(job.size for job in asset_jobs), len(core_jobs) + len(asset_jobs))

        progress.set_phase("校验核心文件")
        await _run_jobs(
            session,
            core_jobs,
            progress,
            max_core_concurrency,
            cache_dirs,
            game_directory,
            mirror_source=mirror_source,
            speed_limit_bps=int(speed_limit_kbps or 0) * 1024,
            cache_strategy=cache_strategy,
        )
        progress.set_phase("校验资源文件")
        await _run_jobs(
            session,
            asset_jobs,
            progress,
            max_asset_concurrency,
            cache_dirs,
            game_directory,
            mirror_source=mirror_source,
            speed_limit_bps=int(speed_limit_kbps or 0) * 1024,
            cache_strategy=cache_strategy,
        )

    progress.set_phase("补全完成")
    progress.emit(force=True)
    extract_natives(version_json, game_directory, version_id)
    logger.info("Game file repair finished: version=%s", version_id)
