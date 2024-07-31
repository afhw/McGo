import os
import requests
import zipfile
import json
import asyncio
import aiohttp
import patoolib  # pip install patool

global_library_path = ""

async def download_file(url, file_path, progress_callback=None, semaphore=None, retries=3):
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


async def download_assets(version_json, game_directory, version_id, progress_callback=None):
    """下载资源索引文件和资源文件到游戏版本目录"""
    asset_index_url = version_json["assetIndex"]["url"]
    asset_index_path = os.path.join(
        game_directory, 'versions', version_id, "assets", "indexes",
        f"{version_json['assetIndex']['id']}.json"
    ).replace('/', os.path.sep)

    # 下载资源索引文件
    await download_file(asset_index_url, asset_index_path, progress_callback)

    # 解析资源索引文件
    with open(asset_index_path, "r") as f:
        asset_index_json = json.load(f)

    # 创建下载任务列表
    tasks = []
    # 限制并发下载数量为5
    semaphore = asyncio.Semaphore(5)
    for i, (object_name, object_info) in enumerate(asset_index_json["objects"].items()):
        object_hash = object_info["hash"]
        object_url = f"https://resources.download.minecraft.net/{object_hash[:2]}/{object_hash}"
        object_path = os.path.join(
            game_directory, 'versions', version_id, "assets", "objects", object_hash[:2], object_hash
        ).replace('/', os.path.sep)
        # 将下载任务添加到列表中
        tasks.append(download_file(object_url, object_path, progress_callback, semaphore))

    # 并发执行所有下载任务
    await asyncio.gather(*tasks)


# import zipfile

def extract_natives(version_json, game_directory, version_id):
    for library in version_json["libraries"]:
        # 只处理包含 "natives-windows" 分类器的库文件
        if (
            "downloads" in library
            and "classifiers" in library["downloads"]
            and "natives-windows" in library["downloads"]["classifiers"]
        ):
            natives_jar_path = os.path.join(
                game_directory,
                'libraries',
                library["downloads"]["classifiers"]["natives-windows"]["path"]
            ).replace('/', os.path.sep)
            natives_directory = os.path.join(
                game_directory, 'versions', version_id, f"{version_id}-natives"
            )
            os.makedirs(natives_directory, exist_ok=True)

            # 使用 zipfile 解压 .jar 文件
            try:
                with zipfile.ZipFile(natives_jar_path, "r") as zip_ref:
                    zip_ref.extractall(natives_directory)
            except FileNotFoundError:
                print(f"警告: natives 文件不存在: {natives_jar_path}")
            except Exception as e:
                raise Exception(f"解压 natives 文件时出错：{e}")


async def download_game_files(version_json, game_directory, version, progress_callback=None,):
    """下载游戏文件."""
    os.makedirs(game_directory, exist_ok=True)  # 确保游戏目录存在

    async def download_and_update_progress(download_function, url, file_path):
        """异步下载文件并更新进度条."""
        # print(type(url))
        await download_function(url, file_path, progress_callback=progress_callback)

    async def update_progress(progress):
        if progress_callback:
            progress_callback(progress)

    version_id = version_json["id"]
    # print(version_id["id"])
    # 保存 version.json 文件
    version_json_path = os.path.join(game_directory, "versions", version_id,
                                     f"{version_id}.json")
    os.makedirs(os.path.dirname(version_json_path), exist_ok=True)
    with open(version_json_path, "w") as f:
        json.dump(version_json, f, indent=4)

    # 下载主文件
    await update_progress(0.0)
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
        if "downloads" in library and "artifact" in library["downloads"]:
            library_path = os.path.join(
                game_directory,
                "libraries",
                library["downloads"]["artifact"]["path"]
            ).replace('/', os.path.sep)
            # print(library_path)
            global global_library_path
            global_library_path = library_path
            tasks.append(download_file(library["downloads"]["artifact"]["url"], library_path,
                                       progress_callback, semaphore))
    await asyncio.gather(*tasks)

    await update_progress(0.8)

    # 下载资源文件
    await download_assets(version_json, game_directory, version_id, progress_callback)
    # 解压 natives 文件
    # extract_natives(version_json, game_directory, version_id)


def download_version_json(version_url):
    """下载并解析版本 JSON 文件"""
    try:
        response = requests.get(version_url)
        response.raise_for_status()
        return response.json()
    except Exception as e:
        raise Exception(f"下载版本 JSON 文件时出错: {e}")
