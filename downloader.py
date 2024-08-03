# downloader.py
import os
import json
import asyncio
import aiohttp
import zipfile
import shutil

global_library_path = ""


async def download_file(url, file_path, progress_callback=None, semaphore=None, retries=5):
    """异步下载文件到指定路径，并支持进度回调、信号量控制和重试机制."""
    for attempt in range(retries):
        try:
            if semaphore:
                async with semaphore:  # 限制并发下载数量
                    await _download_file(url, file_path, progress_callback)
            else:
                await _download_file(url, file_path, progress_callback)
            return  # 下载成功，退出循环
        except Exception as e:
            print(f"下载文件时出错: {e}，正在进行第 {attempt + 1} 次重试...")
            await asyncio.sleep(1)  # 等待 1 秒后重试

    raise Exception(f"下载文件失败: {url}")  # 超过最大重试次数，抛出异常


async def _download_file(url, file_path, progress_callback=None):
    """异步下载文件的实际逻辑"""
    if not os.path.exists(os.path.dirname(file_path)):
        os.makedirs(os.path.dirname(file_path))

    async with aiohttp.ClientSession() as session:
        async with session.get(url) as response:
            response.raise_for_status()
            total_size = int(response.headers.get("content-length", 0))
            downloaded_size = 0
            with open(file_path, "wb") as f:
                async for chunk in response.content.iter_chunked(8192):
                    f.write(chunk)
                    downloaded_size += len(chunk)
                    if progress_callback:
                        try:
                            if total_size == 0:  # 防止除以零
                                total_size = 0.01
                            progress_callback(downloaded_size / total_size)
                        finally:
                            pass


async def download_assets(version_json, game_directory, version_id, MIRROR_SOURCES, progress_callback=None):
    # from main import mirror_source,
    """下载资源索引文件和资源文件到游戏版本目录"""
    asset_index_url = version_json["assetIndex"]["url"]
    asset_index_path = os.path.join(
        game_directory, 'versions', version_id, "assets", "indexes",
        f"{version_json['assetIndex']['id']}.json"
    ).replace('/', os.path.sep)
    print(asset_index_url)
    # 下载资源索引文件
    await download_file(asset_index_url, asset_index_path, progress_callback)

    # 解析资源索引文件
    with open(asset_index_path, "r") as f:
        asset_index_json = json.load(f)

    # 创建下载任务列表
    tasks = []
    # 限制并发下载数量为32
    semaphore = asyncio.Semaphore(32)
    for i, (object_name, object_info) in enumerate(asset_index_json["objects"].items()):
        object_hash = object_info["hash"]
        # object_url = f"https://resources.download.minecraft.net/{object_hash[:2]}/{object_hash}"
        object_url = f"{MIRROR_SOURCES[mirror_source]}/resources/{object_hash[:2]}/{object_hash}"
        print(f"{MIRROR_SOURCES[mirror_source]}/resources/{object_hash[:2]}/{object_hash}")
        object_path = os.path.join(
            game_directory, 'versions', version_id, "assets", "objects", object_hash[:2], object_hash
        ).replace('/', os.path.sep)
        # 将下载任务添加到列表中
        tasks.append(download_file(object_url, object_path, progress_callback, semaphore))

    # 并发执行所有下载任务
    await asyncio.gather(*tasks)


def extract_natives(version_json, game_directory, version_id):
    """解压 natives 文件"""
    natives_directory = os.path.join(
        game_directory, 'versions', version_id, f"{version_id}-natives"
    ).replace('/', os.path.sep)
    os.makedirs(natives_directory, exist_ok=True)
    for library in version_json["libraries"]:
        if (
                "downloads" in library
                and "classifiers" in library["downloads"]
                and "natives-windows" in library["downloads"]["classifiers"]
        ):
            library_path = os.path.join(
                game_directory,
                'libraries',
                library["downloads"]["classifiers"]["natives-windows"]["path"]
            ).replace('/', os.path.sep)
            # 使用 zipfile 解压缩 jar 文件
            try:
                with zipfile.ZipFile(library_path, 'r') as zip_ref:
                    for file_info in zip_ref.infolist():
                        # 排除 META-INF 文件夹和目录
                        if not file_info.filename.startswith('META-INF') and not file_info.filename.endswith('/'):
                            zip_ref.extract(file_info, natives_directory)
            except FileNotFoundError:
                print(f"警告: natives 文件不存在: {library_path}")
            except Exception as e:
                raise Exception(f"解压 natives 文件时出错：{e}")
    file_count = len([f for f in os.listdir(natives_directory) if os.path.isfile(os.path.join(natives_directory, f))])
    print(f"已解压 {file_count} 个 natives 文件到 {natives_directory}")


async def download_game_files(version_json, game_directory, version, MIRROR_SOURCES, progress_callback=None, ):
    """下载游戏文件."""
    # from main import mirror_source, MIRROR_SOURCES
    os.makedirs(game_directory, exist_ok=True)  # 确保游戏目录存在

    async def download_and_update_progress(download_function, url, file_path):
        """异步下载文件并更新进度条."""
        await download_function(url, file_path, progress_callback=progress_callback)

    async def update_progress(progress):
        if progress_callback:
            progress_callback(progress)

    version_id = version_json["id"]
    # 保存 version.json 文件
    version_json_path = os.path.join(game_directory, "versions", version_id,
                                     f"{version_id}.json")
    os.makedirs(os.path.dirname(version_json_path), exist_ok=True)
    with open(version_json_path, "w") as f:
        json.dump(version_json, f, indent=4)

    # 下载主文件
    await update_progress(0.0)
    print(version_json["downloads"]["client"]["url"])
    await download_and_update_progress(
        download_file,
        version_json["downloads"]["client"]["url"],
        os.path.join(game_directory, "versions", version_id, f"{version_id}.jar"),
    )
    await update_progress(0.2)

    # 下载库文件
    tasks = []
    semaphore = asyncio.Semaphore(5)
    for i, library in enumerate(version_json["libraries"]):
        if "downloads" in library:
            # 下载 artifact 文件
            if "artifact" in library["downloads"]:
                artifact_path = os.path.join(
                    game_directory,
                    "libraries",
                    library["downloads"]["artifact"]["path"]
                ).replace('/', os.path.sep)
                tasks.append(download_file(library["downloads"]["artifact"]["url"],
                                           artifact_path,
                                           progress_callback, semaphore))

            # 下载 natives-windows 文件
            if "classifiers" in library["downloads"] and "natives-windows" in library["downloads"]["classifiers"]:
                natives_path = os.path.join(
                    game_directory,
                    "libraries",
                    library["downloads"]["classifiers"]["natives-windows"]["path"]
                ).replace('/', os.path.sep)
                tasks.append(download_file(library["downloads"]["classifiers"]["natives-windows"]["url"],
                                           natives_path,
                                           progress_callback, semaphore))
                # 下载完成后，解压 natives 文件
                # extract_natives(version_json, game_directory, version_id)

    await asyncio.gather(*tasks)

    await update_progress(0.8)

    # 下载资源文件
    await download_assets(version_json, game_directory, version_id, MIRROR_SOURCES, progress_callback)
