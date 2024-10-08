import asyncio
import configparser
import os
import threading
import webbrowser

import flet as ft
import requests
from flask import Flask, request, redirect

from downloader import download_game_files, extract_natives
from java_utils import find_java_paths, get_java_version
from launcher import launch_minecraft, get_local_versions
from auth import MicrosoftAuthenticator

# --- 配置 ---
client_id = "cf1d47c2-2199-495a-9822-a2a2b97cd568"  # 你的 Azure 应用程序 ID
redirect_uri = "http://localhost:5000/login/callback"  # 回调地址

# 镜像源配置
MIRROR_SOURCES = {
    "official": "https://launchermeta.mojang.com",
    "bmclapi": "https://bmclapi2.bangbang93.com",
    # 更多镜像源...
}

# 全局变量
java_path = ""
game_directory = ".minecraft"
config_file = "launcher_config.ini"
mirror_source = "official"  # 默认镜像源为官方源
use_microsoft_login = False  # 是否使用微软登录

# 创建/读取配置文件
config = configparser.ConfigParser()
if not os.path.exists(config_file):
    config["USER"] = {"username": "", "uuid": "", "accessToken": ""}
    config["DOWNLOAD"] = {"mirror_source": "official"}
    config["AUTH"] = {"use_microsoft_login": "False", "refresh_token": ""}
    with open(config_file, "w") as f:
        config.write(f)
else:
    config.read(config_file)
    mirror_source = config.get("DOWNLOAD", "mirror_source")
    use_microsoft_login = config.getboolean("AUTH", "use_microsoft_login")

# 创建认证器实例
authenticator = MicrosoftAuthenticator(client_id, redirect_uri)

# 创建 Flask 应用
app = Flask(__name__)


# --- Flask 路由 ---
@app.route("/login/callback")
def login_callback():
    # 获取授权码
    authenticator.authorization_code = request.args.get("code")
    return "登录成功，你可以关闭此窗口"


def get_remote_versions(version_type):
    """获取可下载版本列表"""
    try:
        response = requests.get(
            f"{MIRROR_SOURCES[mirror_source]}/mc/game/version_manifest.json"
        )
        response.raise_for_status()
        versions = response.json()["versions"]
        return [v["id"] for v in versions if v["type"] == version_type]
    except requests.exceptions.RequestException as e:
        print(f"获取可下载版本列表时出错: {e}")
        return []


def get_version_url(version_id):
    """获取版本的下载链接"""
    try:
        response = requests.get(
            f"{MIRROR_SOURCES[mirror_source]}/mc/game/version_manifest.json"
        )
        response.raise_for_status()
        versions = response.json()["versions"]
        for v in versions:
            if v["id"] == version_id:
                return v["url"]
        return None
    except requests.exceptions.RequestException as e:
        print(f"获取版本的下载链接时出错: {e}")
        return None


