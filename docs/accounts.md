# 账号与登录

## 离线账号

进入“管理 -> 账号 -> 离线账号”，填写用户名后点击“添加/更新离线账号”。高级模式下可以手动填写 UUID 和 Access Token。

## Microsoft 账号

进入“管理 -> 账号 -> Microsoft”，点击“添加 Microsoft 账号”。如果关闭自动打开浏览器，启动器会显示登录链接，可复制后手动打开。

登录成功后账号会保存到 `accounts.json`。启动游戏时会自动刷新 Microsoft 登录状态。

## 外置登录

进入“管理 -> 账号 -> 外置登录”，填写认证服务器、用户名、密码和 authlib-injector 路径。也可以点击“自动下载”获取 authlib-injector。

外置登录账号启动前会验证或刷新 token，并自动注入 authlib-injector JVM 参数。

