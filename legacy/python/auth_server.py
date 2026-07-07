import threading

from flask import Flask, request
from markupsafe import escape

from auth import MicrosoftAuthenticator
from log_utils import get_logger


app = Flask(__name__)
authenticator = None
redirect_uri = ""
flask_thread = None
flask_thread_lock = threading.Lock()
logger = get_logger(__name__)


def create_authenticator(client_id, callback_uri):
    global authenticator, redirect_uri
    redirect_uri = callback_uri
    authenticator = MicrosoftAuthenticator(client_id, redirect_uri)
    return authenticator


@app.route("/login/callback")
def login_callback():
    error = request.args.get("error")
    if error:
        logger.warning("Microsoft OAuth callback failed: error=%s", error)
        return render_callback_page(
            "Microsoft 登录失败",
            "Microsoft 返回了错误，请回到 McGo 查看状态日志并重新登录。",
            status="error",
            detail=request.args.get("error_description", error),
        ), 400

    code = request.args.get("code")
    if not code:
        logger.warning("Microsoft OAuth callback missing authorization code")
        return render_callback_page(
            "缺少授权码",
            "回调地址没有收到授权码，请回到 McGo 重新发起 Microsoft 登录。",
            status="error",
        ), 400

    if authenticator is None:
        logger.error("Microsoft OAuth callback received before authenticator initialization")
        return render_callback_page(
            "登录服务未初始化",
            "McGo 本地回调服务尚未准备好，请回到启动器重新发起登录。",
            status="error",
        ), 500

    authenticator.authorization_code = code
    logger.info("Microsoft OAuth callback received: has_code=%s", bool(authenticator.authorization_code))
    return render_callback_page(
        "登录成功",
        "McGo 已收到 Microsoft 授权码。你可以关闭此页面，回到启动器继续。",
    )


@app.route("/")
def oauth_callback_index():
    return render_callback_page(
        "McGo 登录回调服务",
        "这个本地页面用于接收 Microsoft 登录回调。请从 McGo 启动器中发起登录。",
    )


def render_callback_page(title, message, status="success", detail=""):
    is_success = status == "success"
    accent = "#2e7d32" if is_success else "#b3261e"
    icon = "✓" if is_success else "!"
    escaped_title = escape(title)
    escaped_message = escape(message)
    escaped_detail = escape(detail)
    detail_html = f"<p class='detail'>{escaped_detail}</p>" if detail else ""
    return f"""<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{escaped_title}</title>
  <style>
    :root {{
      color-scheme: light dark;
      font-family: "Segoe UI", "Microsoft YaHei", Arial, sans-serif;
    }}
    body {{
      margin: 0;
      min-height: 100vh;
      display: grid;
      place-items: center;
      background: #f4f6f8;
      color: #1f1f1f;
    }}
    main {{
      width: min(520px, calc(100vw - 32px));
      padding: 32px;
      border: 1px solid rgba(0, 0, 0, 0.08);
      border-radius: 10px;
      background: white;
      box-shadow: 0 18px 48px rgba(0, 0, 0, 0.10);
    }}
    .icon {{
      width: 48px;
      height: 48px;
      display: grid;
      place-items: center;
      border-radius: 50%;
      background: {accent};
      color: white;
      font-size: 28px;
      font-weight: 700;
      margin-bottom: 18px;
    }}
    h1 {{
      margin: 0 0 10px;
      font-size: 24px;
      font-weight: 650;
    }}
    p {{
      margin: 0;
      line-height: 1.65;
      color: #4b5563;
    }}
    .detail {{
      margin-top: 12px;
      padding: 12px;
      border-radius: 8px;
      background: rgba(0, 0, 0, 0.04);
      word-break: break-word;
    }}
    @media (prefers-color-scheme: dark) {{
      body {{
        background: #171717;
        color: #f5f5f5;
      }}
      main {{
        background: #242424;
        border-color: rgba(255, 255, 255, 0.10);
      }}
      p {{
        color: #c9c9c9;
      }}
      .detail {{
        background: rgba(255, 255, 255, 0.08);
      }}
    }}
  </style>
</head>
<body>
  <main>
    <div class="icon">{icon}</div>
    <h1>{escaped_title}</h1>
    <p>{escaped_message}</p>
    {detail_html}
  </main>
</body>
</html>"""


def render_oauth_callback_page(title, message, status="success", detail=""):
    return render_callback_page(title, message, status=status, detail=detail)


def run_flask_app():
    app.run(port=5000, debug=False, use_reloader=False)


def start_flask_in_thread():
    global flask_thread
    with flask_thread_lock:
        if flask_thread and flask_thread.is_alive():
            return flask_thread
        flask_thread = threading.Thread(
            target=run_flask_app,
            name="McGoOAuthCallbackServer",
            daemon=True,
        )
        flask_thread.start()
        logger.info("Microsoft OAuth callback server started lazily: %s", redirect_uri)
        return flask_thread
