import os
import uuid as uuidlib
from urllib.parse import urlparse

import http_client
from log_utils import get_logger


AUTHLIB_INJECTOR_METADATA_URL = "https://authlib-injector.yushi.moe/artifact/latest.json"
logger = get_logger(__name__)


def normalize_auth_server(server_url):
    server = (server_url or "").strip().rstrip("/")
    if not server:
        raise RuntimeError("请填写外置登录服务器地址。")
    if not server.startswith(("http://", "https://")):
        server = "https://" + server
    parsed = urlparse(server)
    if parsed.scheme not in {"http", "https"} or not parsed.netloc:
        raise RuntimeError("外置登录服务器地址格式不正确。")
    if parsed.scheme == "http" and parsed.hostname not in {"localhost", "127.0.0.1", "::1"}:
        raise RuntimeError("外置登录服务器必须使用 HTTPS，本机测试地址除外。")
    if server.endswith("/authserver"):
        server = server[: -len("/authserver")]
    return server


def external_auth_endpoint(server, action):
    return f"{normalize_auth_server(server)}/authserver/{action}"


def external_auth_headers():
    return {"Content-Type": "application/json", "Accept": "application/json", "User-Agent": "McGo/1.0"}


def external_auth_error(response, action):
    labels = {
        "authenticate": "登录",
        "refresh": "刷新",
        "validate": "验证",
    }
    action_label = labels.get(action, action)
    detail = ""
    try:
        data = response.json()
        detail = data.get("errorMessage") or data.get("error") or ""
    except ValueError:
        detail = response.text[:300].strip()
    if response.status_code in {401, 403}:
        return f"外置登录{action_label}被拒绝，请检查用户名、密码或令牌。{('详情：' + detail) if detail else ''}"
    if response.status_code == 404:
        return "认证端点不存在，请确认地址是 Yggdrasil/Authlib-Injector 根地址，例如 https://example.com/api/yggdrasil。"
    if response.status_code >= 500:
        return f"认证服务器内部错误（{response.status_code}）。{('详情：' + detail) if detail else ''}"
    return f"外置登录{action_label}失败（HTTP {response.status_code}）。{('详情：' + detail) if detail else ''}"


def raise_for_external_auth(response, action):
    if 200 <= response.status_code < 300:
        return
    raise RuntimeError(external_auth_error(response, action))


def probe_external_auth_server(server_url):
    server = normalize_auth_server(server_url)
    probes = [
        f"{server}/authserver",
        f"{server}/authserver/validate",
        server,
    ]
    errors = []
    for url in probes:
        try:
            if url.endswith("/validate"):
                response = http_client.post(url, json={"accessToken": "mcgo-probe"}, headers=external_auth_headers(), timeout=10)
            else:
                response = http_client.get(url, headers={"Accept": "application/json", "User-Agent": "McGo/1.0"}, timeout=10)
            if response.status_code < 500:
                return {
                    "server": server,
                    "status": response.status_code,
                    "message": f"服务器可访问，探测端点返回 HTTP {response.status_code}",
                }
            errors.append(f"{url}: HTTP {response.status_code}")
        except http_client.HttpRequestError as exc:
            errors.append(f"{url}: {exc}")
    raise RuntimeError("认证服务器不可访问：" + "；".join(errors[-2:]))


def authenticate_external_account(server_url, username, password, client_token=""):
    server = normalize_auth_server(server_url)
    if not username.strip() or not password:
        raise RuntimeError("外置登录需要用户名和密码。")
    client_token = client_token or str(uuidlib.uuid4())
    logger.info("Authenticating external account: server=%s username=%s", server, username.strip())
    payload = {
        "agent": {"name": "Minecraft", "version": 1},
        "username": username.strip(),
        "password": password,
        "clientToken": client_token,
        "requestUser": True,
    }
    try:
        response = http_client.post(
            external_auth_endpoint(server, "authenticate"),
            json=payload,
            headers=external_auth_headers(),
            timeout=30,
        )
    except http_client.HttpRequestError as exc:
        raise RuntimeError(f"无法连接外置登录服务器：{exc}") from exc
    raise_for_external_auth(response, "authenticate")
    try:
        data = response.json()
    except ValueError as exc:
        raise RuntimeError("外置登录服务器返回的不是有效 JSON。") from exc
    selected = data.get("selectedProfile") or {}
    if not selected.get("id") or not selected.get("name") or not data.get("accessToken"):
        raise RuntimeError("外置登录服务器返回的数据不完整。")
    logger.info("External account authenticated: server=%s username=%s uuid=%s", server, selected.get("name"), selected.get("id"))
    return {
        "server": server,
        "username": selected.get("name"),
        "display_name": selected.get("name"),
        "uuid": selected.get("id"),
        "access_token": data.get("accessToken"),
        "client_token": data.get("clientToken", client_token),
    }


