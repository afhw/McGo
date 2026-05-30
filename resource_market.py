import json
import os
import re
import tempfile
import time

import http_client
from file_utils import sanitize_filename, sha1_file, sha1_text
from log_utils import get_logger
from version_utils import (
    mods_directory_for_version,
    resolve_base_minecraft_version,
    runtime_directory_for_version,
    version_type_label,
)


RESOURCE_TYPE_LABELS = {
    "mod": "Mod",
    "resourcepack": "资源包",
    "shader": "光影",
    "datapack": "数据包",
}

RESOURCE_TYPE_FACETS = {
    "mod": "project_type:mod",
    "resourcepack": "project_type:resourcepack",
    "shader": "project_type:shader",
    "datapack": "project_type:datapack",
}

RESOURCE_SOURCE_LABELS = {
    "modrinth": "Modrinth",
    "curseforge": "CurseForge",
    "local": "本地",
}

RESOURCE_SEARCH_SORTS = {
    "相关度": "relevance",
    "下载量": "downloads",
    "收藏数": "follows",
    "最近更新": "updated",
}

CURSEFORGE_CLASS_IDS = {
    "mod": 6,
    "resourcepack": 12,
    "shader": 6552,
    "datapack": 6945,
}

RESOURCE_EXTENSIONS = {
    "mod": (".jar", ".jar.disabled"),
    "resourcepack": (".zip",),
    "shader": (".zip",),
    "datapack": (".zip",),
}

logger = get_logger(__name__)


def _stream_download(url, file_path, progress_callback=None, status_label="下载中"):
    os.makedirs(os.path.dirname(file_path), exist_ok=True)
    start_time = time.monotonic()
    downloaded = 0
    with http_client.get(url, stream=True, timeout=60) as response:
        http_client.raise_for_status(response, status_label)
        total = int(response.headers.get("content-length", 0) or 0)
        with open(file_path, "wb") as file_handle:
            for chunk in response.iter_content(chunk_size=128 * 1024):
                if not chunk:
                    continue
                file_handle.write(chunk)
                downloaded += len(chunk)
                if progress_callback:
                    elapsed = max(0.001, time.monotonic() - start_time)
                    progress_callback({
                        "progress": downloaded / total if total else 0.0,
                        "phase": status_label,
                        "current_file": os.path.basename(file_path),
                        "downloaded_bytes": downloaded,
                        "total_bytes": total,
                        "speed_bytes": downloaded / elapsed,
                        "completed_files": 0,
                        "total_files": 1,
                        "reused_files": 0,
                    })


def modrinth_loader_for_version(game_dir, version_id):
    version_type = version_type_label(game_dir, version_id)
    mapping = {
        "Fabric": "fabric",
        "Forge": "forge",
        "NeoForge": "neoforge",
        "OptiFine": "optifine",
    }
    return mapping.get(version_type, "")


def normalize_minecraft_version_for_api(game_dir, version_id):
    base_version = resolve_base_minecraft_version(game_dir, version_id)
    match = re.search(r"(?<![\d.])(?:1|2|3|4|20|21|22|23|24|25|26|27|28|29|30)(?:\.\d{1,2}){1,2}(?:[-_](?:pre|rc)\d+)?(?![\d.])", str(base_version), flags=re.IGNORECASE)
    if match:
        logger.debug("Normalized Minecraft version for API: version_id=%s base=%s normalized=%s", version_id, base_version, match.group(0))
        return match.group(0)
    snapshot = re.search(r"(?<![0-9a-z])(\d{2}w\d{2}[a-z])(?![0-9a-z])", str(base_version), flags=re.IGNORECASE)
    if snapshot:
        logger.debug("Normalized Minecraft snapshot for API: version_id=%s base=%s normalized=%s", version_id, base_version, snapshot.group(1))
        return snapshot.group(1)
    logger.warning("Could not normalize Minecraft version for API: version_id=%s base=%s", version_id, base_version)
    return base_version


def resource_directory_for_type(game_dir, settings, version_id, resource_type, global_isolation=False):
    runtime_dir = runtime_directory_for_version(game_dir, settings, version_id, global_isolation=global_isolation)
    if resource_type == "mod":
        return mods_directory_for_version(game_dir, settings, version_id, global_isolation=global_isolation)
    if resource_type == "resourcepack":
        return os.path.join(runtime_dir, "resourcepacks")
    if resource_type == "shader":
        return os.path.join(runtime_dir, "shaderpacks")
    if resource_type == "datapack":
        return os.path.join(runtime_dir, "saves")
    return runtime_dir


