# 整合包导入导出

## 导入

进入“整合包”页面，在“导入整合包”区域选择 `.mrpack` 或 `.zip`，目标版本 ID 可留空。

支持类型：

- Modrinth `.mrpack`：读取 `modrinth.index.json`，下载声明文件并复制 overrides。
- CurseForge manifest 包：读取 `manifest.json`，导入 overrides，并尝试下载可直接访问的文件。
- 普通 zip：按覆写包处理。

导入完成后会创建或更新本地版本，并默认启用资源隔离。若整合包声明的远程文件无法直接下载，会在版本目录写入 `missing-modpack-files.json`。

## 导出

进入“整合包”页面，选择本地版本、目标文件和格式后点击“导出”。

支持导出：

- Modrinth `.mrpack`
- CurseForge manifest zip
- 普通 zip 覆写包可以通过导入入口导入；导出入口当前生成 Modrinth 或 CurseForge manifest 包。

导出会收集当前版本运行目录中的文件，并跳过日志、临时文件和部分启动器本地配置。

## 开黑胶囊

进入“整合包”页面的“开黑胶囊”区域，可以导入或导出 `.mcgo-capsule` 开黑胶囊。

开黑胶囊会保存：

- 当前版本的基础信息和版本 JSON。
- P2P 中继、房间号、口令和端口。
- 私有文件清单和 SHA256 校验。

胶囊不会内嵌私有文件本体，只保存 `mods`、`config`、`resourcepacks`、`shaderpacks`、`datapacks` 中可共享文件的清单。导入后会列出待补齐文件，后续可根据清单校验 SHA256 并补齐文件。

胶囊不会记录 `saves`、日志、崩溃报告、截图、缓存和常见账号/启动器配置文件。导入后会创建隔离实例，并把胶囊里的 P2P 邀请信息同步到联机页。
