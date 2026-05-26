# 故障排查

## 启动失败

1. 检查“启动”页提示的 Java 版本是否满足当前 Minecraft。
2. 使用“补全/校验文件”修复缺失客户端、依赖库、资源文件和 natives。
3. 点击“分析崩溃”查看 latest.log 或 crash-reports 中的常见原因。
4. 如果使用外置登录，确认 authlib-injector 路径和认证服务器可访问。

## 下载失败

- 切换镜像源后重试。
- 将下载预设改为“保守”。
- 设置较低速度限制，避免网络波动。
- 查看状态日志中的失败 URL 和错误信息。

## Mod 不加载

- 确认目标版本是 Fabric、Forge、NeoForge 或 OptiFine。
- 检查 Mod 是否为 `.jar`，`.jar.disabled` 不会加载。
- 检查 Mod 是否匹配当前 Minecraft 版本和加载器。
- Fabric 版本通常需要 Fabric API。

## 账号问题

- Microsoft 账号失败时重新登录。
- 外置登录失败时先点击“刷新/验证当前外置账号”。
- 不要手动分享 `accounts.json`，其中可能包含令牌。