def search_modrinth_resources(query, resource_type, game_version="", loader="", limit=25, index="relevance"):
    def run_search(current_game_version="", current_loader=""):
        facets = [[RESOURCE_TYPE_FACETS.get(resource_type, "project_type:mod")]]
        if current_game_version:
            facets.append([f"versions:{current_game_version}"])
        if resource_type == "mod" and current_loader:
            facets.append([f"categories:{current_loader}"])
        response = http_client.get(
            "https://api.modrinth.com/v2/search",
            params={
                "query": query,
                "limit": limit,
                "index": index,
                "facets": json.dumps(facets),
            },
            headers={"User-Agent": "McGo/1.0"},
            timeout=30,
        )
        http_client.raise_for_status(response, "搜索 Modrinth 资源")
        result_hits = response.json().get("hits", [])
        logger.info(
            "Modrinth search attempt: query=%s type=%s game=%s loader=%s index=%s hits=%d",
            query,
            resource_type,
            current_game_version or "<any>",
            current_loader or "<any>",
            index,
            len(result_hits),
        )
        return result_hits

    logger.info(
        "Modrinth search started: query=%s type=%s game=%s loader=%s index=%s limit=%d",
        query,
        resource_type,
        game_version or "<any>",
        loader or "<any>",
        index,
        limit,
    )
    attempts = [(game_version, loader)]
    if loader:
        attempts.append((game_version, ""))
    if game_version:
        attempts.append(("", loader))
    attempts.append(("", ""))

    seen_attempts = set()
    hits = []
    relaxed = False
    for current_game_version, current_loader in attempts:
        key = (current_game_version, current_loader)
        if key in seen_attempts:
            continue
        seen_attempts.add(key)
        hits = run_search(current_game_version, current_loader)
        if hits:
            relaxed = key != (game_version, loader)
            break

    for hit in hits:
        hit["source"] = "modrinth"
        hit["relaxed_search"] = relaxed
    logger.info(
        "Modrinth search finished: query=%s type=%s game=%s loader=%s hits=%d relaxed=%s",
        query,
        resource_type,
        game_version or "<any>",
        loader or "<any>",
        len(hits),
        relaxed,
    )
    return hits


def strict_modrinth_search(query, resource_type, game_version="", loader="", limit=25, index="relevance"):
    facets = [[RESOURCE_TYPE_FACETS.get(resource_type, "project_type:mod")]]
    if game_version:
        facets.append([f"versions:{game_version}"])
    if resource_type == "mod" and loader:
        facets.append([f"categories:{loader}"])
    response = http_client.get(
        "https://api.modrinth.com/v2/search",
        params={
            "query": query,
            "limit": limit,
            "index": index,
            "facets": json.dumps(facets),
        },
        headers={"User-Agent": "McGo/1.0"},
        timeout=30,
    )
    http_client.raise_for_status(response, "搜索 Modrinth 资源")
    hits = response.json().get("hits", [])
    for hit in hits:
        hit["source"] = "modrinth"
    return hits


def get_modrinth_versions(project_id, resource_type, game_version, loader):
    params = {"game_versions": json.dumps([game_version])} if game_version else {}
    if resource_type == "mod" and loader:
        params["loaders"] = json.dumps([loader])
    logger.debug(
        "Fetching Modrinth versions: project=%s type=%s game=%s loader=%s",
        project_id,
        resource_type,
        game_version or "<any>",
        loader or "<any>",
    )
    response = http_client.get(
        f"https://api.modrinth.com/v2/project/{project_id}/version",
        params=params,
        headers={"User-Agent": "McGo/1.0"},
        timeout=30,
    )
    http_client.raise_for_status(response, "获取 Modrinth 版本")
    versions = response.json()
    logger.info(
        "Fetched Modrinth versions: project=%s type=%s game=%s loader=%s count=%d",
        project_id,
        resource_type,
        game_version or "<any>",
        loader or "<any>",
        len(versions),
    )
    return versions


def modrinth_version_is_compatible(version, resource_type, game_version="", loader=""):
    game_versions = {str(item).lower() for item in version.get("game_versions", [])}
    loaders = {str(item).lower() for item in version.get("loaders", [])}
    if game_version and str(game_version).lower() not in game_versions:
        return False
    if resource_type == "mod" and loader and str(loader).lower() not in loaders:
        return False
    return True


