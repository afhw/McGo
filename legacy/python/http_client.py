import time

import requests


DEFAULT_TIMEOUT = 30
DEFAULT_USER_AGENT = "McGo/1.0"


class HttpRequestError(RuntimeError):
    def __init__(self, message, *, url="", status_code=0):
        super().__init__(message)
        self.url = url
        self.status_code = status_code


def _headers(headers=None):
    merged = {"User-Agent": DEFAULT_USER_AGENT}
    if headers:
        merged.update(headers)
    return merged


def _friendly_request_error(exc, url):
    if isinstance(exc, requests.Timeout):
        return f"网络请求超时：{url}"
    if isinstance(exc, requests.ConnectionError):
        return f"无法连接网络服务：{url}"
    return f"网络请求失败：{url}：{exc}"


def request(method, url, *, timeout=DEFAULT_TIMEOUT, retries=2, headers=None, **kwargs):
    last_error = None
    for attempt in range(max(1, retries + 1)):
        try:
            response = requests.request(
                method,
                url,
                timeout=timeout,
                headers=_headers(headers),
                **kwargs,
            )
            if response.status_code >= 500 and attempt < retries:
                time.sleep(min(2 ** attempt, 4))
                continue
            return response
        except requests.RequestException as exc:
            last_error = exc
            if attempt >= retries:
                raise HttpRequestError(_friendly_request_error(exc, url), url=url) from exc
            time.sleep(min(2 ** attempt, 4))
    raise HttpRequestError(_friendly_request_error(last_error, url), url=url)


def get(url, **kwargs):
    return request("GET", url, **kwargs)


def post(url, **kwargs):
    return request("POST", url, **kwargs)


def raise_for_status(response, action="请求"):
    try:
        response.raise_for_status()
    except requests.HTTPError as exc:
        detail = ""
        try:
            data = response.json()
            detail = data.get("error_description") or data.get("errorMessage") or data.get("error") or ""
        except ValueError:
            detail = response.text[:300].strip()
        message = f"{action}失败（HTTP {response.status_code}）"
        if detail:
            message += f"：{detail}"
        raise HttpRequestError(message, url=response.url, status_code=response.status_code) from exc
