# 账号与登录

## 离线账号

进入“账号”页面的“离线账号”区域，填写用户名后点击“添加/更新”。UUID 可留空，启动器会自动生成。

## Microsoft 账号

进入“账号”页面的“Microsoft 登录”区域，点击“添加 Microsoft 账号”。启动器先准备本机回调，再打开默认浏览器；完成授权后自动保存账号，无需再点击等待按钮。

如果浏览器无法自动打开，可以展开“手动登录链接”并复制链接。本机回调在生成或复制链接时已经准备好。登录过程中可以点击“取消登录”，离开账号页面也会取消等待并释放端口。端口 5000 被其它程序占用时，界面会显示回调启动失败原因。

登录成功后账号会保存到 `accounts.json`。启动游戏或预览命令前，如果账号保存了 refresh token，启动器会刷新 Microsoft 登录状态并写回新 token。

## 外置登录

进入“账号”页面的“外置登录”区域，填写认证服务器、用户名、密码和 authlib-injector 路径。也可以点击“选择 Jar”选择本地文件，或点击“自动下载”下载最新版 authlib-injector 到游戏目录。

认证服务器应填写 Yggdrasil/Authlib-Injector 根地址，例如：

```text
https://example.com/api/yggdrasil
```

如果误填到 `/authserver`，启动器会自动修正为根地址。非本机地址必须使用 HTTPS。

可用操作：

- 规范化地址：把误填到 `/authserver` 的地址修正为 Yggdrasil 根地址。
- 测试服务器：探测 Yggdrasil/Authlib-Injector 端点是否可访问。
- 选择 Jar：选择已有 authlib-injector jar。
- 自动下载：获取 authlib-injector 元数据，下载 jar 并校验 SHA256。
- 登录并添加：使用外置登录账号密码登录并写入账号列表。
- 生成 JVM 参数：预览 authlib-injector 启动参数。

外置登录账号启动或预览命令前会先 refresh/validate token，并自动注入 authlib-injector JVM 参数。错误信息会尽量显示服务器返回的 `errorMessage`。
