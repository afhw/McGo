import json
import os
import re
import time
import zipfile
from io import BytesIO

import http_client
from log_utils import get_logger


MIRROR_SOURCES = {
    "official": "https://launchermeta.mojang.com",
    "bmclapi": "https://bmclapi2.bangbang93.com",
}

INSTALL_TYPE_LABELS = {
    "fabric": "Fabric",
    "forge": "Forge",
    "neoforge": "NeoForge",
    "optifine": "OptiFine",
    "fabric_api": "Fabric API",
}

logger = get_logger(__name__)


def fallback_mirror_source(mirror_source):
    return "bmclapi" if mirror_source == "official" else "official"


def mirror_source_sequence(mirror_source):
    primary = mirror_source if mirror_source in MIRROR_SOURCES else "official"
    fallback = fallback_mirror_source(primary)
    return [primary, fallback] if fallback != primary else [primary]


def rewrite_download_url_for_mirror(url, mirror_source):
    mirror = MIRROR_SOURCES.get(mirror_source, MIRROR_SOURCES["official"])
    if mirror_source == "bmclapi":
        replacements = {
            "https://piston-data.mojang.com": mirror,
            "https://launcher.mojang.com": mirror,
            "https://libraries.minecraft.net": f"{mirror}/maven",
            "https://resources.download.minecraft.net": f"{mirror}/assets",
            "https://launchermeta.mojang.com": mirror,
        }
        for source, target in replacements.items():
            if url.startswith(source):
                return url.replace(source, target, 1)
    elif mirror_source == "official":
        replacements = {
            f"{MIRROR_SOURCES['bmclapi']}/maven": "https://libraries.minecraft.net",
            f"{MIRROR_SOURCES['bmclapi']}/assets": "https://resources.download.minecraft.net",
            MIRROR_SOURCES["bmclapi"]: "https://piston-data.mojang.com",
        }
        for source, target in replacements.items():
            if url.startswith(source):
                return url.replace(source, target, 1)
    return url


def get_remote_versions(version_type, mirror_source):
    logger.info("Fetching remote versions: type=%s mirror=%s", version_type, mirror_source)
    response = http_client.get(f"{MIRROR_SOURCES[mirror_source]}/mc/game/version_manifest.json", timeout=30)
    http_client.raise_for_status(response, "获取远程版本列表")
    versions = [v["id"] for v in response.json()["versions"] if v["type"] == version_type]
    logger.info("Fetched %d remote versions: type=%s mirror=%s", len(versions), version_type, mirror_source)
    return versions


def get_version_url(version_id, mirror_source):
    logger.debug("Resolving version metadata URL: version=%s mirror=%s", version_id, mirror_source)
    response = http_client.get(f"{MIRROR_SOURCES[mirror_source]}/mc/game/version_manifest.json", timeout=30)
    http_client.raise_for_status(response, "获取版本清单")
    for version in response.json()["versions"]:
        if version["id"] == version_id:
            logger.debug("Resolved version metadata URL: version=%s url=%s", version_id, version["url"])
            return version["url"]
    logger.warning("Version metadata URL not found: version=%s mirror=%s", version_id, mirror_source)
    return None


def get_version_metadata_with_fallback(version_id, mirror_source, status_callback=None):
    errors = []
    for source in mirror_source_sequence(mirror_source):
        try:
            if status_callback and source != mirror_source:
                status_callback(f"主下载源失败，正在切换到 {source} 获取版本信息...")
            version_url = get_version_url(version_id, source)
            if not version_url:
                raise RuntimeError(f"无法获取版本 {version_id} 的下载地址")
            version_url = rewrite_download_url_for_mirror(version_url, source)
            response = http_client.get(version_url, timeout=30)
            http_client.raise_for_status(response, "获取版本元数据")
            return response.json(), source
        except Exception as exc:
            logger.warning("Version metadata fetch failed: version=%s mirror=%s error=%s", version_id, source, exc)
            errors.append(f"{source}: {exc}")
    raise RuntimeError("；".join(errors) or f"无法获取版本 {version_id} 的下载地址")


def mirror_root(mirror_source):
    return MIRROR_SOURCES.get(mirror_source, MIRROR_SOURCES["official"])


