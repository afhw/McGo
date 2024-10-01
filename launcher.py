import os
import subprocess
import json
import requests
import configparser

# 读取配置文件
config = configparser.ConfigParser()
config.read("launcher_config.ini")


def launch_minecraft(java_path, version_id, game_directory=".minecraft", minecraft_access_token=None, username=None, uuid=None):
    """启动 Minecraft。"""
    version_json_path = os.path.join(
        game_directory, "versions", version_id, f"{version_id}.json"
    )
    version_jar_path = os.path.join(
        game_directory, "versions", version_id, f"{version_id}.jar"
    )

    if not os.path.exists(version_json_path) or not os.path.exists(
        version_jar_path
    ):
        # 如果 version.json 或 version.jar 不存在，则需要下载
        return False
    else:
        # 检查 version.json 文件大小是否与服务器一致
        try:
            with open(version_json_path, "r") as f:
                local_version_json = json.load(f)
            remote_version_size = int(
                requests.head(local_version_json["downloads"]["client"]["url"])
                .headers["Content-Length"]
            )
            local_version_size = os.path.getsize(version_jar_path)
            if local_version_size != remote_version_size:
                return False
        except (
            requests.exceptions.RequestException,
            KeyError,
            json.JSONDecodeError,
        ):
            # 如果获取文件大小失败，则默认需要下载
            return False

    # --- 只有当文件完整存在时，才执行以下代码 ---

    with open(version_json_path, "r") as f:
        version_json = json.load(f)

    # 构造 JVM 参数
    jvm_arguments = [
        "-Xmx1G",
        "-XX:+UnlockExperimentalVMOptions",
        "-XX:+UseG1GC",
        "-XX:G1NewSizePercent=20",
        "-XX:G1ReservePercent=20",
        "-XX:MaxGCPauseMillis=50",
        "-XX:G1HeapRegionSize=32M",
        f"-Djava.library.path={os.path.join(game_directory, 'versions', version_json['id'], version_json['id'] + '-natives')}",
    ]
    print(
        f"-Djava.library.path={os.path.join(game_directory, 'versions', version_json['id'], version_json['id'] + '-natives')}"
    )
    print("UUID:",uuid)
    # 构造游戏参数
    game_arguments = [
        "--username", username if username else config["USER"]["username"],
        "--version", version_json["id"],
        "--gameDir", game_directory,
        "--assetsDir", os.path.join(game_directory, "versions", version_id, "assets"),
        "--assetIndex", version_json["assetIndex"]["id"],
        "--uuid", uuid if uuid else config["USER"]["uuid"],
        "--accessToken", minecraft_access_token if minecraft_access_token else config["USER"]["accessToken"],
        "--userType", "msa" if minecraft_access_token else "mojang",  # 根据登录方式设置 userType
        "--versionType", version_json["type"],
    ]

    # 构造 Classpath
    print(version_json["assetIndex"]["id"])
    classpath = [
        os.path.join(game_directory, "libraries", library["downloads"]["artifact"]["path"])
        for library in version_json["libraries"]
        if "downloads" in library and "artifact" in library["downloads"]
    ]
    classpath.append(
        os.path.join(
            game_directory, "versions", version_json["id"], f"{version_json['id']}.jar"
        )
    )
    classpath_string = os.pathsep.join(classpath)

    # 构造完整命令
    command = [
        java_path,
        *jvm_arguments,
        "-cp",
        classpath_string,
        version_json["mainClass"],
        *game_arguments,
    ]

    # 启动游戏
    subprocess.Popen(command)
    return True


def get_local_versions(game_directory=".minecraft"):
    """获取本地已存在的 Minecraft 版本列表。"""
    versions = []
    versions_dir = os.path.join(game_directory, "versions")
    if os.path.exists(versions_dir):
        for version_dir in os.listdir(versions_dir):
            if os.path.isdir(os.path.join(versions_dir, version_dir)):
                versions.append(version_dir)
    return versions


def get_remote_versions(version_type="release"):
    """从 Mojang 服务器获取可下载的 Minecraft 版本列表."""
    url = "https://launchermeta.mojang.com/mc/game/version_manifest.json"
    try:
        response = requests.get(url)
        response.raise_for_status()
        versions = [
            version["id"]
            for version in response.json()["versions"]
            if version["type"] == version_type
        ]
        return versions
    except Exception as e:
        raise Exception(f"获取可下载版本列表时出错: {e}")


def get_version_url(version_id):
    """从 Mojang 服务器获取指定版本号的 JSON 文件下载地址。"""
    url = "https://launchermeta.mojang.com/mc/game/version_manifest.json"
    try:
        response = requests.get(url)
        response.raise_for_status()
        for version in response.json()["versions"]:
            if version["id"] == version_id:
                return version["url"]
        raise ValueError(f"版本号 {version_id} 不存在")
    except Exception as e:
        raise Exception(f"获取版本 {version_id} 下载地址时出错: {e}")