import main as launcher_main
from auth import MicrosoftAuthenticator


app = launcher_main.app


def create_authenticator(client_id, redirect_uri):
    launcher_main.authenticator = MicrosoftAuthenticator(client_id, redirect_uri)
    return launcher_main.authenticator


def render_callback_page(title, message, status="success", detail=""):
    return launcher_main.render_oauth_callback_page(title, message, status=status, detail=detail)


def run_flask_app():
    launcher_main.run_flask_app()


def start_flask_in_thread():
    return launcher_main.ensure_flask_running()
