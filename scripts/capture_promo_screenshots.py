from __future__ import annotations

import os
import sys
import time
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
OUT_DIR = ROOT / "dist" / "promo" / "screenshots"

sys.path.insert(0, str(ROOT))
os.chdir(ROOT)
os.environ.setdefault("QT_ENABLE_HIGHDPI_SCALING", "0")

from qt_compat import QApplication, QPoint, Qt  # noqa: E402
from qfluentwidgets import Theme, setTheme  # noqa: E402

import main as mcgo_main  # noqa: E402
from app_settings import DownloadTask  # noqa: E402


DEMO_ACCOUNT = {
    "id": "promo-offline",
    "type": "offline",
    "display_name": "Steve",
    "username": "Steve",
    "uuid": "00000000-0000-0000-0000-000000000000",
    "access_token": "",
    "refresh_token": "",
}


def pump(app: QApplication, seconds: float = 0.35):
    deadline = time.time() + seconds
    while time.time() < deadline:
        app.processEvents()
        time.sleep(0.01)


def set_combo_text(combo, text: str):
    if combo is None:
        return
    if hasattr(combo, "setCurrentText"):
        combo.setCurrentText(text)
        return
    if hasattr(combo, "findText") and hasattr(combo, "setCurrentIndex"):
        index = combo.findText(text)
        if index >= 0:
            combo.setCurrentIndex(index)


def set_page_scroll(window, focus_widget=None, value: int | None = None):
    page = window.stackedWidget.currentWidget() if hasattr(window, "stackedWidget") else None
    if page is not None and hasattr(page, "verticalScrollBar"):
        if focus_widget is not None and hasattr(page, "ensureWidgetVisible"):
            page.ensureWidgetVisible(focus_widget, 80, 80)
        elif focus_widget is not None:
            pos = focus_widget.mapTo(page, QPoint(0, 0))
            page.verticalScrollBar().setValue(max(0, pos.y() - 160))
        else:
            page.verticalScrollBar().setValue(value or 0)
    QApplication.processEvents()


def save_window(window, name: str, focus_widget=None, scroll_value: int | None = None):
    set_page_scroll(window, focus_widget=focus_widget, value=scroll_value)
    pixmap = window.grab()
    path = OUT_DIR / f"{name}.png"
    pixmap.save(str(path), "PNG")
    return path


def prepare_demo_files():
    version_dir = ROOT / ".minecraft" / "versions" / "1.21.6-Fabric"
    save_dir = version_dir / "saves" / "演示生存存档"
    mods_dir = version_dir / "mods"
    save_dir.mkdir(parents=True, exist_ok=True)
    mods_dir.mkdir(parents=True, exist_ok=True)
    level_dat = save_dir / "level.dat"
    if not level_dat.exists():
        level_dat.write_bytes(b"McGo promo quick play demo")
    for filename in ("sodium-fabric.jar", "lithium-fabric.jar"):
        mod_path = mods_dir / filename
        if not mod_path.exists():
            mod_path.write_bytes(b"McGo promo mod dependency demo")


