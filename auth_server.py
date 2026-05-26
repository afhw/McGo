# auth_server.py

import threading
from flask import Flask, request
from markupsafe import escape
from auth import MicrosoftAuthenticator  # 假设 auth 模块已经定义了 MicrosoftAuthenticator

# 创建 Flask 应用
app = Flask(__name__)

# 创建认证器实例（需要传递 client_id 和 redirect_uri）
authenticator = None


def create_authenticator(client_id, redirect_uri):
    """创建 MicrosoftAuthenticator 实例"""
    global authenticator
    authenticator = MicrosoftAuthenticator(client_id, redirect_uri)


def render_callback_page(title, message, status="success", detail=""):
    """Render a small standalone OAuth callback page."""
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


@app.route("/login/callback")
def login_callback():
    """处理微软登录的回调"""
    if authenticator is None:
        return render_callback_page(
            "认证器未配置",
            "启动器还没有初始化 Microsoft 登录流程，请回到 McGo 重新发起登录。",
            status="error",
        ), 500

    error = request.args.get("error")
    if error:
        return render_callback_page(
            "Microsoft 登录失败",
            "Microsoft 返回了错误，请回到 McGo 查看状态日志并重新登录。",
            status="error",
            detail=request.args.get("error_description", error),
        ), 400

    code = request.args.get("code")
    if not code:
        return render_callback_page(
            "缺少授权码",
            "回调地址没有收到授权码，请回到 McGo 重新发起 Microsoft 登录。",
            status="error",
        ), 400

    authenticator.authorization_code = code
    return render_callback_page(
        "登录成功",
        "McGo 已收到 Microsoft 授权码。你可以关闭此页面，回到启动器继续。",
    )


@app.route("/")
def index():
    return render_callback_page(
        "McGo 登录回调服务",
        "这个本地页面用于接收 Microsoft 登录回调。请从 McGo 启动器中发起登录。",
    )


def run_flask_app():
    """启动 Flask 应用，监听回调"""
    app.run(port=5000)


def start_flask_in_thread():
    """在新的线程中启动 Flask"""
    thread = threading.Thread(target=run_flask_app)
    thread.daemon = True
    thread.start()
