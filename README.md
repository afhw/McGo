# McGo

McGo 是一个基于 PyQt6 和 QFluentWidgets 的 Minecraft 启动器，支持账号管理、游戏下载、加载器安装、资源市场、整合包导入导出、版本独立设置和本地实例管理。

## 快速开始

```bash
python -m pip install -r requirements.txt
python main.py
```

轻量校验：

```bash
python -m py_compile main.py launcher.py downloader.py auth.py auth_server.py java_utils.py version_utils.py log_utils.py
```

## 文档导航

- [快速开始与界面概览](docs/getting-started.md)
- [账号与登录](docs/accounts.md)
- [下载、补全与安装扩展](docs/downloads.md)
- [版本设置与启动参数](docs/version-settings.md)
- [资源市场与 Mod 管理](docs/resources-and-mods.md)
- [整合包导入导出](docs/modpacks.md)
- [个性化、主页、音乐与联机入口](docs/personalization.md)
- [故障排查](docs/troubleshooting.md)

## 数据与安全

`accounts.json`、`launcher_config.ini`、`.minecraft/` 和 `logs/` 是本地运行数据，可能包含账号令牌、目录路径或日志信息，不应提交到公开仓库。