def wait_for_background_tasks(app: QApplication, window, timeout: float = 6.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        scan_threads = getattr(window, "scan_threads", {})
        running = [thread for thread in scan_threads.values() if thread and thread.isRunning()]
        if not running:
            return
        app.processEvents()
        time.sleep(0.02)


def prepare_demo_state(window):
    prepare_demo_files()
    if hasattr(window, "remote_version_combo"):
        window.remote_version_combo.reset_items(["1.21.6", "1.20.1", "1.12.2"], current_text="1.21.6")
    if hasattr(window, "local_version_combo"):
        window.local_version_combo.reset_items(["1.21.6-Fabric", "1.20.1-Forge", "1.12.2"], current_text="1.21.6-Fabric")
    if hasattr(window, "version_display_combo"):
        window.version_display_combo.reset_items(["1.21.6-Fabric", "1.20.1-Forge", "1.12.2"], current_text="1.21.6-Fabric")
    if hasattr(window, "resource_search_input"):
        window.resource_search_input.setText("sodium")
    if hasattr(window, "server_list_input"):
        window.server_list_input.setPlainText("play.example.net | 示例服务器\n127.0.0.1:25565 | 本地联机")
    if hasattr(window, "p2p_room_input"):
        window.p2p_room_input.setText("MCGO2026")
    if hasattr(window, "p2p_secret_input"):
        window.p2p_secret_input.setText("friend")
    if hasattr(window, "p2p_status_label"):
        window.p2p_status_label.setText("开黑胶囊已导入：清单 18 个文件，待 P2P 补包 3 个")
    if hasattr(window, "home_custom_card"):
        window.home_custom_card.setVisible(False)
    if hasattr(window, "download_metrics_label"):
        window.download_metrics_label.setText("等待下载，可按需安装 Fabric / Forge / NeoForge / OptiFine")
    if hasattr(window, "install_status_label"):
        window.install_status_label.setText("Modrinth 依赖会随安装任务自动加入队列")
    if hasattr(window, "resource_status_label"):
        window.resource_status_label.setText("安装 Modrinth 资源时会递归安装 required 前置")
    if hasattr(window, "advanced_mode_check"):
        window.advanced_mode_check.setChecked(True)
    if hasattr(window, "download_install_combo"):
        if hasattr(window.download_install_combo, "reset_items"):
            window.download_install_combo.reset_items(
                ["Fabric", "Forge", "NeoForge", "OptiFine", "不安装"],
                current_text="Fabric",
            )
        else:
            set_combo_text(window.download_install_combo, "Fabric")
    if hasattr(window, "download_fabric_api_check"):
        window.download_fabric_api_check.setChecked(True)
    if hasattr(window, "modrinth_api_base_input"):
        window.modrinth_api_base_input.setText("https://api.example.com/modrinth/v2")
    if hasattr(window, "modrinth_cdn_base_input"):
        window.modrinth_cdn_base_input.setText("https://cdn.example.com/modrinth")
    if hasattr(window, "download_addon_hint_label"):
        window.download_addon_hint_label.setText("下载原版后继续安装加载器；Modrinth 慢时可填写 API / CDN 代理前缀。")
    if hasattr(window, "resource_dependency_check"):
        window.resource_dependency_check.setChecked(True)
    if hasattr(window, "resource_result_list"):
        window.resource_result_list.clear()
        window.resource_result_list.addItem("Sodium - 已匹配 Fabric，安装时检查 required 依赖")
        window.resource_result_list.addItem("Fabric API - 作为必需前置自动加入安装队列")
    if hasattr(window, "version_quick_play_combo"):
        window.version_quick_play_combo.reset_items(
            ["不快速进入存档", "演示生存存档"],
            current_text="演示生存存档",
        )
    if hasattr(window, "version_mods_list"):
        window.version_mods_list.clear()
        window.version_mods_list.addItem("sodium-fabric.jar  | 已识别")
        window.version_mods_list.addItem("lithium-fabric.jar | 等待前置检查")
    if hasattr(window, "mod_section_hint"):
        window.mod_section_hint.setText("可按文件 Hash 识别已装 Mod，并扫描 required 依赖后补齐缺失前置。")
    if hasattr(window, "repair_status_label"):
        window.repair_status_label.setText("等待补齐 Mod 前置")
    if hasattr(window, "repair_metrics_label"):
        window.repair_metrics_label.setText("扫描 mods 哈希后，自动补齐缺失 required 前置")
    if hasattr(window, "update_status_label"):
        window.update_status_label.setText("可检查更新、下载新版本、校验 SHA256，并在 Windows 上重启替换")
    if hasattr(window, "update_manifest_input"):
        window.update_manifest_input.setText("https://mcgo.example.com/update.json")
    if hasattr(window, "capsule_host_button"):
        window.capsule_host_button.setText("停止胶囊补包")
        window.capsule_host_button.setEnabled(True)
    if hasattr(window, "capsule_pull_button"):
        window.capsule_pull_button.setEnabled(True)
    if hasattr(window, "set_download_task_record"):
        current_task = DownloadTask("download", "下载 1.21.6 并安装 Fabric + Fabric API", lambda: None)
        queued_task = DownloadTask("launcher_update", "下载 McGo 更新 1.0.10", lambda: None)
        failed_task = DownloadTask("resource_install", "安装 Sodium 依赖", lambda: None)
        window.set_download_task_record(
            current_task,
            status="running",
            progress=48,
            detail="下载资源文件 | 48.0% | 128.0 MB / 266.0 MB | 8.2 MB/s",
        )
        window.set_download_task_record(
            queued_task,
            status="queued",
            progress=0,
            detail="等待前面的任务完成",
        )
        window.set_download_task_record(
            failed_task,
            status="failed",
            progress=72,
            detail="网络超时，可在任务管理中重试",
        )
    window.pending_capsule_payload = {
        "version_id": "1.21.6-Fabric",
        "files": [
            {"path": "mods/sodium-fabric.jar"},
            {"path": "config/sodium-options.json"},
            {"path": "resourcepacks/friend-pack.zip"},
        ],
        "missing_files": ["mods/sodium-fabric.jar", "config/sodium-options.json", "resourcepacks/friend-pack.zip"],
    }
    if hasattr(window, "update_download_advanced_visibility"):
        window.update_download_advanced_visibility()
    if hasattr(window, "update_resource_source_controls"):
        window.update_resource_source_controls()
    if hasattr(window, "update_version_advanced_visibility"):
        window.update_version_advanced_visibility()
    window.update_home_summary()


def main():
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    mcgo_main.load_config()
    mcgo_main.config["ACCOUNTS"]["selected_account_id"] = DEMO_ACCOUNT["id"]
    mcgo_main.config["GAME"]["directory"] = ".minecraft"
    mcgo_main.config["UI"]["theme"] = "dark"
    mcgo_main.config["UPDATE"]["auto_check"] = "false"
    mcgo_main.load_accounts = lambda: [DEMO_ACCOUNT.copy()]

    app = QApplication(sys.argv)
    app.setAttribute(Qt.ApplicationAttribute.AA_DontCreateNativeWidgetSiblings, True)
    setTheme(Theme.DARK)

    window = mcgo_main.LauncherWindow()
    window.resize(1440, 920)
    window.show()
    prepare_demo_state(window)
    pump(app, 0.9)

    shots = []

    window.open_download_section("vanilla", "获取游戏")
    pump(app, 0.4)
    if hasattr(window, "download_install_combo") and hasattr(window.download_install_combo, "reset_items"):
        window.download_install_combo.reset_items(
            ["Fabric", "Forge", "NeoForge", "OptiFine", "不安装"],
            current_text="Fabric",
        )
    shots.append(save_window(window, "01-home", focus_widget=getattr(window, "download_install_combo", None)))

    window.open_version_section("settings", "版本维护")
    pump(app, 0.4)
    if hasattr(window, "version_quick_play_combo"):
        window.version_quick_play_combo.reset_items(
            ["不快速进入存档", "演示生存存档"],
            current_text="演示生存存档",
        )
    shots.append(save_window(window, "02-launch", focus_widget=getattr(window, "version_quick_play_row", None)))

    window.open_download_section("vanilla", "获取游戏")
    pump(app, 0.4)
    if hasattr(window, "download_install_combo") and hasattr(window.download_install_combo, "reset_items"):
        window.download_install_combo.reset_items(
            ["Fabric", "Forge", "NeoForge", "OptiFine", "不安装"],
            current_text="Fabric",
        )
    shots.append(save_window(window, "03-download", focus_widget=getattr(window, "modrinth_api_base_row", None)))

    window.open_download_section("resources", "导入内容")
    pump(app, 0.4)
    shots.append(save_window(window, "04-resources", focus_widget=getattr(window, "resource_dependency_check", None)))

    window.open_manage_section("environment", "环境与界面")
    pump(app, 0.4)
    shots.append(save_window(window, "05-accounts", focus_widget=getattr(window, "update_status_label", None)))

    window.open_online_page()
    pump(app, 0.4)
    shots.append(save_window(window, "06-online", focus_widget=getattr(window, "import_capsule_button", None)))

    window.open_download_section("tasks", "任务管理")
    pump(app, 0.4)
    shots.append(save_window(window, "07-tasks", focus_widget=getattr(window, "download_task_list", None)))

    for path in shots:
        print(path)
    wait_for_background_tasks(app, window)
    window.close()
    app.quit()


if __name__ == "__main__":
    main()
