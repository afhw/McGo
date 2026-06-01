import sys

from app_settings import config, load_config


def __getattr__(name):
    if name == "LauncherWindow":
        from ui_window import LauncherWindow

        return LauncherWindow
    raise AttributeError(name)


def main():
    from PyQt6.QtWidgets import QApplication
    from qfluentwidgets import Theme, setTheme
    from ui_window import LauncherWindow

    load_config()
    qt_app = QApplication(sys.argv)
    configured_theme = config.get("UI", "theme", fallback="dark").strip().lower()
    if configured_theme == "light":
        setTheme(Theme.LIGHT)
    elif configured_theme == "auto":
        setTheme(Theme.AUTO)
    else:
        setTheme(Theme.DARK)
    window = LauncherWindow()
    window.show()
    sys.exit(qt_app.exec())


if __name__ == "__main__":
    main()
