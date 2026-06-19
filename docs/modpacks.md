# 整合包导入导出

## 导入

进入“下载 -> 导入整合包”，选择 `.mrpack` 或 `.zip`。

支持类型：

- Modrinth `.mrpack`：读取 `modrinth.index.json`，下载声明文件并复制 overrides。
- CurseForge manifest 包：读取 `manifest.json`，导入 overrides，并尝试下载可直接访问的文件。
- 普通 zip：按覆写包处理。

导入完成后会创建或更新本地版本，并默认启用资源隔离。

## 导出

进入“启动 -> 版本设置 -> 快捷方式”，点击“导出整合包”。

支持导出：

- Modrinth `.mrpack`
- CurseForge manifest zip
- 普通 zip 覆写包

导出会收集当前版本运行目录中的文件，并跳过日志、临时文件和部分启动器本地配置。

## 开黑胶囊

进入“联机”，可以导入或导出 `.mcgo-capsule` 开黑胶囊。

开黑胶囊会保存：

- 当前版本的基础信息和版本 JSON。
- P2P 中继、房间号、口令和端口。
- 私有文件清单和 SHA256 校验。

胶囊不会内嵌私有文件本体，只保存 `mods`、`config`、`resourcepacks`、`shaderpacks`、`datapacks` 中可共享文件的清单。房主作为房主启动 P2P 后，启动器会自动开启同房间的胶囊补包通道；加入者导入胶囊并点击“补齐胶囊文件”，启动器会按清单通过 P2P 拉取缺失文件并校验 SHA256。

胶囊不会记录 `saves`、日志、崩溃报告、截图、缓存和常见账号/启动器配置文件。导入后会创建隔离实例，并把胶囊里的 P2P 邀请信息同步到联机页。