def refresh_external_account(account):
    server = normalize_auth_server(account.get("auth_server", ""))
    access_token = account.get("access_token", "")
    client_token = account.get("client_token", "")
    if not access_token:
        raise RuntimeError("外置登录账号缺少 Access Token，请重新登录。")
    logger.info(
        "Refreshing external account: server=%s username=%s uuid=%s",
        server,
        account.get("username", ""),
        account.get("uuid", ""),
    )
    payload = {
        "accessToken": access_token,
        "clientToken": client_token,
        "requestUser": True,
    }
    try:
        response = http_client.post(
            external_auth_endpoint(server, "refresh"),
            json=payload,
            headers=external_auth_headers(),
            timeout=30,
        )
    except http_client.HttpRequestError as exc:
        raise RuntimeError(f"无法连接外置登录服务器：{exc}") from exc
    if response.status_code == 403:
        validate_payload = {"accessToken": access_token, "clientToken": client_token}
        try:
            validate_response = http_client.post(
                external_auth_endpoint(server, "validate"),
                json=validate_payload,
                headers=external_auth_headers(),
                timeout=30,
            )
        except http_client.HttpRequestError as exc:
            raise RuntimeError(f"无法验证外置登录令牌：{exc}") from exc
        raise_for_external_auth(validate_response, "validate")
        logger.info("External account token validated without refresh: server=%s username=%s", server, account.get("username", ""))
        return dict(account)
    raise_for_external_auth(response, "refresh")
    try:
        data = response.json()
    except ValueError as exc:
        raise RuntimeError("外置登录刷新接口返回的不是有效 JSON。") from exc
    selected = data.get("selectedProfile") or {}
    refreshed = dict(account)
    refreshed["access_token"] = data.get("accessToken", access_token)
    refreshed["client_token"] = data.get("clientToken", client_token)
    if selected.get("id"):
        refreshed["uuid"] = selected.get("id")
    if selected.get("name"):
        refreshed["username"] = selected.get("name")
        refreshed["display_name"] = selected.get("name")
    logger.info("External account refreshed: server=%s username=%s uuid=%s", server, refreshed.get("username", ""), refreshed.get("uuid", ""))
    return refreshed


def authlib_injector_download_url():
    logger.info("Fetching authlib-injector metadata: url=%s", AUTHLIB_INJECTOR_METADATA_URL)
    response = http_client.get(AUTHLIB_INJECTOR_METADATA_URL, headers={"User-Agent": "McGo/1.0"}, timeout=30)
    http_client.raise_for_status(response, "获取 authlib-injector 元数据")
    data = response.json()
    version = data.get("version") or "latest"
    checksums = data.get("checksums") or {}
    url = (
        data.get("download_url")
        or data.get("downloadUrl")
        or data.get("url")
        or f"https://authlib-injector.yushi.moe/artifact/{version}/authlib-injector.jar"
    )
    filename = data.get("fileName") or data.get("filename") or f"authlib-injector-{version}.jar"
    logger.info("Authlib-injector metadata fetched: filename=%s url=%s has_sha256=%s", filename, url, bool(checksums.get("sha256", "")))
    return url, filename, checksums.get("sha256", "")


def authlib_injector_args(account):
    if account.get("type") != "external":
        return []
    injector_path = (account.get("authlib_injector_path") or "").strip()
    server = normalize_auth_server(account.get("auth_server", ""))
    if not injector_path:
        raise RuntimeError("外置登录账号缺少 authlib-injector jar 路径。")
    if not os.path.isfile(injector_path):
        raise RuntimeError(f"authlib-injector jar 不存在：{injector_path}")
    logger.debug("Authlib-injector args prepared: server=%s injector=%s", server, injector_path)
    return [
        f"-javaagent:{injector_path}={server}",
        f"-Dauthlibinjector.yggdrasil.prefetched={server}",
    ]
