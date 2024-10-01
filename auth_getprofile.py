import requests

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
        print("Error:", response.json())
        return None

# 假设你已经有了有效的XSTS令牌和用户哈希
xsts_token = "eyJlbmMiOiJBMTI4Q0JDLUhTMjU2IiwiYWxnIjoiUlNBLU9BRVAiLCJjdHkiOiJKV1QiLCJ6aXAiOiJERUYiLCJ4NXQiOiJnN3hlanVTXzRnOXNNdW9fLXdVeFQ5NHNHancifQ.h6FIGpdJF4LBBxe0QepR9e4SvaVCiO9w5qmWaaPDk1Ke-a0cmieVgRzFg2M3glm6oVNjyMAQbcNSjmQNVHUPFT1Mg-hjCGKJp3MdQi7KqdwJM85Mq-Or9ly--zLF2I4EXPFg4b4hH8TuJCAvK2vJGEPfbp_Kf_FCLzMl-qMdpeXmiMDFG2b3vokw1QTpKjgWzxaLjubvxPQGtzrLsob0Qx0arXOB6-BRBuzPrLD1rLB2GgMA6QpM5_OoRyEYhNLHD2o01HL6wJVzGbj5et1pqmRjqKAJVdV2EeX7oK2SD8qL9ER6n6BSXUpbU7fdrCzGlnmLCaZa5JZQ-x-TcXqAIQ.AwAqHSPurdDwaXamvKfGGQ.DI_HJLp8Vxr8Jt4n_IzrvanKZXJz0jeya-LYiGERQYFS0FPW9Y43gkw_sT0BsKLW0RAPjqn9T-mLLQ3X6B_t-wxLQ10alvP6D-tM8R3iudwl9X94-3B94fVNtR3iyqGK9fq_zRgQFj4Yii_Oz1WxwN7jEAkA1uTet8OZAc7PX5lhCwdrdMC1ne_3hcAwoLl0V58G8dHvCWzd6_hNIMHyCmzzvnrscZSNIrWceGp186femjj2AErd03Zx3raeKJnZHvE-Mvj9xvL80-dad5-IvI2A4MJp4gDqjy1sbCDTaMcOSVryzq2kXujlPdtljoM1xba4h8qrYTSjF0hfzBplRPag9WQUsUbfB0UfDfHpQtQjxxAE8MULP5iY3S86R_ZL2TDJXG9BkswUXO8oDDLU6GnQWlz77zvPHGxiAOiDkkR_G49e1ENLuw7IEBl74suGcGfqzDRAuMcLONyXDUbAKijOOPXE7PSidwsIQvWq-Olg7fNPl_Hp7PKH1ZFlKgIsAachjwG1DHxPMYnVz0VKkTXJrV07NUJxmCo9gpQl9rDwclgvTm4LfB45QrgS2HwEcjDB1xEvFAtQNgDpcEAJwsDJzpAwHHZnG0aTa8X6AS8WM0h6FzSrZ0RwGH7X3DSNfxmJU_s0RzQLvnvfwvB_bhzwJyQ1GXjZTO83POFNqwgqeb4gDOuQUhYO5gAqIYhc-JWCiLOhVl12P-Jszh24SshSigqkQKZmHjZOX_cpQTiMrj9GhVmIJoDF0GOqviDSvZJqThias9KCHLNh5gVZvLQjbPKUVV5Qvt4PoXCW2aJ_HgUEMlHiFflGhZMkwJbONtn5G4ADEqRwtNg3lvigeE0TlKXBWKdcdWd-FMgiB_TadsbSR-gcEdMSSQs9UVIbRdUr69sbFDZBPb8-jfJlW2hwTQ8lljsOBMHlMQmLf1NlqzLoB6Q1rQPeq12pvC2BACAe7ZT2zBzzg4_pXNCFVjGjU1FT0ZIZ10vPZRS-P61zKnUFZuLDtf4Nqqb0idfghTPO_hz3lcci0IScQAx_-DUTEZo9KAg2SWn0iImwWvLRRbm-74tP1F8Lc3J0HVMMQFc3eL_mkDP3T_tupEjT15BINWS8BqX-4ZrS_Ta-xHkofZ35LjeTtnmLV_WL6fex_bMu6qWq2iGhVCsR6YIoMQAxSWTrfJ0S-Slck2qtdnmL1mxQWkewlGe2OcF8SQ3grVNwuCtwTZrIkeBH90A6u3nnCB672MJ9FGB3pijAemjBizwzDUAiNQ03SU70YhD5nSMOcmHgFDgd83QpE1bOs4LcqQ2VYoG5yTt827siL5LfBuuqJbSS-5I6CV6C1SoSKQZfYOpSNp4dNDFw4e4tW-PqkETE3uNUxouayfirNhgNTtn9CtHvmKw4KXn6P_XYbC-1-UW9IA5x11VERKwyhR60xD-BVl67mYPKVtqtlOcPvLNC1lvlLe6p7nJLXJEhhpehSwD3Hi4_3MINEUMlJBii8oVMZpJcniwxG60fpi60ha4Co4-qjUr_86UlsG-_vnkZrqOlyvkLp1bFvinmp2aJnY3226ORHmYzLNaw8sKeG__-x3AGt6YAj5fZ21lqSuCCYC0IuWhSxJIXF4pao8AOEQwVzYx0EGMg-w4SP-A.0Oxnj1Z7dpUEBqVElfbxMQ"
uhs = "17471064249590583519"
minecraft_access_token = get_minecraft_access_token(xsts_token, uhs)
import requests

def get_minecraft_profile(access_token):
    url = "https://api.minecraftservices.com/minecraft/profile"
    headers = {
        "Authorization": f"Bearer {access_token}"
    }
    response = requests.get(url, headers=headers)

    if response.status_code == 200:
        profile = response.json()
        print("UUID:", profile["id"])
        print("Username:", profile["name"])
        print("Skins:", profile["skins"])
    else:
        print("Error:", response.json())

# 假设你已经有了一个有效的Minecraft访问令牌

get_minecraft_profile(minecraft_access_token)