def stream_download(url, file_path, progress_callback=None, status_label="下载中", mirror_source=None):
    os.makedirs(os.path.dirname(file_path), exist_ok=True)
    logger.info("Starting stream download: label=%s target=%s url=%s", status_label, os.path.abspath(file_path), url)
    downloaded = 0
    try:
        response = None
        last_error = None
        for source in mirror_source_sequence(mirror_source or "official"):
            candidate_url = rewrite_download_url_for_mirror(url, source)
            try:
                response = http_client.get(candidate_url, stream=True, timeout=60)
                http_client.raise_for_status(response, "下载文件")
                url = candidate_url
                break
            except Exception as exc:
                if response is not None:
                    response.close()
                response = None
                last_error = exc
                logger.warning("Stream download source failed: label=%s mirror=%s url=%s error=%s", status_label, source, candidate_url, exc)
        if response is None:
            raise last_error or RuntimeError(f"下载失败：{url}")
        with response:
            total = int(response.headers.get("content-length") or 0)
            logger.debug(
                "Stream download response: label=%s status=%s total_bytes=%d target=%s",
                status_label,
                response.status_code,
                total,
                os.path.abspath(file_path),
            )
            last_tick = time.monotonic()
            last_bytes = 0
            with open(file_path, "wb") as file_handle:
                for chunk in response.iter_content(chunk_size=128 * 1024):
                    if not chunk:
                        continue
                    file_handle.write(chunk)
                    downloaded += len(chunk)
                    now = time.monotonic()
                    if progress_callback and total:
                        speed = 0
                        elapsed = max(now - last_tick, 1e-6)
                        if downloaded > last_bytes:
                            speed = max(0, int((downloaded - last_bytes) / elapsed))
                            last_bytes = downloaded
                            last_tick = now
                        progress_callback({
                            "progress": min(1.0, downloaded / total),
                            "phase": status_label,
                            "current_file": os.path.basename(file_path),
                            "downloaded_bytes": downloaded,
                            "total_bytes": total,
                            "speed_bytes": speed,
                            "completed_files": 0,
                            "total_files": 1,
                            "reused_files": 0,
                        })
            if progress_callback and total:
                progress_callback({
                    "progress": 1.0,
                    "phase": status_label,
                    "current_file": os.path.basename(file_path),
                    "downloaded_bytes": downloaded,
                    "total_bytes": total,
                    "speed_bytes": 0,
                    "completed_files": 1,
                    "total_files": 1,
                    "reused_files": 0,
                })
        logger.info("Stream download finished: label=%s bytes=%d target=%s", status_label, downloaded, os.path.abspath(file_path))
    except Exception:
        logger.exception("Stream download failed: label=%s bytes=%d target=%s url=%s", status_label, downloaded, os.path.abspath(file_path), url)
        raise


def extract_zip_bytes(zip_bytes, target_dir):
    with zipfile.ZipFile(BytesIO(zip_bytes)) as zip_file:
        zip_file.extractall(target_dir)


def version_key(version):
    parts = re.split(r"[.\-+_]", str(version))
    key = []
    for part in parts:
        if part.isdigit():
            key.append((0, int(part)))
        else:
            key.append((1, part))
    return key


def library_relative_path(library_name):
    parts = library_name.split(":")
    if len(parts) != 3:
        return None
    group, artifact, version = parts
    return os.path.join(*group.split("."), artifact, version, f"{artifact}-{version}.jar")


def mirror_library_base(library_url, mirror_source):
    if mirror_source == "bmclapi":
        if "maven.fabricmc.net" in library_url:
            return f"{mirror_root(mirror_source)}/maven"
        if "maven.neoforged.net" in library_url:
            return f"{mirror_root(mirror_source)}/maven"
        if "libraries.minecraft.net" in library_url:
            return f"{mirror_root(mirror_source)}/maven"
        if "maven.minecraftforge.net" in library_url:
            return f"{mirror_root(mirror_source)}/maven"
    return library_url.rstrip("/")


def download_profile_libraries(profile_json, game_dir, mirror_source, progress_callback=None):
    libraries = profile_json.get("libraries", [])
    if not libraries:
        return

    total_files = len(libraries)
    completed_files = 0
    for library in libraries:
        name = library.get("name", "")
        relative_path = library_relative_path(name)
        if not relative_path:
            continue
        url_base = mirror_library_base(library.get("url", "https://libraries.minecraft.net/"), mirror_source)
        artifact_url = f"{url_base}/{relative_path.replace(os.sep, '/')}"
        target_path = os.path.join(game_dir, "libraries", relative_path)
        if os.path.exists(target_path):
            completed_files += 1
            if progress_callback:
                progress_callback({
                    "progress": min(1.0, completed_files / max(total_files, 1)),
                    "phase": "复用安装依赖",
                    "current_file": os.path.basename(target_path),
                    "downloaded_bytes": completed_files,
                    "total_bytes": total_files,
                    "speed_bytes": 0,
                    "completed_files": completed_files,
                    "total_files": total_files,
                    "reused_files": completed_files,
                })
            continue
        stream_download(artifact_url, target_path, progress_callback, "下载安装依赖")
        completed_files += 1