def find_compatible_modrinth_version(project_id, resource_type, game_version, loader):
    versions = get_modrinth_versions(project_id, resource_type, game_version, loader)
    if not versions and (game_version or loader):
        logger.info(
            "Modrinth exact version query returned no results, falling back to local filtering: project=%s type=%s game=%s loader=%s",
            project_id,
            resource_type,
            game_version or "<any>",
            loader or "<any>",
        )
        try:
            versions = get_modrinth_versions(project_id, resource_type, "", "")
        except Exception:
            logger.exception("Modrinth fallback version query failed: project=%s", project_id)
            versions = []
        versions = [
            item for item in versions
            if modrinth_version_is_compatible(item, resource_type, game_version, loader)
        ]
    preferred = [item for item in versions if item.get("version_type") == "release"]
    selected = (preferred or versions)[0] if versions else None
    if selected:
        logger.info(
            "Selected Modrinth version: project=%s version=%s type=%s game_versions=%s loaders=%s",
            project_id,
            selected.get("version_number", ""),
            selected.get("version_type", ""),
            ",".join(str(item) for item in selected.get("game_versions", [])),
            ",".join(str(item) for item in selected.get("loaders", [])),
        )
    else:
        logger.warning(
            "No compatible Modrinth version selected: project=%s type=%s game=%s loader=%s",
            project_id,
            resource_type,
            game_version or "<any>",
            loader or "<any>",
        )
    return selected


def hit_has_modrinth_compatibility(hit, resource_type, game_version="", loader=""):
    if game_version:
        versions = {str(item).lower() for item in hit.get("versions", [])}
        if versions and str(game_version).lower() not in versions:
            return False
    if resource_type == "mod" and loader:
        categories = {str(item).lower() for item in hit.get("categories", [])}
        display_categories = {str(item).lower() for item in hit.get("display_categories", [])}
        all_categories = categories | display_categories
        if all_categories and str(loader).lower() not in all_categories:
            return False
    return True


def select_modrinth_version(project_id, resource_type, game_version, loader):
    selected = find_compatible_modrinth_version(project_id, resource_type, game_version, loader)
    if not selected:
        target = f"Minecraft {game_version}" if game_version else "当前 Minecraft 版本"
        if resource_type == "mod" and loader:
            target += f" / {loader}"
        raise RuntimeError(f"这个资源没有适配 {target} 的可安装文件。")
    return selected


def install_modrinth_resource(project_id, resource_type, game_version, loader, target_dir, install_dependencies=False, installed=None, status_callback=None, progress_callback=None):
    installed = installed if installed is not None else set()
    project_key = f"modrinth:{project_id}"
    if project_key in installed:
        logger.debug("Skipping already installed Modrinth dependency: project=%s", project_id)
        return {"filename": "", "path": "", "version": "", "dependencies_installed": []}
    installed.add(project_key)

    logger.info(
        "Installing Modrinth resource: project=%s type=%s game=%s loader=%s target=%s dependencies=%s",
        project_id,
        resource_type,
        game_version or "<any>",
        loader or "<any>",
        os.path.abspath(target_dir),
        install_dependencies,
    )
    selected = select_modrinth_version(project_id, resource_type, game_version, loader)
    dependencies_installed = []
    if install_dependencies:
        for dependency in selected.get("dependencies", []):
            if dependency.get("dependency_type") != "required":
                continue
            dep_project_id = dependency.get("project_id")
            if not dep_project_id:
                continue
            logger.info(
                "Installing required Modrinth dependency: parent=%s dependency=%s",
                project_id,
                dep_project_id,
            )
            if status_callback:
                status_callback(f"正在安装依赖：{dep_project_id}")
            dep_payload = install_modrinth_resource(
                dep_project_id,
                resource_type,
                game_version,
                loader,
                target_dir,
                install_dependencies=True,
                installed=installed,
                status_callback=status_callback,
                progress_callback=progress_callback,
            )
            if dep_payload.get("filename"):
                dependencies_installed.append(dep_payload.get("filename", dep_project_id))
            dependencies_installed.extend(dep_payload.get("dependencies_installed", []))

    files = selected.get("files", [])
    primary = next((item for item in files if item.get("primary")), None) or (files[0] if files else None)
    if not primary or not primary.get("url"):
        logger.error(
            "Selected Modrinth version has no downloadable file: project=%s version=%s",
            project_id,
            selected.get("version_number", ""),
        )
        raise RuntimeError("资源版本缺少可下载文件。")

    filename = sanitize_filename(primary.get("filename") or f"{project_id}.jar", f"{project_id}.jar")
    os.makedirs(target_dir, exist_ok=True)
    target_path = os.path.join(target_dir, filename)
    logger.info(
        "Downloading Modrinth resource file: project=%s version=%s filename=%s size=%s target=%s",
        project_id,
        selected.get("version_number", ""),
        filename,
        primary.get("size", 0),
        os.path.abspath(target_path),
    )
    _stream_download(primary["url"], target_path, progress_callback, "下载资源")
    expected_hash = primary.get("hashes", {}).get("sha1", "")
    if expected_hash and sha1_file(target_path).lower() != expected_hash.lower():
        logger.error("Modrinth resource SHA1 mismatch: project=%s target=%s", project_id, os.path.abspath(target_path))
        raise RuntimeError("资源文件 SHA1 校验失败。")
    logger.info(
        "Modrinth resource installed: project=%s version=%s filename=%s dependencies=%d",
        project_id,
        selected.get("version_number", ""),
        filename,
        len(dependencies_installed),
    )
    return {
        "filename": filename,
        "path": target_path,
        "version": selected.get("version_number", ""),
        "dependencies_installed": dependencies_installed,
    }


