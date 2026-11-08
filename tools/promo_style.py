# -*- coding: utf-8 -*-
"""宣传片三支脚本共用的样式：颜色、版本、路径都从仓库里唯一的真相源取。

TRM UI 的第一条硬规矩是「颜色只从变量取」。宣传片虽然不是 WPF 界面，
但同一套视觉语言不该在 make_promo / make_bili 里各抄一份色值 ——
抄两份的结局一定是界面换了主题，宣传片还是旧蓝。

所以这里直接解析 src/Pcv.Shell/Styles/TrmTheme.xaml 里的 SolidColorBrush，
版本号从 installer/PCV.Setup/Installer.cs 读。改一处，三支脚本全跟着变。

两套工具副本（开发机 tools/ 与仓库内 modular/tools/）用同一份文件：
路径靠从脚本所在目录向上找 promo/ 与 src/ 定位，不写死本机绝对路径。
"""
import os
import re
import shutil

from PIL import Image, ImageDraw, ImageFilter, ImageFont

W, H = 1920, 1080
SEGMENT = 5.0
FADE = 0.5
FPS = 30

_TOOLS = os.path.dirname(os.path.abspath(__file__))


def _ancestors():
    """脚本所在目录的父目录开始，一路往上，最多 5 层。"""
    node = os.path.dirname(_TOOLS)
    result = []
    for _ in range(5):
        result.append(node)
        parent = os.path.dirname(node)
        if parent == node:
            break
        node = parent
    return result


def _find_dir(name):
    for base in _ancestors():
        candidate = os.path.join(base, name)
        if os.path.isdir(candidate):
            return candidate
    raise RuntimeError("找不到 %s/，向上找过：%s" % (name, " / ".join(_ancestors())))


def _find_file(relative, label):
    """既支持开发机布局（PCL-MultiModule/PCL-repo/modular/...），
    也支持仓库内布局（modular/...）。"""
    for base in _ancestors():
        for prefix in ("", os.path.join("PCL-repo", "modular")):
            candidate = os.path.join(base, prefix, relative)
            if os.path.isfile(candidate):
                return candidate
    raise RuntimeError("找不到 " + label)


PROMO = _find_dir("promo")
AUDIO = os.path.join(os.path.dirname(PROMO), "audio")
THEME = _find_file(os.path.join("src", "Pcv.Shell", "Styles", "TrmTheme.xaml"), "TRM 浅色主题字典")
INSTALLER = _find_file(os.path.join("installer", "PCV.Setup", "Installer.cs"), "版本号真相源")
LOGO = _find_file(os.path.join("assets", "pcv-256.png"), "PCV 图标")

# ── 颜色：只从设计令牌来 ────────────────────────────────────────────────────

_TOKEN_RE = re.compile(r'<SolidColorBrush x:Key="(Trm\w+)"\s+Color="#([0-9A-Fa-f]{6,8})"')


def _read_tokens():
    with open(THEME, encoding="utf-8") as handle:
        found = _TOKEN_RE.findall(handle.read())
    if not found:
        raise RuntimeError("从 %s 里没解析出颜色令牌，检查字典格式" % THEME)
    return {key: tuple(int(value[i:i + 2], 16) for i in (0, 2, 4))
            for key, value in found}


TOKENS = _read_tokens()


def token(key):
    if key not in TOKENS:
        raise RuntimeError("设计令牌 %s 不存在，界面和宣传片会脱节" % key)
    return TOKENS[key]


def mix(a, b, ratio):
    return tuple(int(a[i] + (b[i] - a[i]) * ratio) for i in range(3))


BG_TOP = token("TrmBg")
BG_BOTTOM = mix(BG_TOP, token("TrmLine"), 0.18)   # 跨度收窄，防 8bit 色带
INK = token("TrmInk")
INK_SOFT = token("TrmInkMuted")
ACCENT = token("TrmAccent")
LINE = token("TrmLine")

with open(INSTALLER, encoding="utf-8") as _handle:
    _match = re.search(r'public const string Version\s*=\s*"([\d.]+)"', _handle.read())
if not _match:
    raise RuntimeError("从 %s 读不到版本号" % INSTALLER)
VERSION = _match.group(1)


def font(bold, size):
    name = "msyhbd.ttc" if bold else "msyh.ttc"
    path = os.path.join(PROMO, name)
    if not os.path.isfile(path):
        shutil.copy2(os.path.join("C:\\Windows\\Fonts", name), path)
    return ImageFont.truetype(path, size)


def canvas(width=W, height=H):
    """浅色渐变底，和 TrmBg / TrmLine 同一色系。"""
    image = Image.new("RGB", (width, height), BG_TOP)
    draw = ImageDraw.Draw(image)
    for y in range(height):
        ratio = y / height
        color = mix(BG_TOP, BG_BOTTOM, ratio)
        draw.line([(0, y), (width, y)], fill=color)
    return image


def paste_card(base, image_path, box, x=None, y=92, radius=18):
    """把截图按比例塞进 box，加圆角 + 柔和投影。"""
    shot = Image.open(image_path).convert("RGB")
    box_w, box_h = box
    scale = min(box_w / shot.width, box_h / shot.height)
    shot = shot.resize((int(shot.width * scale), int(shot.height * scale)), Image.LANCZOS)

    mask = Image.new("L", shot.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, shot.width - 1, shot.height - 1],
                                           radius, fill=255)

    if x is None:
        x = (base.width - shot.width) // 2

    shadow = Image.new("RGBA", (shot.width + 80, shot.height + 80), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle(
        [40, 52, shot.width + 40, shot.height + 40], radius + 8,
        fill=(21, 50, 92, 38))
    shadow = shadow.filter(ImageFilter.GaussianBlur(22))
    base.paste(shadow, (x - 40, y - 40), shadow)
    base.paste(shot, (x, y), mask)
