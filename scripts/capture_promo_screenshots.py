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

from PyQt6.QtCore import Qt  # noqa: E402
from PyQt6.QtWidgets import QApplication  # noqa: E402
from qfluentwidgets import Theme, setTheme  # noqa: E402

import main as mcgo_main  # noqa: E402


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


def save_window(window, name: str):
    page = window.stackedWidget.currentWidget() if hasattr(window, "stackedWidget") else None
    if page is not None and hasattr(page, "verticalScrollBar"):
        page.verticalScrollBar().setValue(0)
    QApplication.processEvents()
    pixmap = window.grab()
    path = OUT_DIR / f"{name}.png"
    pixmap.save(str(path), "PNG")
    return path


def prepare_demo_state(window):
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
        window.p2p_status_label.setText("P2P 隧道未启动，填写房间后即可邀请好友")
    if hasattr(window, "home_custom_card"):
        window.home_custom_card.setVisible(False)
    if hasattr(window, "download_metrics_label"):
        window.download_metrics_label.setText("等待下载，可按需安装 Fabric / Forge / NeoForge / OptiFine")
    if hasattr(window, "resource_status_label"):
        window.resource_status_label.setText("输入关键词后搜索资源，支持兼容性验证")
    window.update_home_summary()


def main():
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    mcgo_main.load_config()
    mcgo_main.config["ACCOUNTS"]["selected_account_id"] = DEMO_ACCOUNT["id"]
    mcgo_main.config["GAME"]["directory"] = ".minecraft"
    mcgo_main.config["UI"]["theme"] = "dark"
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

    window.switch_main_page(window.home_page, window.home_cards)
    pump(app, 0.4)
    shots.append(save_window(window, "01-home"))

    window.open_version_section("selector", "本地版本")
    pump(app, 0.4)
    shots.append(save_window(window, "02-launch"))

    window.open_download_section("vanilla", "获取游戏")
    pump(app, 0.4)
    shots.append(save_window(window, "03-download"))

    window.open_download_section("resources", "导入内容")
    pump(app, 0.4)
    shots.append(save_window(window, "04-resources"))

    window.open_account_section("microsoft", "账号管理")
    pump(app, 0.4)
    shots.append(save_window(window, "05-accounts"))

    window.open_online_page()
    pump(app, 0.4)
    shots.append(save_window(window, "06-online"))

    for path in shots:
        print(path)
    window.close()
    app.quit()


if __name__ == "__main__":
    main()