def get_modrinth_resource_detail(project_id, resource_type, game_version, loader):
    project_response = http_client.get(
        f"https://api.modrinth.com/v2/project/{project_id}",
        headers={"User-Agent": "McGo/1.0"},
        timeout=30,
    )
    http_client.raise_for_status(project_response, "获取 Modrinth 项目详情")
    project = project_response.json()
    versions = get_modrinth_versions(project_id, resource_type, game_version, loader)
    selected = (versions or [{}])[0]
    screenshots = project.get("gallery", [])[:5]
    dependencies = [
        item for item in selected.get("dependencies", [])
        if item.get("dependency_type") in {"required", "optional"}
    ]
    return {
        "source": "modrinth",
        "title": project.get("title", project_id),
        "description": project.get("description", ""),
        "body": project.get("body", ""),
        "downloads": project.get("downloads", 0),
        "followers": project.get("followers", 0),
        "project_url": f"https://modrinth.com/{project.get('project_type', 'mod')}/{project.get('slug', project_id)}",
        "screenshots": [item.get("url", "") for item in screenshots if item.get("url")],
        "dependencies": dependencies,
        "versions": versions[:8],
    }


def curseforge_headers():
    api_key = os.environ.get("CURSEFORGE_API_KEY", "").strip()
    if not api_key:
        raise RuntimeError("CurseForge 官方 API 需要 CURSEFORGE_API_KEY 环境变量。")
    return {"Accept": "application/json", "x-api-key": api_key, "User-Agent": "McGo/1.0"}


def search_curseforge_resources(query, resource_type, game_version="", loader="", limit=25):
    params = {
        "gameId": 432,
        "searchFilter": query,
        "pageSize": limit,
        "sortField": 2,
        "sortOrder": "desc",
    }
    class_id = CURSEFORGE_CLASS_IDS.get(resource_type)
    if class_id:
        params["classId"] = class_id
    if game_version:
        params["gameVersion"] = game_version
    response = http_client.get(
        "https://api.curseforge.com/v1/mods/search",
        params=params,
        headers=curseforge_headers(),
        timeout=30,
    )
    http_client.raise_for_status(response, "搜索 CurseForge 资源")
    hits = []
    for item in response.json().get("data", []):
        links = item.get("links", {})
        logo = item.get("logo") or {}
        hits.append({
            "source": "curseforge",
            "project_id": item.get("id"),
            "title": item.get("name", ""),
            "slug": item.get("slug", ""),
            "description": item.get("summary", ""),
            "downloads": item.get("downloadCount", 0),
            "follows": item.get("thumbsUpCount", 0),
            "project_url": links.get("websiteUrl", ""),
            "icon_url": logo.get("thumbnailUrl", ""),
        })
    return hits


