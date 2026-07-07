# P2P 联机与中继部署

McGo 的 P2P 联机会优先尝试 UDP 打洞。打洞成功后，启动器会在 UDP 上构建一个可靠有序的 TCP 字节流隧道来承载 Minecraft Java 版的局域网联机流量；如果 UDP 不可用，会自动回退到 TCP 中继转发。房主在游戏内“对局域网开放”后，把聊天栏显示的端口填到 McGo；加入者启动隧道后，在 Minecraft 里连接本机地址。

默认中继地址：

```text
flyliq.cn:10721
```

## 客户端使用

房主：

1. 进入单人世界，点击“对局域网开放”。
2. 复制聊天栏显示的端口，例如 `51342`。
3. 进入“联机”页面。
4. 填写中继地址 `flyliq.cn`、端口 `10721`、房间号和可选口令。
5. 将“房主 Minecraft LAN 端口”改成第 2 步的端口。
6. 保持“UDP 打洞优先”开启，点击“作为房主启动”，再把邀请信息发给加入者。

加入者：

1. 填写相同的中继地址、端口、房间号和口令。
2. 选择一个本地监听端口，默认 `25565`。
3. 保持“UDP 打洞优先”开启，点击“作为加入者启动”。
4. 在 Minecraft 多人游戏里添加服务器 `127.0.0.1:25565`，端口按实际填写为准。

## 部署服务端

客户端入口正在迁移到 C# WinUI 3 的“联机”页面。C# 中继服务端入口位于 `src/McGo.Relay`：

```bash
dotnet run --project src/McGo.Relay/McGo.Relay.csproj -- --host 0.0.0.0 --port 10721
```

如果使用防火墙，需要同时放行 TCP 和 UDP 端口。TCP 用于房间控制和中继回退，UDP 用于打洞协调：

```bash
sudo ufw allow 10721/tcp
sudo ufw allow 10721/udp
```

云服务器还需要在安全组里同时放行 `10721/tcp` 和 `10721/udp`。

当前 C# Core 已提供 UDP 优先隧道：房主侧会注册 UDP 端点并同时保留 TCP 回退，加入者侧本地 TCP 监听收到 Minecraft 连接后会先尝试 UDP 可靠流，失败再自动走 TCP 中继。WinUI 3 联机页正在承接这些入口。C# 中继提供 TCP stream 配对转发和 UDP 房间端点交换；旧 Python 隧道实现保留在 `legacy/python/p2p_tunnel.py` 和 `legacy/python/p2p_server.py` 作为历史参照。

## 说明

这是中继辅助的 P2P 隧道，不是公网 Minecraft 服务器。中继负责房间控制、UDP 端点交换，以及 UDP 不可用时的 TCP 字节流转发；它不理解 Minecraft 协议，也不保存账号信息。请只把房间号和口令发给可信玩家。
