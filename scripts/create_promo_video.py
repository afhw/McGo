from __future__ import annotations

import math
import os
import shutil
import subprocess
import wave
from functools import lru_cache
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont


ROOT = Path(__file__).resolve().parents[1]
OUT_DIR = ROOT / "dist" / "promo"
OUT_VIDEO = OUT_DIR / "mcgo-bilibili-promo.mp4"
OUT_COVER = OUT_DIR / "mcgo-bilibili-cover.png"
OUT_COPY = OUT_DIR / "mcgo-bilibili-copy.txt"
OUT_SRT = OUT_DIR / "mcgo-bilibili-subtitles.srt"
OUT_VOICEOVER = OUT_DIR / "mcgo-bilibili-voiceover.wav"
OUT_AI_VOICEOVER = OUT_DIR / "mcgo-bilibili-ai-voiceover.mp3"
TEMP_MUSIC = OUT_DIR / "mcgo-promo-bed.wav"
VOICEOVER_TEXT = OUT_DIR / "mcgo-bilibili-voiceover.txt"

WIDTH = 1920
HEIGHT = 1080
FPS = 30
DURATION = 74.0

FONT_REGULAR = Path("C:/Windows/Fonts/msyh.ttc")
FONT_BOLD = Path("C:/Windows/Fonts/msyhbd.ttc")


def font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont:
    path = FONT_BOLD if bold and FONT_BOLD.exists() else FONT_REGULAR
    if not path.exists():
        path = Path("C:/Windows/Fonts/simhei.ttf")
    return ImageFont.truetype(str(path), size=size)


F_TITLE = font(94, True)
F_SUBTITLE = font(44)
F_H1 = font(70, True)
F_H2 = font(48, True)
F_BODY = font(34)
F_SMALL = font(27)
F_TINY = font(22)

VIDEO_TITLE = "McGo：把下载、账号、资源和联机放进一个 Minecraft 启动器"
VIDEO_DESC = (
    "McGo 是基于 PyQt6 和 QFluentWidgets 的 Minecraft 启动器，支持账号管理、游戏下载、"
    "加载器安装、资源市场、整合包导入导出、版本独立设置和 P2P 联机入口。"
)
VOICE_SCRIPT = (
    "如果你经常为了启动 Minecraft 来回切工具，McGo 想把这些流程集中起来。"
    "它是一个 Fluent 风格的 Minecraft 启动器，首页会汇总账号、Java、版本和游戏目录。"
    "在启动页，你可以选择账号、本地版本，并管理当前版本的独立设置。"
    "下载页支持原版版本、镜像源和下载预设，也可以在下载后继续安装 Fabric、Forge、NeoForge、OptiFine 或 Fabric API。"
    "资源市场用于搜索 Mod、资源包、光影和数据包，并配合整合包导入导出，让本地实例更容易整理。"
    "账号方面，McGo 支持离线账号、Microsoft 登录和外置登录，同时管理 Java 路径、游戏目录和界面偏好。"
    "需要和朋友联机时，McGo 提供 P2P 隧道入口，通过轻量中继连接局域网联机流量。"
    "如果你想要一个更顺手、更集中的 Minecraft 启动体验，可以试试 McGo。"
)


def ease(x: float) -> float:
    x = max(0.0, min(1.0, x))
    return x * x * (3 - 2 * x)


def out_back(x: float) -> float:
    x = max(0.0, min(1.0, x)) - 1.0
    c1 = 1.70158
    c3 = c1 + 1.0
    return 1.0 + c3 * x * x * x + c1 * x * x


def lerp(a: float, b: float, x: float) -> float:
    return a + (b - a) * x


def draw_round(draw: ImageDraw.ImageDraw, box, radius, fill, outline=None, width=1):
    draw.rounded_rectangle(box, radius=radius, fill=fill, outline=outline, width=width)


def text_size(draw: ImageDraw.ImageDraw, text: str, fnt: ImageFont.FreeTypeFont) -> tuple[int, int]:
    box = draw.textbbox((0, 0), text, font=fnt)
    return box[2] - box[0], box[3] - box[1]


def draw_text(draw: ImageDraw.ImageDraw, xy, text: str, fnt, fill, anchor=None):
    draw.text(xy, text, font=fnt, fill=fill, anchor=anchor)


def wrap(draw: ImageDraw.ImageDraw, text: str, fnt, max_width: int) -> list[str]:
    lines: list[str] = []
    line = ""
    for ch in text:
        test = line + ch
        if text_size(draw, test, fnt)[0] <= max_width or not line:
            line = test
        else:
            lines.append(line)
            line = ch
    if line:
        lines.append(line)
    return lines


