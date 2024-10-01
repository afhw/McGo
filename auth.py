import asyncio
import requests


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

    async def authenticate(self):
        if not self.authorization_code:
            raise Exception("请先使用 get_login_url() 获取授权码")

        # 使用授权码获取访问令牌和刷新令牌
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

        # 使用访问令牌进行 XBox Live 身份验证
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

        # 使用 XBL 令牌进行 XSTS 身份验证
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
        print(self.xsts_token)
        print("\n")
        print(self.user_hash)

        # 获取mc访问令牌


        def get_minecraft_access_token(xsts_token, uhs):
            url = "https://api.minecraftservices.com/authentication/login_with_xbox"
            headers = {
             "Content-Type": "application/json"
         }
            data = {
            "identityToken": f"XBL3.0 x={uhs};{xsts_token}"
            }
            response = requests.post(url, json=data, headers=headers)

            if response.status_code == 200:
                access_token_data = response.json()
                minecraft_access_token = access_token_data["access_token"]
                print("Minecraft Access Token:", minecraft_access_token)
                return minecraft_access_token
            else:
               # print("Minecraft Access Token:", minecraft_access_token)
               print("Error:", response.json())
               return None

        self.minecraft_access_token = get_minecraft_access_token(self.xsts_token, self.user_hash)
        print(self.minecraft_access_token)

    async def get_minecraft_profile(self):
        url = "https://api.minecraftservices.com/minecraft/profile"
        headers = {
            "Authorization": f"Bearer {self.minecraft_access_token}"
        }
        response = requests.get(url, headers=headers)

        if response.status_code == 200:
            profile = response.json()
            print("UUID:", profile["id"])
            print("Username:", profile["name"])
            print("Skins:", profile["skins"])
            return profile["id"], profile["name"], profile["skins"]
        else:
            print("Error:", response.json())
            return None

    async def refresh_access_token(self, refresh_token):
        if not refresh_token:
            raise Exception("没有可用的刷新令牌")

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