def get_fabric_loader_versions(game_version, mirror_source):
    base = mirror_root(mirror_source)
    response = http_client.get(f"{base}/fabric-meta/v2/versions/loader/{game_version}", timeout=30)
    http_client.raise_for_status(response, "获取 Fabric Loader 列表")
    return response.json()


def get_forge_promos(mirror_source):
    if mirror_source == "bmclapi":
        url = f"{mirror_root(mirror_source)}/forge/promos"
    else:
        url = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json"
    response = http_client.get(url, timeout=30)
    http_client.raise_for_status(response, "获取 Forge 版本列表")
    data = response.json()
    if isinstance(data, dict) and "promos" in data:
        return data["promos"]
    return data


def get_forge_version_for_mc(game_version, mirror_source):
    promos = get_forge_promos(mirror_source)
    if isinstance(promos, list):
        preferred = []
        fallback = []
        for item in promos:
            build = item.get("build")
            if not isinstance(build, dict):
                continue
            if build.get("mcversion") != game_version:
                continue
            version = build.get("version")
            if not version:
                continue
            if "recommended" in item.get("name", ""):
                preferred.append(version)
            else:
                fallback.append(version)
        if preferred:
            return sorted(preferred, key=version_key, reverse=True)[0]
        if fallback:
            return sorted(fallback, key=version_key, reverse=True)[0]
        return None

    candidates = [
        promos.get(f"{game_version}-recommended"),
        promos.get(f"{game_version}-latest"),
    ]
    for candidate in candidates:
        if candidate:
            return candidate
    return None


def get_neoforge_list(game_version, mirror_source):
    if mirror_source == "bmclapi":
        url = f"{mirror_root(mirror_source)}/neoforge/list/{game_version}"
        response = http_client.get(url, timeout=30)
        http_client.raise_for_status(response, "获取 NeoForge 版本列表")
        return response.json()
    raise RuntimeError("NeoForge 安装当前仅支持 BMCLAPI 镜像。")


def get_optifine_list(mirror_source):
    if mirror_source == "bmclapi":
        url = f"{mirror_root(mirror_source)}/optifine/versionList"
        response = http_client.get(url, timeout=30)
        http_client.raise_for_status(response, "获取 OptiFine 版本列表")
        return response.json()
    raise RuntimeError("OptiFine 安装当前仅支持 BMCLAPI 镜像。")


def get_fabric_api_versions(game_version):
    response = http_client.get(
        "https://api.modrinth.com/v2/project/fabric-api/version",
        params={
            "loaders": json.dumps(["fabric"]),
            "game_versions": json.dumps([game_version]),
        },
        timeout=30,
    )
    http_client.raise_for_status(response, "获取 Fabric API 版本列表")
    data = response.json()
    if isinstance(data, dict) and "value" in data:
        data = data["value"]
    return data or []


def build_optifine_version_id(minecraft_version, selected):
    filename = (selected.get("filename") or "").strip()
    if filename:
        name = filename
        if name.lower().endswith(".jar"):
            name = name[:-4]
        if name.startswith("preview_"):
            name = name[len("preview_"):]
        prefix = f"OptiFine_{minecraft_version}_"
        if name.startswith(prefix):
            return f"{minecraft_version}-OptiFine_{name[len(prefix):]}"

    optifine_type = (selected.get("type") or "HD_U").strip()
    patch = (selected.get("patch") or "").strip()
    suffix = optifine_type if not patch else f"{optifine_type}_{patch}"
    return f"{minecraft_version}-OptiFine_{suffix}"


def build_optifine_library_version(selected):
    filename = (selected.get("filename") or "").strip()
    if filename:
        name = filename[:-4] if filename.lower().endswith(".jar") else filename
        if name.startswith("preview_"):
            name = name[len("preview_"):]
        prefix = "OptiFine_"
        if name.startswith(prefix):
            return name[len(prefix):]
        return name

    minecraft_version = (selected.get("mcversion") or "").strip()
    optifine_type = (selected.get("type") or "HD_U").strip()
    patch = (selected.get("patch") or "").strip()
    return f"{minecraft_version}_{optifine_type}" if not patch else f"{minecraft_version}_{optifine_type}_{patch}"


def select_optifine_candidate(candidates):
    stable = [item for item in candidates if not str(item.get("filename", "")).startswith("preview_")]
    ordered = stable or candidates
    return ordered[0] if ordered else None
