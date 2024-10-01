# auth_server.py

import threading
from flask import Flask, request
from auth import MicrosoftAuthenticator  # 假设 auth 模块已经定义了 MicrosoftAuthenticator

# 创建 Flask 应用
app = Flask(__name__)

# 创建认证器实例（需要传递 client_id 和 redirect_uri）
authenticator = None


def create_authenticator(client_id, redirect_uri):
    """创建 MicrosoftAuthenticator 实例"""
    global authenticator
    authenticator = MicrosoftAuthenticator(client_id, redirect_uri)


@app.route("/login/callback")
def login_callback():
    """处理微软登录的回调"""
    # 获取授权码
    if authenticator is None:
        return "未配置认证器", 500
    authenticator.authorization_code = request.args.get("code")
    return "登录成功，你可以关闭此窗口"


def run_flask_app():
    """启动 Flask 应用，监听回调"""
    app.run(port=5000)


def start_flask_in_thread():
    """在新的线程中启动 Flask"""
    thread = threading.Thread(target=run_flask_app)
    thread.daemon = True
    thread.start()