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