async def main(page: ft.Page):
    page.title = "Minecraft 启动器"

    # --- 启动 Flask 应用 ---
    def run_flask_app():
        app.run(port=5000)

    threading.Thread(target=run_flask_app).start()

    # --- 导航栏 ---
    nav_items = [
        ft.NavigationRailDestination(
            icon="settings", label="设置", selected_icon="settings"
        ),
        ft.NavigationRailDestination(
            icon="play_arrow", label="启动游戏", selected_icon="play_arrow"
        ),
        ft.NavigationRailDestination(
            icon="download", label="下载游戏", selected_icon="download"
        ),
    ]

    nav_rail = ft.NavigationRail(
        selected_index=0,
        label_type="all",
        destinations=nav_items,
        on_change=lambda e: on_nav_change(e, page),
    )

    # --- 设置页面 ---
    java_paths = ft.Dropdown(
        label="Java 路径",
        options=[ft.dropdown.Option(path) for path in find_java_paths()],
        on_change=lambda e: on_java_path_change(e, page),
    )
    java_version = ft.Text("")

    def save_user_config():
        global use_microsoft_login
        config["USER"]["username"] = username_input.value
        config["USER"]["uuid"] = uuid_input.value
        config["USER"]["accessToken"] = access_token_input.value
        config["AUTH"]["use_microsoft_login"] = str(use_microsoft_login)
        with open(config_file, "w") as f:
            config.write(f)
        page.snack_bar = ft.SnackBar(ft.Text("用户信息已保存"))
        page.snack_bar.open = True
        update_settings_visibility()
        page.update()

    username_input = ft.TextField(
        label="用户名",
        value=config["USER"]["username"],
        visible=not use_microsoft_login,
    )
    uuid_input = ft.TextField(
        label="UUID", value=config["USER"]["uuid"], visible=not use_microsoft_login
    )
    access_token_input = ft.TextField(
        label="Access Token",
        value=config["USER"]["accessToken"],
        password=True,
        visible=not use_microsoft_login,
    )
    save_button = ft.ElevatedButton(
        text="保存", on_click=lambda _: save_user_config()
    )

    def on_mirror_source_change(e):
        global mirror_source
        mirror_source = mirror_source_dropdown.value
        config["DOWNLOAD"]["mirror_source"] = mirror_source
        with open(config_file, "w") as f:
            config.write(f)
        # 更新可下载版本列表
        remote_version_dropdown.options = [
            ft.dropdown.Option(v)
            for v in get_remote_versions(version_type_dropdown.value)
        ]
        page.update()

    mirror_source_dropdown = ft.Dropdown(
        label="镜像源",
        options=[
            ft.dropdown.Option("official"),
            ft.dropdown.Option("bmclapi"),
            # 可以在这里添加更多镜像源选项
        ],
        value=mirror_source,
        on_change=on_mirror_source_change,
    )

    # --- 处理微软登录 ---
    def handle_microsoft_login():
        webbrowser.open(authenticator.get_login_url())

        async def wait_for_authentication():
            while authenticator.authorization_code is None:
                await asyncio.sleep(1)
            await authenticator.authenticate()
            # 保存刷新令牌到配置文件
            config["AUTH"]["refresh_token"] = authenticator.refresh_token
            with open(config_file, "w") as f:
                config.write(f)

        # 在新的线程中启动异步函数
        threading.Thread(
            target=asyncio.run, args=(wait_for_authentication(),)
        ).start()

    login_button = ft.ElevatedButton(
        text="使用微软账号登录",
        on_click=lambda _: handle_microsoft_login(),
        visible=use_microsoft_login,
    )

    def on_login_mode_change(e):
        global use_microsoft_login
        use_microsoft_login = login_mode_dropdown.value == "microsoft"
        update_settings_visibility()
        page.update()

    login_mode_dropdown = ft.Dropdown(
        label="登录模式",
        options=[
            ft.dropdown.Option("offline"),
            ft.dropdown.Option("microsoft"),
        ],
        value="microsoft" if use_microsoft_login else "offline",
        on_change=on_login_mode_change,
    )

    def update_settings_visibility():
        username_input.visible = not use_microsoft_login
        uuid_input.visible = not use_microsoft_login
        access_token_input.visible = not use_microsoft_login
        login_button.visible = use_microsoft_login

    settings_content = ft.Container(
        ft.Column(
            [
                ft.Text("设置页面", size=20),
                java_paths,
                java_version,
                login_mode_dropdown,
                username_input,
                uuid_input,
                access_token_input,
                login_button,
                mirror_source_dropdown,
                save_button,
            ],
            alignment=ft.MainAxisAlignment.START,
            horizontal_alignment=ft.CrossAxisAlignment.CENTER,
        ),
        expand=True,
        visible=True,
    )

    # --- 启动游戏页面 ---
    def on_launch_click(e, page=page, selected_version=None):
        global java_path
        if not java_path:
            page.snack_bar = ft.SnackBar(ft.Text("请先选择 Java 路径"))
            page.snack_bar.open = True
            page.update()
            return

        selected_version = local_version_dropdown.value
        if not selected_version:
            page.snack_bar = ft.SnackBar(ft.Text("请选择游戏版本"))
            page.snack_bar.open = True
            page.update()
            return

        async def launch_game_async():
            try:
                # 尝试刷新访问令牌
                if (
                    use_microsoft_login
                    and config.has_option("AUTH", "refresh_token")
                ):
                    await authenticator.refresh_access_token(
                        config.get("AUTH", "refresh_token")
                    )

                # 如果已登录微软账号，则获取用户信息
                if authenticator.xsts_token:
                    uuid, username, skin = await authenticator.get_minecraft_profile()
                else:
                    uuid = None
                    username = None

                # 启动游戏
                if not launch_minecraft(
                    java_path,
                    selected_version,
                    game_directory,
                    authenticator.minecraft_access_token,
                    username,
                    uuid,
                ):
                    page.snack_bar = ft.SnackBar(
                        ft.Text(f"本地未找到版本 {selected_version}，请先下载")
                    )
                else:
                    page.snack_bar = ft.SnackBar(
                        ft.Text(f"正在启动 Minecraft {selected_version}...")
                    )
                page.snack_bar.open = True
                page.update()
            except Exception as e:
                print(f"启动游戏时出错: {e}")
                page.snack_bar = ft.SnackBar(ft.Text(f"启动游戏时出错: {e}"))
                page.snack_bar.open = True
                page.update()

        # 在新的线程中启动异步函数
        threading.Thread(
            target=asyncio.run, args=(launch_game_async(),)
        ).start()

    # 本地版本下拉菜单
    local_version_dropdown = ft.Dropdown(
        label="选择本地版本",
        options=[
            ft.dropdown.Option(v) for v in get_local_versions(game_directory)
        ],
    )

    launch_button = ft.ElevatedButton(
        text="启动 Minecraft", on_click=on_launch_click
    )

    launch_game_content = ft.Container(
        ft.Column(
            [
                ft.Text("启动游戏", size=20),
                local_version_dropdown,
                launch_button,
            ],
            alignment=ft.MainAxisAlignment.CENTER,
            horizontal_alignment=ft.CrossAxisAlignment.CENTER,
        ),
        expand=True,
        visible=False,
    )

    # --- 下载游戏页面 ---

    progress_dialog = ft.Ref[ft.AlertDialog]()
    progress_bar = ft.ProgressBar()

    def update_progress(progress):
        if progress_dialog.current is not None:
            progress_bar.value = progress
            page.update()

    def  on_download_click(e, page=page, selected_version=None):
        selected_version = remote_version_dropdown.value
        if not selected_version:
            page.snack_bar = ft.SnackBar(ft.Text("请选择要下载的版本"))
            page.snack_bar.open = True
            page.update()
            return

        def download_game_thread():
            async def run_download():
                try:
                    # 显示对话框
                    progress_dialog.current.open = True
                    page.update()

                    page.snack_bar = ft.SnackBar(
                        ft.Text(f"正在下载 Minecraft {selected_version}...")
                    )
                    page.snack_bar.open = True
                    page.update()

                    version_url = get_version_url(selected_version)
                    response = requests.get(version_url)
                    response.raise_for_status()
                    version_json = response.json()

                    await download_game_files(
                        version_json,
                        game_directory,
                        selected_version,
                        MIRROR_SOURCES[mirror_source],
                        progress_callback=update_progress,
                    )
                    extract_natives(
                        version_json, game_directory, selected_version
                    )

                    page.snack_bar = ft.SnackBar(
                        ft.Text(f"Minecraft {selected_version} 下载完成！")
                    )
                    page.snack_bar.open = True
                    page.update()

                    # 下载完成后更新本地版本列表
                    local_version_dropdown.options = [
                        ft.dropdown.Option(v)
                        for v in get_local_versions(game_directory)
                    ]
                    local_version_dropdown.value = selected_version
                    page.update()

                except Exception as e:
                    print(f"下载游戏时出错: {e}")
                    page.snack_bar = ft.SnackBar(
                        ft.Text(f"下载游戏时出错: {e}")
                    )
                    page.snack_bar.open = True
                    page.update()
                finally:
                    # 关闭对话框
                    progress_dialog.current.open = False
                    page.update()

            # 创建新的 asyncio 事件循环
            loop = asyncio.new_event_loop()
            asyncio.set_event_loop(loop)
            try:
                # 在新的事件循环中运行 run_download 协程
                loop.run_until_complete(run_download())
            finally:
                # 关闭事件循环
                loop.close()

        # 创建并启动新的线程
        threading.Thread(target=download_game_thread).start()

    # 可下载版本下拉菜单
    remote_version_dropdown = ft.Dropdown(
        label="选择可下载版本",
        options=[
            ft.dropdown.Option(v) for v in get_remote_versions("release")
        ],
    )

    # 版本类型选择
    def on_version_type_change(e):
        version_type = version_type_dropdown.value
        remote_version_dropdown.options = [
            ft.dropdown.Option(v)
            for v in get_remote_versions(version_type)
        ]
        page.update()

    version_type_dropdown = ft.Dropdown(
        label="版本类型",
        options=[
            ft.dropdown.Option("release"),
            ft.dropdown.Option("snapshot"),
            ft.dropdown.Option("old_alpha"),
            ft.dropdown.Option("old_beta"),
        ],
        value="release",
        on_change=on_version_type_change,
    )

    download_button = ft.ElevatedButton(
        text="下载", on_click=on_download_click
    )

    download_game_content = ft.Container(
        ft.Column(
            [
                ft.Text("下载游戏", size=20),
                remote_version_dropdown,
                version_type_dropdown,
                download_button,
                ft.AlertDialog(
                    ref=progress_dialog,
                    modal=True,
                    title=ft.Text("下载进度"),
                    content=progress_bar,
                ),
            ],
            alignment=ft.MainAxisAlignment.CENTER,
            horizontal_alignment=ft.CrossAxisAlignment.CENTER,
        ),
        expand=True,
        visible=False,
    )

    # --- 页面切换 ---

    page_content = ft.Stack(
        [settings_content, launch_game_content, download_game_content],
        expand=True,
    )

    def on_nav_change(event, page):
        selected_index = event.control.selected_index
        if selected_index == 0:
            settings_content.visible = True
            launch_game_content.visible = False
            download_game_content.visible = False
        elif selected_index == 1:
            settings_content.visible = False
            launch_game_content.visible = True
            download_game_content.visible = False
        elif selected_index == 2:
            settings_content.visible = False
            launch_game_content.visible = False
            download_game_content.visible = True
        page.update()

    # --- 处理 Java 路径更改 ---

    def on_java_path_change(event, page):
        global java_path
        selected_path = event.control.value
        java_path = selected_path
        version_info = get_java_version(selected_path)
        if version_info:
            java_version.value = version_info
        else:
            java_version.value = "无法获取版本信息"
        page.update()

    # --- 布局 ---

    page.add(ft.Row([nav_rail, page_content], expand=True))

    update_settings_visibility()


# 运行 Flet 应用程序
ft.app(target=main)