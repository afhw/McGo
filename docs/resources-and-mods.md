# 资源市场与 Mod 管理

## 资源市场

进入“下载 -> 资源市场”：

1. 选择来源：Modrinth、CurseForge 或本地。
2. 选择资源类型：Mod、资源包、光影或数据包。
3. 选择安装目标版本。
4. 输入关键词并搜索。

Modrinth 支持兼容性验证和一键安装必需依赖。CurseForge 需要 `CURSEFORGE_API_KEY` 环境变量，当前主要用于搜索和查看详情。本地来源会扫描目标版本对应资源目录。

## 资源详情

详情面板会展示描述、项目链接、截图、依赖和可用版本/文件。截图会缓存到系统临时目录。

## Mod 管理

进入“启动 -> 版本设置 -> Mod 管理”。Fabric、Forge、NeoForge 和 OptiFine 版本会显示 mods 文件夹中的 `.jar` 和 `.jar.disabled` 文件。

- 双击或点击“启用/禁用所选 Mod”会切换 `.jar` 与 `.jar.disabled`。
- “删除所选 Mod”会删除文件。
- 列表会显示启用/禁用状态，并给出基础依赖提示。

