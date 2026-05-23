# downloader.py
import asyncio
import json
import os
import shutil
import time
import zipfile
from dataclasses import dataclass

import aiohttp

CHUNK_SIZE = 128 * 1024
MAX_CORE_CONCURRENCY = 12
MAX_ASSET_CONCURRENCY = 24
REQUEST_TIMEOUT = aiohttp.ClientTimeout(total=None, sock_connect=20, sock_read=60)


@dataclass
class DownloadJob:
    url: str
    file_path: str
    relative_path: str
    size: int = 0
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


def _matches_size(path, expected_size):
    if not os.path.exists(path):
        return False
    if expected_size and _file_size(path) != expected_size:
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
        if not _matches_size(source_path, job.size):
            continue

        _ensure_parent(job.file_path)
        shutil.copy2(source_path, job.file_path)
        return True

    return False


def _rewrite_url(url, mirror_source):
    if mirror_source != "https://bmclapi2.bangbang93.com":
        return url

    replacements = {
        "https://piston-data.mojang.com": mirror_source,
        "https://launcher.mojang.com": mirror_source,
        "https://libraries.minecraft.net": f"{mirror_source}/maven",
        "https://resources.download.minecraft.net": f"{mirror_source}/assets",
    }
    for source, target in replacements.items():
        if url.startswith(source):
            return url.replace(source, target, 1)
    return url


async def _download_with_retries(session, job, progress, semaphore, retries=5):
    for attempt in range(retries):
        try:
            async with semaphore:
                await _download_single(session, job, progress)
            return
        except Exception:
            if attempt == retries - 1:
                raise
            await asyncio.sleep(min(2 ** attempt, 5))


async def _download_single(session, job, progress):
    _ensure_parent(job.file_path)
    progress.set_current_file(job.label)

    async with session.get(job.url) as response:
        response.raise_for_status()
        with open(job.file_path, "wb") as file_handle:
            async for chunk in response.content.iter_chunked(CHUNK_SIZE):
                if not chunk:
                    continue
                file_handle.write(chunk)
                progress.advance_network(len(chunk))


async def _process_job(session, job, progress, semaphore, cache_dirs, target_game_dir):
    if _matches_size(job.file_path, job.size):
        progress.set_current_file(f"{job.label}（已存在）")
        progress.advance_reused(job.size or _file_size(job.file_path))
        progress.finish_file()
        return "reused"

    if _try_copy_from_cache(job, cache_dirs, target_game_dir):
        progress.set_current_file(f"{job.label}（本地复用）")
        progress.advance_reused(job.size or _file_size(job.file_path))
        progress.finish_file()
        return "reused"

    await _download_with_retries(session, job, progress, semaphore)
    progress.finish_file()
    return "downloaded"


async def _run_jobs(session, jobs, progress, concurrency, cache_dirs, target_game_dir):
    if not jobs:
        return
    semaphore = asyncio.Semaphore(concurrency)
    await asyncio.gather(*[
        _process_job(session, job, progress, semaphore, cache_dirs, target_game_dir)
        for job in jobs
    ])


def _build_core_jobs(version_json, game_directory, version_id, mirror_source):
    jobs = []

    client_info = version_json.get("downloads", {}).get("client", {})
    client_relative = os.path.join("versions", version_id, f"{version_id}.jar")
    jobs.append(DownloadJob(
        url=_rewrite_url(client_info["url"], mirror_source),
        file_path=os.path.join(game_directory, client_relative),
        relative_path=client_relative,
        size=client_info.get("size", 0),
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
                label=os.path.basename(artifact["path"]),
            ))

        natives = downloads.get("classifiers", {}).get("natives-windows")
        if natives:
            jobs.append(DownloadJob(
                url=_rewrite_url(natives["url"], mirror_source),
                file_path=os.path.join(game_directory, "libraries", natives["path"]),
                relative_path=os.path.join("libraries", natives["path"]),
                size=natives.get("size", 0),
                label=os.path.basename(natives["path"]),
            ))
    return jobs


def _build_asset_index_job(version_json, game_directory, mirror_source):
    asset_index = version_json.get("assetIndex", {})
    asset_index_relative = os.path.join("assets", "indexes", f"{asset_index['id']}.json")
    return DownloadJob(
        url=_rewrite_url(asset_index["url"], mirror_source),
        file_path=os.path.join(game_directory, asset_index_relative),
        relative_path=asset_index_relative,
        size=asset_index.get("size", 0),
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
            label=object_hash,
        ))
    return jobs


