# TODO

## C# 重构完成清单

- [x] 主应用切换到 C# / .NET 8 WinUI 3：入口位于 `src/McGo.WinUI`。
- [x] 核心业务迁移到 `src/McGo.Core`：配置、账号、启动、下载、安装、资源、整合包、胶囊、自更新和 P2P 均已拆分为服务。
- [x] 联机中继迁移到 `src/McGo.Relay`：提供 TCP stream 配对转发和 UDP 端点交换。
- [x] 旧实现移动到 `legacy/`：仅保留为历史行为参照，不再作为默认入口、CI 或发布路径。
- [x] 根目录旧入口、依赖文件和旧发布脚本已移除。
- [x] CI 改为 `dotnet build McGo.slnx` 和 C# 核心测试运行器。
- [x] 发布脚本改为 `scripts/build_release.ps1`，输出 WinUI 3 `dist/app` 和中继 `dist/relay`。
- [x] 文档入口更新为 C# / .NET / WinUI 3 使用方式。

## 已覆盖能力

- [x] 账号：离线、Microsoft OAuth、外置登录、令牌安全存储和刷新。
- [x] 启动：版本扫描、继承解析、classpath、JVM/游戏参数、版本独立设置、启动脚本导出和启动前检查。
- [x] 下载与修复：版本 JSON、客户端 jar、libraries、assets、natives、校验、并发、限速、缓存策略和镜像重写。
- [x] 安装：Fabric、Fabric API、Forge、NeoForge、OptiFine 和 Java 运行时检测/下载。
- [x] 资源：Modrinth 搜索安装、依赖解析、本地资源启用/禁用/删除和兼容性提示。
- [x] 整合包与胶囊：Modrinth `.mrpack`、CurseForge manifest、普通 zip、胶囊导入导出和缺失文件报告。
- [x] 联机：服务器列表、NAT 检测、UDP 优先 P2P 隧道和 TCP 回退。
- [x] 个性化：主题、自定义主页、联网纯文本主页和背景音乐。
- [x] 更新：清单检查、启动时自动检查、下载校验和打包 exe 自动替换。
- [x] 测试：C# package-free 测试运行器覆盖核心服务行为。

## 后续维护建议

- [ ] 按 [WinUI 3 功能迁移清单](docs/winui-migration.md) 将旧版能力逐项迁移到 WinUI 3。
- [ ] 为 WinUI 3 窗口补充自动化 UI 冒烟测试。
- [ ] 按真实发布流程补充更新清单生成与签名发布步骤。
- [~] 崩溃日志分析：已扩展到 14 类常见崩溃，附带处理建议与关键错误行提取（见 `docs/troubleshooting.md`），并新增核心测试覆盖；后续可继续补充规则。
- [ ] 继续扩展资源市场源。