def get_curseforge_resource_detail(project_id):
    response = http_client.get(
        f"https://api.curseforge.com/v1/mods/{project_id}",
        headers=curseforge_headers(),
        timeout=30,
    )
    http_client.raise_for_status(response, "获取 CurseForge 项目详情")
    project = response.json().get("data", {})
    files_response = http_client.get(
        f"https://api.curseforge.com/v1/mods/{project_id}/files",
        headers=curseforge_headers(),
        timeout=30,
    )
    http_client.raise_for_status(files_response, "获取 CurseForge 文件列表")
    files = files_response.json().get("data", [])[:8]
    links = project.get("links", {})
    screenshots = [
        screenshot.get("url", "")
        for screenshot in project.get("screenshots", [])[:5]
        if screenshot.get("url")
    ]
    return {
        "source": "curseforge",
        "title": project.get("name", str(project_id)),
        "description": project.get("summary", ""),
        "body": "",
        "downloads": project.get("downloadCount", 0),
        "followers": project.get("thumbsUpCount", 0),
        "project_url": links.get("websiteUrl", ""),
        "screenshots": screenshots,
        "dependencies": [],
        "versions": files,
    }


def list_local_resources(query, resource_type, target_dir):
    lowered_query = query.lower()
    extensions = RESOURCE_EXTENSIONS.get(resource_type, (".jar", ".zip"))
    hits = []
    if not os.path.isdir(target_dir):
        return hits
    for current_root, _, files in os.walk(target_dir):
        for filename in files:
            lowered = filename.lower()
            if not lowered.endswith(extensions):
                continue
            if lowered_query and lowered_query not in lowered:
                continue
            path = os.path.join(current_root, filename)
            try:
                stat = os.stat(path)
            except OSError:
                continue
            hits.append({
                "source": "local",
                "project_id": path,
                "title": filename,
                "slug": filename,
                "description": os.path.relpath(path, target_dir),
                "downloads": stat.st_size,
                "follows": 0,
                "path": path,
                "updated": stat.st_mtime,
            })
    hits.sort(key=lambda item: item.get("updated", 0), reverse=True)
    return hits[:100]


def analyze_local_mod_file(path, mods_dir):
    basename = os.path.basename(path)
    lowered = basename.lower()
    status = "禁用" if lowered.endswith(".jar.disabled") else "启用"
    hints = []
    if lowered.endswith(".jar.disabled"):
        hints.append("当前不会被加载")
    dependency_markers = {
        "fabric-api": "Fabric API",
        "architectury": "Architectury API",
        "cloth-config": "Cloth Config",
        "modmenu": "Mod Menu",
        "geckolib": "GeckoLib",
        "balm": "Balm",
        "moonlight": "Moonlight Lib",
    }
    available = set()
    if os.path.isdir(mods_dir):
        available = {name.lower().replace(".disabled", "") for name in os.listdir(mods_dir)}
    for marker, label in dependency_markers.items():
        if marker in lowered:
            continue
        if any(marker in name for name in available):
            continue
        if marker in {"fabric-api"} and ("fabric" in lowered or basename.endswith(".jar")):
            hints.append(f"可能需要 {label}")
            break
    return status, hints


def get_local_resource_detail(path):
    stat = os.stat(path)
    status, hints = analyze_local_mod_file(path, os.path.dirname(path))
    return {
        "source": "local",
        "title": os.path.basename(path),
        "description": os.path.abspath(path),
        "body": "",
        "downloads": stat.st_size,
        "followers": 0,
        "project_url": os.path.abspath(os.path.dirname(path)),
        "screenshots": [],
        "dependencies": [{"dependency_type": "hint", "project_id": hint} for hint in hints],
        "versions": [],
        "status": status,
    }


def cache_resource_screenshot(url):
    if not url:
        return ""
    cache_dir = os.path.join(tempfile.gettempdir(), "mcgo-resource-screenshots")
    os.makedirs(cache_dir, exist_ok=True)
    extension = os.path.splitext(url.split("?", 1)[0])[1].lower()
    if extension not in {".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"}:
        extension = ".jpg"
    safe_name = sanitize_filename(sha1_text(url), "screenshot") + extension
    target_path = os.path.join(cache_dir, safe_name)
    if os.path.isfile(target_path) and os.path.getsize(target_path) > 0:
        return target_path
    response = http_client.get(url, headers={"User-Agent": "McGo/1.0"}, timeout=30)
    http_client.raise_for_status(response, "下载资源截图")
    with open(target_path, "wb") as file_handle:
        file_handle.write(response.content)
    return target_path
