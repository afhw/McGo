# 账号与登录

## 离线账号

进入“管理 -> 账号 -> 离线账号”，填写用户名后点击“添加/更新离线账号”。高级模式下可以手动填写 UUID 和 Access Token。

## Microsoft 账号

进入“管理 -> 账号 -> Microsoft”，点击“添加 Microsoft 账号”。如果关闭自动打开浏览器，启动器会显示登录链接，可复制后手动打开。

登录成功后账号会保存到 `accounts.json`。启动游戏时会自动刷新 Microsoft 登录状态。

## 外置登录

进入“管理 -> 账号 -> 外置登录”，填写认证服务器、用户名、密码和 authlib-injector 路径。也可以点击“自动下载”获取 authlib-injector。

认证服务器应填写 Yggdrasil/Authlib-Injector 根地址，例如：

```text
https://example.com/api/yggdrasil
```

如果误填到 `/authserver`，启动器会自动修正为根地址。非本机地址必须使用 HTTPS。

可用操作：

- 测试服务器：后台探测认证端点是否可访问。
- 登录并添加外置账号：后台登录，不阻塞主界面。
- 刷新/验证当前外置账号：优先 refresh，失败后尝试 validate 当前 token。
- 自动下载：下载 authlib-injector.jar 到当前游戏目录。

外置登录账号启动前会验证或刷新 token，并自动注入 authlib-injector JVM 参数。错误信息会尽量显示服务器返回的 `errorMessage`。
