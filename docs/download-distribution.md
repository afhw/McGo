# 下载分发

安装包由 GitHub Releases 托管，官网的 `/download/` 入口只返回 302 跳转，默认使用 `https://gh-proxy.org/` 公共镜像喵。

网页仍请求同源的更新清单，主按钮和已安装客户端继续使用清单中的 `url`，因此无须升级客户端便能使用新的分发线路喵。
可选的 `github_url` 提供 GitHub 原始资产地址，网页显示为“GitHub 直连”备用入口；浏览器下载失败时需要手动选择备用入口，当前客户端不会自动切换镜像喵。

## 当前资产

| 版本 | GitHub Release 标签 | 文件 |
| --- | --- | --- |
| C# 1.1.3 测试版 | `csharp-preview-1.1.3` | `McGo-CSharp-Preview-x64-Setup.exe` |
| Python 1.0.11 稳定版 | `public` | `McGo.exe` |

Python 的 `public` 标签是已有发布，已按 SHA-256 确认它与官网的 1.0.11 文件一致；不能按旧 Release 标题重新推断二进制版本喵。

## 部署与更新

在站点 HTTPS `server` 块内 include [download-routes.conf](../webside/deploy/download-routes.conf)，先运行 `nginx -t`，再 reload，并验证每个入口的 302 Location 喵。
正式环境配置副本位于 `/etc/nginx/snippets/mcgo-download-routes.conf`，更新版本时需与网页清单一起部署，避免网站新清单引用旧安装包喵。

1. 构建并验证安装包，上传到新的 GitHub Release 标签，不覆盖既有标签中的同名二进制喵。
2. 上传安装包对应的 `.sha256` 文件，并从镜像完整下载，核对字节数、SHA-256 与构建产物一致喵。
3. 更新 `webside/public/update-csharp.json` 的 `github_url` 及 `download-routes.conf` 的目标标签，同时更新版本、大小和 SHA-256 喵。
4. 保留清单的 `/download/` 路径，让原有客户端与统计继续工作；`build_csharp_installer.ps1` 重新生成清单后需要补充本次 Release 的 `github_url` 喵。

官网统计仍累计成功的下载入口请求，其中包括 302；绕过官网直接访问 GitHub 的下载不会计入官网统计，不能把它当作总完成下载量喵。
镜像属于第三方服务，默认线路可以通过 Nginx 配置替换；客户端仍按官网清单的 SHA-256 校验文件，网站也保留校验值喵。