async def download_assets(version_json, game_directory, version_id, mirror_source, progress_callback=None):
    progress = DownloadProgress(progress_callback)
    cache_dirs = _candidate_cache_dirs(game_directory)
    asset_index_path = os.path.join(
        game_directory, "assets", "indexes", f"{version_json['assetIndex']['id']}.json"
    )

    async with aiohttp.ClientSession(
        timeout=REQUEST_TIMEOUT,
        connector=aiohttp.TCPConnector(limit=MAX_ASSET_CONCURRENCY * 2, ttl_dns_cache=300),
    ) as session:
        with open(asset_index_path, "r", encoding="utf-8") as file_handle:
            asset_index_json = json.load(file_handle)
        jobs = _build_asset_jobs(asset_index_json, game_directory, mirror_source)
        progress.set_phase("下载资源文件")
        progress.add_totals(sum(job.size for job in jobs), len(jobs))
        await _run_jobs(session, jobs, progress, MAX_ASSET_CONCURRENCY, cache_dirs, game_directory)
        progress.set_phase("资源文件下载完成")
        progress.emit(force=True)


def extract_natives(version_json, game_directory, version_id):
    natives_directory = os.path.join(
        game_directory, "versions", version_id, f"{version_id}-natives"
    )
    os.makedirs(natives_directory, exist_ok=True)

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
                        print(f"警告: 跳过可疑路径 {file_info.filename}")
                        continue

                    zip_ref.extract(file_info, natives_directory)
        except FileNotFoundError:
            print(f"警告: natives 文件不存在: {library_path}")
        except Exception as exc:
            print(f"解压 natives 文件时出错：{exc}")

    file_count = 0
    for _, _, files in os.walk(natives_directory):
        file_count += len(files)
    print(f"已解压 {file_count} 个 natives 文件到 {natives_directory}")


async def download_game_files(version_json, game_directory, version, mirror_source, progress_callback=None):
    os.makedirs(game_directory, exist_ok=True)

    version_id = version_json["id"]
    version_json_relative = os.path.join("versions", version_id, f"{version_id}.json")
    version_json_path = os.path.join(game_directory, version_json_relative)
    _ensure_parent(version_json_path)
    with open(version_json_path, "w", encoding="utf-8") as file_handle:
        json.dump(version_json, file_handle, ensure_ascii=False, indent=4)

    progress = DownloadProgress(progress_callback)
    cache_dirs = _candidate_cache_dirs(game_directory)
    asset_index_job = _build_asset_index_job(version_json, game_directory, mirror_source)
    core_jobs = _build_core_jobs(version_json, game_directory, version_id, mirror_source)

    async with aiohttp.ClientSession(
        timeout=REQUEST_TIMEOUT,
        connector=aiohttp.TCPConnector(limit=MAX_ASSET_CONCURRENCY * 2, ttl_dns_cache=300),
    ) as session:
        asset_index_result = await _process_job(
            session,
            asset_index_job,
            DownloadProgress(),
            asyncio.Semaphore(1),
            cache_dirs,
            game_directory,
        )

        asset_index_path = os.path.join(
            game_directory, "assets", "indexes", f"{version_json['assetIndex']['id']}.json"
        )
        with open(asset_index_path, "r", encoding="utf-8") as file_handle:
            asset_index_json = json.load(file_handle)

        asset_jobs = _build_asset_jobs(asset_index_json, game_directory, mirror_source)
        all_jobs = [asset_index_job, *core_jobs, *asset_jobs]
        progress.add_totals(sum(job.size for job in all_jobs), len(all_jobs))

        progress.set_phase("准备下载")
        progress.set_current_file(asset_index_job.label)
        if asset_index_result == "reused":
            progress.advance_reused(asset_index_job.size or _file_size(asset_index_job.file_path))
        else:
            progress.ready_bytes += asset_index_job.size or _file_size(asset_index_job.file_path)
            progress.emit(force=True)
        progress.finish_file()

        progress.set_phase("下载核心文件")
        await _run_jobs(session, core_jobs, progress, MAX_CORE_CONCURRENCY, cache_dirs, game_directory)
        progress.set_phase("下载资源文件")
        await _run_jobs(session, asset_jobs, progress, MAX_ASSET_CONCURRENCY, cache_dirs, game_directory)

    progress.set_phase("下载完成")
    progress.emit(force=True)
