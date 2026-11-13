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

发布安装器及网站自更新清单（PowerShell 7，Windows x64）：

```powershell
.\scripts\build_csharp_installer.ps1 -ReleaseNotes "本次更新说明"
```

该命令构建 Release / win-x64 客户端和安装器，生成 `webside/public/download/McGo-CSharp-Preview-x64-Setup.exe`、同名 `.sha256` 和 `webside/public/update-csharp.json`。清单的版本来自最终安装器的 ProductVersion（去除 Git 构建后缀），大小和 SHA-256 来自最终文件；打包时验证内含客户端、Core 与安装器版本相同。网页直接读取此清单展示版本、MiB 大小和 SHA-256，下载地址也取自清单。经典版仍读取自己的 `update.json`。

`-ReleaseVersion` 是可选的一致性断言，不能用它给旧产物标记新版本。`-DownloadUrl` 支持 HTTP(S) 绝对地址或以 `/` 开头的网站路径，文件名必须与 `-SetupName` 相同。自定义 `-OutputDirectory` 时清单默认写入该目录的 `update-csharp.json`，也可用 `-ManifestPath` 指定位置；部署时必须一起替换安装器、校验文件和清单。这些脚本不会签名或上传产物，SHA-256 只用于完整性校验；如在外部签名，必须针对签名后的最终文件重新生成清单和校验文件并验证。

另一入口 `.\scripts\build_release.ps1 -Installer -SkipRelay` 会实际构建安装器，并在 `dist/` 生成安装器、校验文件和 `update.json`，不再把客户端 EXE 的哈希标成安装器哈希。不带 `-Installer` 时，加 `-DownloadUrl` 可为 `dist/app/McGo.WinUI.exe` 生成直接替换模式清单；网站与 C# 默认更新通道使用安装器。

仅验证参数（不运行 dotnet，也不写入发布目录）：

```powershell
.\scripts\build_csharp_installer.ps1 -ValidateOnly
.\tests\release\test_release_scripts.ps1
node --test webside/src/releaseMetadata.test.js
```

构建后的验证与 UI 烟测（均不再构建）：

```powershell
.\scripts\verify_release.ps1 -ArtifactPath webside/public/download/McGo-CSharp-Preview-x64-Setup.exe -ManifestPath webside/public/update-csharp.json -AppDirectory dist/app
.\tests\release\test_winui_smoke.ps1 -ExecutablePath dist/app/McGo.WinUI.exe
.\tests\release\test_winui_smoke.ps1 -ExecutablePath dist/app/McGo.WinUI.exe -Resize -ReportPath artifacts/ci/ui-resize.txt
.\tests\release\test_winui_smoke.ps1 -ExecutablePath dist/app/McGo.WinUI.exe -GameDirectories -ReportPath artifacts/ci/ui-directories.txt
```

CI 使用支持 `.slnx` 的 SDK 与 .NET 8 运行时，执行 Release x64 安装器打包、完整负载逐文件哈希校验及网站测试。UI 烟测使用实际桌面尺寸，并检查同一页面连续缩放、导航栏开合及子选项卡的控件边界；Windows runner 没有交互桌面时明确记录 SKIP，已有桌面时的启动失败、超时或页面失败均使 CI 失败，报告保留为 CI artifact。完整 UI 检查需在有交互桌面的 Windows x64 runner 上执行。

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
