# McGo

McGo 是一个 C# / .NET 8 Minecraft 启动器，支持账号配置、版本扫描、启动命令构建、游戏下载工具、资源市场、整合包/开黑胶囊、自更新和 P2P 联机相关核心服务。

## 快速开始

```bash
dotnet build McGo.slnx
dotnet run --project src/McGo.WinUI/McGo.WinUI.csproj
```

核心服务校验：

```bash
dotnet run --project tests/McGo.Core.Tests/McGo.Core.Tests.csproj
```

项目主体正在迁移到 `src/McGo.Core` 和 WinUI 3 客户端 `src/McGo.WinUI`。旧实现已移动到 `legacy/`，仅作为迁移参照和行为对照，不再作为默认入口、CI 或发布路径。

发布 Windows 桌面版：

```powershell
.\scripts\build_release.ps1
```

## 文档导航

- [快速开始与界面概览](docs/getting-started.md)
- [账号与登录](docs/accounts.md)
- [下载、补全与安装扩展](docs/downloads.md)
- [版本设置与启动参数](docs/version-settings.md)
- [资源市场与 Mod 管理](docs/resources-and-mods.md)
- [整合包导入导出](docs/modpacks.md)
- [个性化、主页、音乐与联机入口](docs/personalization.md)
- [P2P 联机与中继部署](docs/p2p.md)
- [故障排查](docs/troubleshooting.md)
- [WinUI 3 功能迁移清单](docs/winui-migration.md)

## 数据与安全

`accounts.json`、`launcher_config.ini`、`.minecraft/` 和 `logs/` 是本地运行数据，可能包含账号令牌、目录路径或日志信息，不应提交到公开仓库。
