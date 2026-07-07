# 下载、补全与安装扩展

## 下载原版

进入“下载”页面：

1. 选择版本类型。
2. 点击“刷新远程版本”。
3. 选择远程版本和镜像源。
4. 按需要勾选下载后继续安装 Fabric、Forge、NeoForge、OptiFine 或 Fabric API。
5. 点击“下载所选版本”。

## 下载设置

- 核心线程：控制客户端与库文件下载并发。
- 资源线程：控制 assets 下载并发。
- 速度限制：`0 KB/s` 表示不限速。
- 缓存策略：`reuse` 会尝试复用其他 `.minecraft` 缓存，`network_only` 只走网络下载。
- Modrinth API：留空使用官方 `https://api.modrinth.com/v2`，也可以填写自建或可信代理前缀。
- Modrinth CDN：留空使用官方下载链接；填写后会把 `cdn.modrinth.com` 文件链接改写到该前缀。

单文件下载失败时会在 official 与 BMCLAPI 之间自动切换。

## 补全/校验文件

进入“启动”页面，选中本地版本后点击“补全/校验文件”。启动器会校验并补齐：

- 客户端 jar。
- 依赖库和当前平台 natives。
- 资源索引。
- assets 资源文件。

补全完成后会自动解压 natives 到版本目录下的 `版本ID-natives` 文件夹。已存在且 SHA1/大小匹配的文件会直接复用。

## 安装扩展

下载原版时可以勾选 Fabric、Forge、NeoForge、OptiFine 或 Fabric API。原版下载完成后，C# 安装器会继续执行对应加载器安装任务，并把状态写入下载日志。