def fit_cover(img: Image.Image, size: tuple[int, int]) -> Image.Image:
    iw, ih = img.size
    sw, sh = size
    scale = max(sw / iw, sh / ih)
    nw, nh = int(iw * scale), int(ih * scale)
    img = img.resize((nw, nh), Image.Resampling.LANCZOS)
    return img.crop(((nw - sw) // 2, (nh - sh) // 2, (nw + sw) // 2, (nh + sh) // 2))


def fit_contain(img: Image.Image, size: tuple[int, int]) -> Image.Image:
    iw, ih = img.size
    sw, sh = size
    scale = min(sw / iw, sh / ih)
    nw, nh = int(iw * scale), int(ih * scale)
    return img.resize((nw, nh), Image.Resampling.LANCZOS)


def load_assets() -> tuple[Image.Image, Image.Image, Image.Image]:
    icon = Image.open(ROOT / "assets" / "mcgo-icon.png").convert("RGBA")

    screenshot_path = ROOT / "屏幕截图 2026-05-30 185207.png"
    full = Image.open(screenshot_path).convert("RGB")
    # Crop the visible McGo application window out of the desktop screenshot.
    window = full.crop((468, 92, 2148, 1235))
    return icon, full, window


ICON, FULL_SCREENSHOT, WINDOW_SCREENSHOT = load_assets()


def load_promo_screenshots() -> dict[str, Image.Image]:
    shots_dir = OUT_DIR / "screenshots"
    mapping = {
        "home": "01-home.png",
        "launch": "02-launch.png",
        "download": "03-download.png",
        "resources": "04-resources.png",
        "accounts": "05-accounts.png",
        "online": "06-online.png",
    }
    shots: dict[str, Image.Image] = {}
    for key, filename in mapping.items():
        path = shots_dir / filename
        if path.exists():
            shots[key] = Image.open(path).convert("RGB")
    return shots


PROMO_SCREENSHOTS = load_promo_screenshots()


def make_static_background() -> Image.Image:
    img = Image.new("RGB", (WIDTH, HEIGHT), "#111314")
    d = ImageDraw.Draw(img)
    for y in range(HEIGHT):
        k = y / HEIGHT
        r = int(16 + 8 * k)
        g = int(19 + 18 * k)
        b = int(21 + 20 * k)
        d.line((0, y, WIDTH, y), fill=(r, g, b))
    layer = Image.new("RGBA", (WIDTH, HEIGHT), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    d.ellipse((1120, -240, 2160, 700), fill=(42, 170, 143, 38))
    d.ellipse((-240, 680, 560, 1320), fill=(70, 100, 180, 30))
    for y in range(160, HEIGHT, 110):
        d.line((0, y, WIDTH, y + 40), fill=(255, 255, 255, 8), width=1)
    return Image.alpha_composite(img.convert("RGBA"), layer)


BASE_BACKGROUND = make_static_background()


def background(t: float) -> Image.Image:
    img = BASE_BACKGROUND.copy()
    d = ImageDraw.Draw(img)
    for x in range(-420, WIDTH + 420, 70):
        alpha = 18 if x % 210 == 0 else 7
        drift = int((t * 58 + 14 * math.sin(t * 0.9)) % 140)
        d.line((x + drift, 0, x - 330 + drift, HEIGHT), fill=(68, 240, 224, alpha), width=1)
    for i in range(9):
        px = int((i * 233 + t * (62 + i * 3)) % (WIDTH + 260)) - 130
        py = int((i * 97 + 80 * math.sin(t * 0.7 + i)) % HEIGHT)
        d.line((px, py, px + 72, py - 22), fill=(76, 242, 226, 18), width=1)
    return img


def paste_shadow(base: Image.Image, img: Image.Image, xy: tuple[int, int], radius=32, offset=(0, 18), alpha=110):
    x, y = xy
    shadow = Image.new("RGBA", img.size, (0, 0, 0, 0))
    shadow_draw = ImageDraw.Draw(shadow)
    shadow_draw.rounded_rectangle((0, 0, img.size[0], img.size[1]), 28, fill=(0, 0, 0, alpha))
    shadow = shadow.filter(ImageFilter.GaussianBlur(radius))
    base.alpha_composite(shadow, (x + offset[0], y + offset[1]))
    base.alpha_composite(img, xy)


def motion_xy(x: int, y: int, local_t: float, dx: int = 180, dy: int = 0, wobble: int = 5) -> tuple[int, int]:
    intro = ease(local_t / 0.95)
    shake = max(0.0, 1.0 - local_t / 1.25)
    dx = int(dx * 0.48)
    dy = int(dy * 0.48)
    wobble = max(1, int(wobble * 0.28))
    return (
        x + int(dx * (1.0 - intro) + math.sin(local_t * 8.0) * wobble * shake),
        y + int(dy * (1.0 - intro) + math.cos(local_t * 7.0) * wobble * shake),
    )


def animated_card(card: Image.Image, local_t: float, base_scale: float = 1.0) -> Image.Image:
    intro = ease(local_t / 0.95)
    pulse = 0.004 * math.sin(local_t * 1.5)
    scale = base_scale * (0.96 + 0.04 * intro + pulse)
    if abs(scale - 1.0) < 0.006:
        return card
    w, h = card.size
    nw, nh = max(1, int(w * scale)), max(1, int(h * scale))
    return card.resize((nw, nh), Image.Resampling.LANCZOS)


def paste_motion_card(base: Image.Image, card: Image.Image, xy: tuple[int, int], local_t: float, dx: int = 180, dy: int = 0):
    moved = animated_card(card, local_t)
    x, y = motion_xy(xy[0], xy[1], local_t, dx=dx, dy=dy)
    x -= (moved.size[0] - card.size[0]) // 2
    y -= (moved.size[1] - card.size[1]) // 2
    paste_shadow(base, moved, (x, y), radius=26, offset=(0, 22), alpha=125)
    return x, y, moved.size[0], moved.size[1]


def draw_scan(base: Image.Image, box: tuple[int, int, int, int], local_t: float):
    x, y, w, h = box
    if w <= 0 or h <= 0:
        return
    layer = Image.new("RGBA", (WIDTH, HEIGHT), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    pos = int((local_t * 230) % (w + 360)) - 180
    d.polygon(
        [(x + pos, y), (x + pos + 120, y), (x + pos - 120, y + h), (x + pos - 240, y + h)],
        fill=(82, 245, 228, 20),
    )
    d.line((x, y + int((local_t * 36) % h), x + w, y + int((local_t * 36) % h)), fill=(82, 245, 228, 34), width=1)
    base.alpha_composite(layer)


def draw_kicker(draw: ImageDraw.ImageDraw, local_t: float, y: int = 100):
    width = int(460 * ease(local_t / 0.5))
    if width:
        draw.rounded_rectangle((120, y, 120 + width, y + 8), radius=4, fill=(55, 229, 214, 235))


def draw_transition(base: Image.Image, t: float):
    for start, _, _ in SCENES[1:]:
        dt = t - start
        if 0.0 <= dt <= 0.72:
            p = ease(dt / 0.72)
            layer = Image.new("RGBA", (WIDTH, HEIGHT), (0, 0, 0, 0))
            d = ImageDraw.Draw(layer)
            x = int(lerp(-WIDTH * 0.35, WIDTH * 1.05, p))
            d.polygon([(x - 300, 0), (x + 260, 0), (x - 30, HEIGHT), (x - 590, HEIGHT)], fill=(55, 229, 214, 45))
            d.polygon([(x - 520, 0), (x - 360, 0), (x - 650, HEIGHT), (x - 810, HEIGHT)], fill=(255, 255, 255, 20))
            base.alpha_composite(layer)
            break


@lru_cache(maxsize=16)
def make_window_card(max_size: tuple[int, int], zoom_key: int = 100) -> Image.Image:
    return make_image_card("legacy", max_size, zoom_key)


@lru_cache(maxsize=32)
def make_image_card(source_key: str, max_size: tuple[int, int], zoom_key: int = 100) -> Image.Image:
    source = PROMO_SCREENSHOTS.get(source_key, WINDOW_SCREENSHOT)
    zoom = zoom_key / 100
    img = fit_contain(source, max_size)
    if zoom > 1:
        iw, ih = img.size
        nw, nh = int(iw / zoom), int(ih / zoom)
        left = (iw - nw) // 2
        top = int((ih - nh) * 0.45)
        img = img.crop((left, top, left + nw, top + nh)).resize((iw, ih), Image.Resampling.LANCZOS)
    card = Image.new("RGBA", (img.size[0] + 8, img.size[1] + 8), (0, 0, 0, 0))
    d = ImageDraw.Draw(card)
    draw_round(d, (0, 0, card.size[0] - 1, card.size[1] - 1), 30, (38, 43, 43, 255), (95, 110, 110, 150), 2)
    card.alpha_composite(img.convert("RGBA"), (4, 4))
    mask = Image.new("L", img.size, 0)
    md = ImageDraw.Draw(mask)
    md.rounded_rectangle((0, 0, img.size[0], img.size[1]), 26, fill=255)
    rounded = Image.new("RGBA", img.size, (0, 0, 0, 0))
    rounded.alpha_composite(img.convert("RGBA"))
    rounded.putalpha(mask)
    card.alpha_composite(rounded, (4, 4))
    return card


def draw_chip(draw: ImageDraw.ImageDraw, x: int, y: int, text: str, accent=(55, 229, 214)):
    w, h = text_size(draw, text, F_SMALL)
    box = (x, y, x + w + 42, y + 48)
    draw_round(draw, box, 16, (29, 36, 37, 230), (accent[0], accent[1], accent[2], 100), 1)
    draw.ellipse((x + 18, y + 18, x + 28, y + 28), fill=accent)
    draw_text(draw, (x + 34, y + 10), text, F_SMALL, (230, 242, 240, 255))


def draw_motion_chip(draw: ImageDraw.ImageDraw, x: int, y: int, text: str, local_t: float, delay: float = 0.0):
    p = ease((local_t - delay) / 0.62)
    if p <= 0:
        return
    draw_chip(draw, x + int(-58 * (1 - p)), y, text)


def render_title(base: Image.Image, local_t: float):
    d = ImageDraw.Draw(base)
    fade = ease(min(local_t / 0.7, 1))
    if "home" in PROMO_SCREENSHOTS:
        card = make_image_card("home", (850, 560), 100)
        card = animated_card(card, local_t, 0.92)
        card = card.copy()
        card.putalpha(120)
        cx, cy = motion_xy(940, 250, local_t, dx=260, dy=60, wobble=8)
        base.alpha_composite(card, (cx, cy))
        draw_scan(base, (cx, cy, card.size[0], card.size[1]), local_t)
    draw_kicker(d, local_t)
    icon_size = int(170 + 5 * math.sin(local_t * 2.0))
    icon = ICON.resize((icon_size, icon_size), Image.Resampling.LANCZOS)
    x = int(260 - 140 * (1 - out_back(local_t / 0.7)))
    y = 310
    base.alpha_composite(icon, (x, y))
    draw_text(d, (x + 230, y + 8), "McGo", F_TITLE, (245, 249, 246, int(255 * fade)))
    draw_text(d, (x + 236, y + 130), "Minecraft 启动，从下载到联机一次完成", F_SUBTITLE, (196, 231, 224, int(230 * fade)))
    draw_motion_chip(d, x + 236, y + 226, "账号管理", local_t, 0.18)
    draw_motion_chip(d, x + 430, y + 226, "游戏下载", local_t, 0.28)
    draw_motion_chip(d, x + 632, y + 226, "资源与整合包", local_t, 0.38)
    draw_motion_chip(d, x + 902, y + 226, "P2P 联机", local_t, 0.48)


def render_home_scene(base: Image.Image, local_t: float):
    d = ImageDraw.Draw(base)
    card = make_image_card("home", (1120, 760), 100)
    box = paste_motion_card(base, card, (690, 185), local_t, dx=260, dy=0)
    draw_scan(base, box, local_t)
    draw_kicker(d, local_t)
    tx = 120 + int(-160 * (1 - out_back(local_t / 0.62)))
    draw_text(d, (tx, 150), "先看清当前状态", F_H1, (248, 250, 248, 255))
    draw_text(d, (tx + 4, 248), "首页汇总账号、Java、版本与游戏目录。", F_BODY, (205, 222, 218, 235))
    for i, line in enumerate(["账号", "Java", "本地版本", "远程版本", "游戏目录"]):
        draw_motion_chip(d, 128, 365 + i * 74, line, local_t, 0.12 + i * 0.07)
    draw_text(d, (128, 790), "把启动前的准备信息放在第一屏。", F_BODY, (204, 222, 218, 235))


def render_launch_scene(base: Image.Image, local_t: float):
    d = ImageDraw.Draw(base)
    card = make_image_card("launch", (1160, 800), 100)
    box = paste_motion_card(base, card, (650, 150), local_t, dx=250, dy=-30)
    draw_scan(base, box, local_t)
    draw_kicker(d, local_t)
    tx = 120 + int(-160 * (1 - out_back(local_t / 0.62)))
    draw_text(d, (tx, 150), "启动与版本设置", F_H1, (248, 250, 248, 255))
    draw_text(d, (tx + 4, 248), "选择账号和本地版本，再按需调整当前实例。", F_BODY, (205, 222, 218, 235))
    lines = ["启动 Minecraft", "版本分类", "内存与窗口", "JVM / 游戏参数", "补全与校验"]
    for i, line in enumerate(lines):
        draw_motion_chip(d, 128, 365 + i * 74, line, local_t, 0.12 + i * 0.07)


def render_screenshot_scene(base: Image.Image, local_t: float):
    d = ImageDraw.Draw(base)
    card = make_image_card("download", (1220, 830), 100)
    x = 590
    y = 145
    box = paste_motion_card(base, card, (x, y), local_t, dx=300, dy=0)
    draw_scan(base, box, local_t)
    draw_kicker(d, local_t, 120)
    tx = 120 + int(-170 * (1 - out_back(local_t / 0.62)))
    draw_text(d, (tx, 168), "游戏下载更省心", F_H1, (248, 250, 248, 255))
    lines = ["原版下载", "Fabric / Forge / NeoForge", "OptiFine 与 Fabric API", "校验、补全、重试队列"]
    yy = 292
    for i, line in enumerate(lines):
        draw_motion_chip(d, 128, yy + i * 76, line, local_t, 0.12 + i * 0.08)
    for i, line in enumerate(wrap(d, "选择版本、镜像源和安装项，减少重复配置。", F_BODY, 410)):
        draw_text(d, (128, 650 + i * 48), line, F_BODY, (201, 219, 214, 235))


def render_accounts_scene(base: Image.Image, local_t: float):
    d = ImageDraw.Draw(base)
    card = make_image_card("accounts", (1040, 710), 100)
    box = paste_motion_card(base, card, (785, 245), local_t, dx=300, dy=40)
    draw_scan(base, box, local_t)
    draw_kicker(d, local_t)
    tx = 125 + int(-170 * (1 - out_back(local_t / 0.62)))
    draw_text(d, (tx, 140), "账号与环境，一处管理", F_H1, (248, 250, 248, 255))
    draw_text(d, (tx + 3, 238), "离线、Microsoft、外置登录，自动刷新状态并注入所需参数。", F_BODY, (205, 222, 218, 235))
    labels = [
        ("离线账号", "快速添加本地用户名，适合测试与单机。"),
        ("Microsoft 登录", "启动前刷新登录状态，账号信息本地保存。"),
        ("外置登录", "支持 Yggdrasil / authlib-injector 服务。"),
    ]
    for i, (title, desc) in enumerate(labels):
        x = 130
        p = out_back((local_t - 0.12 - i * 0.09) / 0.55)
        y = 388 + i * 160
        x = x + int(-130 * (1 - p))
        draw_round(d, (x, y, x + 520, y + 120), 22, (32, 38, 39, 230), (80, 234, 217, 90), 1)
        d.ellipse((x + 34, y + 38, x + 94, y + 98), fill=(52, 223, 208, 255))
        draw_text(d, (x + 124, y + 34), title, F_H2, (246, 250, 247, 255))
        draw_text(d, (x + 124, y + 88), desc, F_TINY, (205, 220, 216, 235))
    draw_chip(d, 132, 790, "Java 路径")
    draw_chip(d, 340, 790, "游戏目录")
    draw_chip(d, 548, 790, "版本独立设置")


def render_resources_scene(base: Image.Image, local_t: float):
    d = ImageDraw.Draw(base)
    card = make_image_card("resources", (980, 690), 100)
    box = paste_motion_card(base, card, (105, 240), local_t, dx=-260, dy=20)
    draw_scan(base, box, local_t)
    draw_kicker(d, local_t, 140)
    tx = 1140 + int(190 * (1 - out_back(local_t / 0.62)))
    draw_text(d, (tx, 190), "资源市场与整合包", F_H1, (248, 250, 248, 255))
    copy = [
        "Modrinth / CurseForge / 本地资源",
        "Mod、资源包、光影、数据包",
        "整合包导入与导出",
        "启用、禁用、删除 Mod",
    ]
    yy = 320
    for i, line in enumerate(copy):
        draw_motion_chip(d, 1145, yy, line, local_t, 0.12 + i * 0.08)
        yy += 82
    draw_text(d, (1148, 705), "从找资源到整理实例，尽量留在一个启动器里完成。", F_BODY, (204, 222, 218, 235))


def render_p2p_scene(base: Image.Image, local_t: float):
    d = ImageDraw.Draw(base)
    card = make_image_card("online", (1040, 730), 100)
    box = paste_motion_card(base, card, (760, 225), local_t, dx=310, dy=0)
    draw_scan(base, box, local_t)
    draw_kicker(d, local_t)
    tx = 126 + int(-170 * (1 - out_back(local_t / 0.62)))
    draw_text(d, (tx, 150), "好友联机，不止局域网", F_H1, (248, 250, 248, 255))
    draw_text(d, (tx + 4, 248), "轻量 TCP 中继打通 Minecraft Java 版 LAN 流量。", F_BODY, (206, 223, 218, 238))
    nodes = [("房主", 245, 458), ("中继", 485, 458), ("加入者", 245, 645), ("本地端口", 485, 645)]
    for a, b in ((0, 1), (1, 2), (1, 3)):
        _, x1, y1 = nodes[a]
        _, x2, y2 = nodes[b]
        p = ease(min(max((local_t - 0.25) / 0.8, 0), 1))
        d.line((x1, y1, int(lerp(x1, x2, p)), int(lerp(y1, y2, p))), fill=(70, 218, 204, 160), width=5)
    for name, x, y in nodes:
        draw_round(d, (x - 96, y - 48, x + 96, y + 48), 22, (31, 38, 40, 245), (72, 229, 213, 120), 2)
        draw_text(d, (x, y), name, F_SMALL, (245, 250, 247, 255), anchor="mm")
    draw_chip(d, 128, 830, "房间号")
    draw_chip(d, 330, 830, "可选口令")
    draw_chip(d, 558, 830, "本地端口")


def render_final(base: Image.Image, local_t: float):
    d = ImageDraw.Draw(base)
    p = out_back(local_t / 0.75)
    icon_size = int(148 + 5 * math.sin(local_t * 2.0))
    icon = ICON.resize((icon_size, icon_size), Image.Resampling.LANCZOS)
    base.alpha_composite(icon, (302 + int(-140 * (1 - p)), 308))
    draw_kicker(d, local_t, 250)
    draw_text(d, (492 + int(-160 * (1 - p)), 292), "McGo", F_TITLE, (248, 250, 248, 255))
    draw_text(d, (498 + int(-160 * (1 - p)), 422), "更顺手的 Minecraft 启动器", F_SUBTITLE, (196, 232, 225, 255))
    draw_text(d, (310, 585), "账号、下载、资源、整合包、联机", F_H2, (244, 249, 246, 255))
    draw_text(d, (314, 660), "把常用流程集中起来，少折腾，多开玩。", F_BODY, (206, 223, 218, 235))
    draw_round(d, (314, 765, 742, 840), 20, (48, 231, 214, 255))
    draw_text(d, (528, 802), "立即体验 McGo", F_BODY, (7, 28, 28, 255), anchor="mm")


SCENES = [
    (0.0, 6.0, render_title),
    (6.0, 15.0, render_home_scene),
    (15.0, 25.0, render_launch_scene),
    (25.0, 38.0, render_screenshot_scene),
    (38.0, 50.0, render_resources_scene),
    (50.0, 61.0, render_accounts_scene),
    (61.0, 70.0, render_p2p_scene),
    (70.0, 74.0, render_final),
]


SUBTITLES = [
    (0.0, 3.8, "如果你经常为了启动 Minecraft 来回切工具，"),
    (3.8, 6.4, "McGo 想把这些流程集中起来。"),
    (6.4, 9.9, "它是一个 Fluent 风格的 Minecraft 启动器，"),
    (9.9, 13.7, "首页会汇总账号、Java、版本和游戏目录。"),
    (13.7, 17.3, "在启动页，你可以选择账号、本地版本，"),
    (17.3, 20.1, "并管理当前版本的独立设置。"),
    (20.1, 24.2, "下载页支持原版版本、镜像源和下载预设，"),
    (24.2, 30.1, "也可以在下载后继续安装 Fabric、Forge、NeoForge、OptiFine 或 Fabric API。"),
    (30.1, 34.5, "资源市场用于搜索 Mod、资源包、光影和数据包，"),
    (34.5, 39.1, "并配合整合包导入导出，让本地实例更容易整理。"),
    (39.1, 44.2, "账号方面，McGo 支持离线账号、Microsoft 登录和外置登录，"),
    (44.2, 48.1, "同时管理 Java 路径、游戏目录和界面偏好。"),
    (48.1, 52.0, "需要和朋友联机时，McGo 提供 P2P 隧道入口，"),
    (52.0, 55.4, "通过轻量中继连接局域网联机流量。"),
    (55.4, 59.0, "如果你想要一个更顺手、更集中的 Minecraft 启动体验，"),
    (59.0, 60.384, "可以试试 McGo。"),
]


def draw_subtitle(base: Image.Image, t: float):
    text = ""
    for start, end, candidate in SUBTITLES:
        if start <= t < end:
            text = candidate
            break
    if not text:
        return
    d = ImageDraw.Draw(base)
    lines = wrap(d, text, F_SMALL, 1360)
    line_height = 40
    box_h = 42 + len(lines) * line_height
    x1, y1, x2, y2 = 250, HEIGHT - box_h - 46, WIDTH - 250, HEIGHT - 46
    draw_round(d, (x1, y1, x2, y2), 22, (7, 14, 15, 195), (74, 226, 211, 90), 1)
    for i, line in enumerate(lines):
        draw_text(d, (WIDTH // 2, y1 + 34 + i * line_height), line, F_SMALL, (238, 248, 245, 255), anchor="mm")


def render_frame(frame_no: int) -> Image.Image:
    t = frame_no / FPS
    base = background(t)
    for start, end, renderer in SCENES:
        if start <= t < end or (frame_no == int(DURATION * FPS) - 1 and end == DURATION):
            local_t = t - start
            renderer(base, local_t)
            break
    draw_transition(base, t)
    draw_subtitle(base, t)
    # Subtle fade in/out.
    alpha = 1.0
    if t < 0.6:
        alpha = ease(t / 0.6)
    elif DURATION - t < 0.7:
        alpha = ease((DURATION - t) / 0.7)
    if alpha < 1.0:
        overlay = Image.new("RGBA", (WIDTH, HEIGHT), (0, 0, 0, int(255 * (1 - alpha))))
        base = Image.alpha_composite(base, overlay)
    return base.convert("RGB")


def write_music_bed(path: Path):
    sample_rate = 44100
    total = int(DURATION * sample_rate)
    chords = [
        (110.00, 164.81, 220.00, 329.63),
        (98.00, 146.83, 196.00, 293.66),
        (130.81, 196.00, 261.63, 392.00),
        (87.31, 130.81, 174.61, 261.63),
    ]
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(2)
        wav.setsampwidth(2)
        wav.setframerate(sample_rate)
        frames = bytearray()
        for i in range(total):
            t = i / sample_rate
            chord = chords[int(t // 3.5) % len(chords)]
            beat = 0.74 + 0.26 * (0.5 + 0.5 * math.sin(2 * math.pi * 1.5 * t))
            env = min(1.0, t / 1.5, (DURATION - t) / 1.2)
            sample = 0.0
            for idx, freq in enumerate(chord):
                sample += math.sin(2 * math.pi * freq * t) * (0.10 / (idx + 1))
                sample += math.sin(2 * math.pi * freq * 2 * t) * (0.025 / (idx + 1))
            shimmer = math.sin(2 * math.pi * 659.25 * t) * 0.018 * (0.5 + 0.5 * math.sin(2 * math.pi * 0.25 * t))
            value = int(max(-1.0, min(1.0, (sample + shimmer) * beat * env)) * 32767)
            frames.extend(value.to_bytes(2, "little", signed=True))
            frames.extend(value.to_bytes(2, "little", signed=True))
        wav.writeframes(frames)


def write_voiceover() -> Path | None:
    VOICEOVER_TEXT.write_text(VOICE_SCRIPT, encoding="utf-8")
    ai_voiceover = write_openai_compatible_voiceover()
    if ai_voiceover:
        return ai_voiceover
    if OUT_AI_VOICEOVER.exists() and OUT_AI_VOICEOVER.stat().st_size > 0:
        return OUT_AI_VOICEOVER

    powershell = shutil.which("powershell")
    if not powershell:
        return None
    ps = f"""
Add-Type -AssemblyName System.Speech
$text = Get-Content -Raw -Encoding UTF8 '{VOICEOVER_TEXT}'
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$voice = $synth.GetInstalledVoices() | Where-Object {{ $_.VoiceInfo.Culture.Name -eq 'zh-CN' }} | Select-Object -First 1
if ($voice -ne $null) {{ $synth.SelectVoice($voice.VoiceInfo.Name) }}
$synth.Rate = 1
$synth.Volume = 96
$synth.SetOutputToWaveFile('{OUT_VOICEOVER}')
$synth.Speak($text)
$synth.Dispose()
"""
    result = subprocess.run([powershell, "-NoProfile", "-Command", ps], capture_output=True, text=True)
    if result.returncode != 0 or not OUT_VOICEOVER.exists():
        return None
    return OUT_VOICEOVER


def write_openai_compatible_voiceover() -> Path | None:
    api_key = os.environ.get("OPENAI_API_KEY", "").strip()
    if not api_key:
        return None
    base_url = os.environ.get("OPENAI_BASE_URL", "https://api.openai.com/v1").strip().rstrip("/")
    model = os.environ.get("OPENAI_TTS_MODEL", "tts-1").strip() or "tts-1"
    voice = os.environ.get("OPENAI_TTS_VOICE", "alloy").strip() or "alloy"
    endpoint = f"{base_url}/audio/speech"
    payload = {
        "model": model,
        "voice": voice,
        "input": VOICE_SCRIPT,
        "response_format": "mp3",
        "speed": 1.0,
    }
    try:
        import requests

        response = requests.post(
            endpoint,
            headers={
                "Authorization": f"Bearer {api_key}",
                "Content-Type": "application/json",
            },
            json=payload,
            timeout=120,
        )
        if response.status_code >= 400:
            message = response.text[:500].replace("\n", " ")
            print(f"OpenAI-compatible TTS failed: HTTP {response.status_code} {message}")
            return None
        OUT_AI_VOICEOVER.write_bytes(response.content)
        return OUT_AI_VOICEOVER if OUT_AI_VOICEOVER.exists() and OUT_AI_VOICEOVER.stat().st_size > 0 else None
    except Exception as exc:
        print(f"OpenAI-compatible TTS failed: {exc}")
        return None


def srt_timestamp(seconds: float) -> str:
    millis = int(round(seconds * 1000))
    hours = millis // 3_600_000
    millis %= 3_600_000
    minutes = millis // 60_000
    millis %= 60_000
    secs = millis // 1000
    millis %= 1000
    return f"{hours:02d}:{minutes:02d}:{secs:02d},{millis:03d}"


def write_srt():
    parts = []
    for index, (start, end, text) in enumerate(SUBTITLES, start=1):
        parts.append(str(index))
        parts.append(f"{srt_timestamp(start)} --> {srt_timestamp(end)}")
        parts.append(text)
        parts.append("")
    OUT_SRT.write_text("\n".join(parts), encoding="utf-8-sig")


def make_cover() -> Image.Image:
    base = background(0.0)
    d = ImageDraw.Draw(base)
    icon = ICON.resize((150, 150), Image.Resampling.LANCZOS)
    base.alpha_composite(icon, (150, 154))
    draw_text(d, (330, 150), "McGo", F_TITLE, (248, 250, 248, 255))
    draw_text(d, (335, 285), "更顺手的 Minecraft 启动器", F_SUBTITLE, (194, 235, 226, 255))
    draw_text(d, (155, 450), "下载 / 账号 / 资源 / 整合包 / 联机", F_H2, (246, 250, 247, 255))
    draw_text(d, (160, 535), "把 Minecraft 常用流程集中起来", F_BODY, (207, 224, 219, 245))
    if "download" in PROMO_SCREENSHOTS:
        card_a = make_image_card("download", (760, 520), 100)
        paste_shadow(base, card_a, (1040, 155), radius=24, offset=(0, 18), alpha=110)
    if "online" in PROMO_SCREENSHOTS:
        card_b = make_image_card("online", (660, 430), 100)
        paste_shadow(base, card_b, (880, 510), radius=22, offset=(0, 16), alpha=105)
    draw_round(d, (160, 710, 700, 790), 22, (49, 232, 215, 255))
    draw_text(d, (430, 750), "立即体验 McGo", F_BODY, (5, 29, 28, 255), anchor="mm")
    return base.convert("RGB")


def write_copy():
    OUT_COPY.write_text(
        f"""McGo 发布素材

标题建议：
{VIDEO_TITLE}

简介建议：
{VIDEO_DESC}

分区建议：
单机游戏 / Minecraft / 工具分享 / 软件推荐

标签建议：
Minecraft, 我的世界, 启动器, McGo, Mod, 整合包, P2P联机, PyQt6

置顶评论建议：
视频画面使用项目自动化截图生成，账号与服务器信息均为演示数据。

字幕文件：
mcgo-bilibili-subtitles.srt

配音文案：
{VOICE_SCRIPT}

视频规格：
1920x1080 / 30fps / H.264 MP4 / AAC / 约 74 秒
""",
        encoding="utf-8",
    )


def main():
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    write_music_bed(TEMP_MUSIC)
    voiceover = write_voiceover()
    write_srt()
    write_copy()
    cover = make_cover()
    cover.save(OUT_COVER)

    ffmpeg = shutil.which("ffmpeg")
    if not ffmpeg:
        raise RuntimeError("ffmpeg was not found in PATH")

    cmd = [
        ffmpeg,
        "-y",
        "-f",
        "rawvideo",
        "-pix_fmt",
        "rgb24",
        "-s",
        f"{WIDTH}x{HEIGHT}",
        "-r",
        str(FPS),
        "-i",
        "-",
        "-i",
        str(TEMP_MUSIC),
    ]
    if voiceover:
        cmd += ["-i", str(voiceover)]
    cmd += [
        "-c:v",
        "libx264",
        "-preset",
        "medium",
        "-crf",
        "18",
        "-pix_fmt",
        "yuv420p",
    ]
    if voiceover:
        cmd += [
            "-filter_complex",
            "[1:a]volume=0.16[music];[2:a]volume=1.0[voice];[music][voice]amix=inputs=2:duration=longest:dropout_transition=2[a]",
            "-map",
            "0:v",
            "-map",
            "[a]",
        ]
    cmd += [
        "-c:a",
        "aac",
        "-b:a",
        "160k",
        "-shortest",
        str(OUT_VIDEO),
    ]
    proc = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    assert proc.stdin is not None
    total_frames = int(DURATION * FPS)
    for frame_no in range(total_frames):
        frame = render_frame(frame_no)
        proc.stdin.write(frame.tobytes())
    proc.stdin.close()
    ret = proc.wait()
    if ret != 0:
        raise RuntimeError(f"ffmpeg failed with exit code {ret}")
    print(OUT_VIDEO)
    print(OUT_COVER)
    print(OUT_COPY)
    print(OUT_SRT)


if __name__ == "__main__":
    main()
