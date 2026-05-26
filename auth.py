import asyncio
import requests

from log_utils import get_logger

logger = get_logger(__name__)


class MicrosoftAuthenticator:
    def __init__(self, client_id, redirect_uri):
        self.minecraft_access_token = None
        self.client_id = client_id
        self.redirect_uri = redirect_uri
        self.authorization_code = None
        self.access_token = None
        self.refresh_token = None
        self.xsts_token = None
        self.user_hash = None

    def get_login_url(self):
        return (
            f"https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize"
            f"?client_id={self.client_id}"
            f"&response_type=code"
            f"&redirect_uri={self.redirect_uri}"
            f"&response_mode=query"
            f"&scope=XboxLive.signin%20offline_access"
        )

    async def _exchange_tokens(self):
        """完整的 Xbox Live -> XSTS -> Minecraft token 交换流程"""
        logger.info("Starting Microsoft token exchange")
        url = "https://user.auth.xboxlive.com/user/authenticate"
        data = {
            "Properties": {
                "AuthMethod": "RPS",
                "SiteName": "user.auth.xboxlive.com",
                "RpsTicket": f"d={self.access_token}",
            },
            "RelyingParty": "http://auth.xboxlive.com",
            "TokenType": "JWT",
        }
        headers = {"Content-Type": "application/json", "Accept": "application/json"}
        response = requests.post(url, json=data, headers=headers)
        response.raise_for_status()
        xbl_token = response.json()["Token"]
        self.user_hash = response.json()["DisplayClaims"]["xui"][0]["uhs"]
        logger.debug("Xbox Live token exchange succeeded: has_user_hash=%s", bool(self.user_hash))

        url = "https://xsts.auth.xboxlive.com/xsts/authorize"
        data = {
            "Properties": {"SandboxId": "RETAIL", "UserTokens": [xbl_token]},
            "RelyingParty": "rp://api.minecraftservices.com/",
            "TokenType": "JWT",
        }
        headers = {"Content-Type": "application/json", "Accept": "application/json"}
        response = requests.post(url, json=data, headers=headers)
        response.raise_for_status()
        self.xsts_token = response.json()["Token"]
        logger.debug("XSTS token exchange succeeded")

        url = "https://api.minecraftservices.com/authentication/login_with_xbox"
        headers = {"Content-Type": "application/json"}
        data = {"identityToken": f"XBL3.0 x={self.user_hash};{self.xsts_token}"}
        response = requests.post(url, json=data, headers=headers)
        response.raise_for_status()
        self.minecraft_access_token = response.json()["access_token"]
        logger.info("Minecraft access token exchange succeeded")

    async def authenticate(self):
        if not self.authorization_code:
            raise Exception("请先使用 get_login_url() 获取授权码")

        logger.info("Authenticating with Microsoft authorization code: has_code=%s", bool(self.authorization_code))
        url = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token"
        data = {
            "client_id": self.client_id,
            "code": self.authorization_code,
            "grant_type": "authorization_code",
            "redirect_uri": self.redirect_uri,
            "scope": "XboxLive.signin offline_access",
        }
        headers = {"Content-Type": "application/x-www-form-urlencoded"}
        response = requests.post(url, data=data, headers=headers)
        response.raise_for_status()
        tokens = response.json()
        self.access_token = tokens["access_token"]
        self.refresh_token = tokens["refresh_token"]
        logger.debug("Microsoft OAuth token response succeeded: has_access=%s has_refresh=%s", bool(self.access_token), bool(self.refresh_token))

        await self._exchange_tokens()

    async def get_minecraft_profile(self):
        if not self.minecraft_access_token:
            raise Exception("未登录或 Minecraft access token 不可用")

        logger.info("Fetching Minecraft profile")
        url = "https://api.minecraftservices.com/minecraft/profile"
        headers = {"Authorization": f"Bearer {self.minecraft_access_token}"}
        response = requests.get(url, headers=headers)

        if response.status_code == 200:
            profile = response.json()
            logger.info("Minecraft profile fetched: name=%s uuid=%s", profile.get("name"), profile.get("id"))
            return profile["id"], profile["name"], profile.get("skins", [])
        else:
            logger.warning("Failed to fetch Minecraft profile: status=%s body=%s", response.status_code, response.text[:500])
            raise Exception(f"获取 Minecraft 档案失败: {response.status_code} {response.text}")

    async def refresh_access_token(self, refresh_token):
        if not refresh_token:
            raise Exception("没有可用的刷新令牌")

        logger.info("Refreshing Microsoft access token")
        url = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token"
        data = {
            "client_id": self.client_id,
            "refresh_token": refresh_token,
            "grant_type": "refresh_token",
            "scope": "XboxLive.signin offline_access",
        }
        headers = {"Content-Type": "application/x-www-form-urlencoded"}
        response = requests.post(url, data=data, headers=headers)
        response.raise_for_status()
        tokens = response.json()
        self.access_token = tokens["access_token"]
        self.refresh_token = tokens["refresh_token"]
        logger.debug("Microsoft refresh token response succeeded: has_access=%s has_refresh=%s", bool(self.access_token), bool(self.refresh_token))

        await self._exchange_tokens()
