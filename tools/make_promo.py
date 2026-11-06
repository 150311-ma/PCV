# -*- coding: utf-8 -*-
"""按软件本身的视觉语言做宣传片（浅色 / 干净 / 蓝点缀）。

之前那版用的是黑底电影感 + 深色字幕条，和界面（#F6F8FC 底、白卡片、#1370F3 蓝）完全是两套语言。
现在改成：浅底 + 圆角截图 + 柔和投影 + 深色字 + 蓝竖条，字幕直接画进画面，
ffmpeg 只负责淡入淡出拼接，不再叠 drawtext（也就不会踩字体转义那些坑）。

素材仍然全部是真实界面：--render 出来的页面 + 真实窗口截图。
"""
import os
import shutil
import subprocess
import sys

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROMO = os.path.join(ROOT, "promo")
W, H = 1920, 1080
SEGMENT = 5.0
FADE = 0.5

# 和界面一致的色
BG_TOP = (246, 248, 252)      # #F6F8FC
BG_BOTTOM = (243, 246, 251)   # 跨度收窄，防 8bit 色带
INK = (19, 26, 36)            # #131A24
INK_SOFT = (107, 118, 136)    # #6B7688
ACCENT = (19, 112, 243)       # #1370F3
LINE = (178, 190, 208)

SHOTS = [
    ("01-home.png", "软件在前", "你打开的是软件，不是一堆设置项"),
    ("02-frameworks.png", "框架按类别分组", "基础框架只被调用，不给你「打开」"),
    ("03-install.png", "粘贴就能装", "拖包、挑文件、粘一段 AI 输出，跑完自动装"),
    ("04-window.png", "一个框架一个窗口", "装完直接能用，不用重启"),
]


def font(bold, size):
    name = "msyhbd.ttc" if bold else "msyh.ttc"
    path = os.path.join(PROMO, name)
    if not os.path.isfile(path):
        shutil.copy2(os.path.join(r"C:\Windows\Fonts", name), path)
    return ImageFont.truetype(path, size)


def canvas():
    """浅色渐变底，和窗口背景同一色系。"""
    image = Image.new("RGB", (W, H), BG_TOP)
    draw = ImageDraw.Draw(image)
    for y in range(H):
        ratio = y / H
        color = tuple(int(BG_TOP[i] + (BG_BOTTOM[i] - BG_TOP[i]) * ratio) for i in range(3))
        draw.line([(0, y), (W, y)], fill=color)
    return image


def paste_card(base, image_path, box):
    """把截图按比例塞进 box，加圆角 + 柔和投影。"""
    shot = Image.open(image_path).convert("RGB")
    box_w, box_h = box
    scale = min(box_w / shot.width, box_h / shot.height)
    shot = shot.resize((int(shot.width * scale), int(shot.height * scale)), Image.LANCZOS)

    radius = 18
    mask = Image.new("L", shot.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, shot.width - 1, shot.height - 1], radius, fill=255)

    x = (W - shot.width) // 2
    y = 92

    # 投影：先画一块模糊的深色圆角块，再贴图
    shadow = Image.new("RGBA", (shot.width + 80, shot.height + 80), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle([40, 52, shot.width + 40, shot.height + 40],
                                             radius + 8, fill=(21, 50, 92, 38))
    shadow = shadow.filter(ImageFilter.GaussianBlur(22))
    base.paste(shadow, (x - 40, y - 40), shadow)

    base.paste(shot, (x, y), mask)


def caption(base, title, subtitle):
    """左下角：蓝色竖条 + 标题 + 一行说明。"""
    draw = ImageDraw.Draw(base)
    bar_x, bar_y = 176, 918
    draw.rounded_rectangle([bar_x, bar_y + 4, bar_x + 6, bar_y + 66], 3, fill=ACCENT)
    draw.text((bar_x + 26, bar_y), title, font=font(True, 46), fill=INK)
    draw.text((bar_x + 28, bar_y + 64), subtitle, font=font(False, 29), fill=INK_SOFT)


def title_card(path):
    base = canvas()
    draw = ImageDraw.Draw(base)

    logo = os.path.join(ROOT, "PCL-repo", "modular", "assets", "pcv-256.png")
    if os.path.isfile(logo):
        icon = Image.open(logo).convert("RGBA").resize((120, 120), Image.LANCZOS)
        base.paste(icon, ((W - 120) // 2, 296), icon)

    word = "PAINCHARNWAV"
    width = draw.textlength(word, font=font(True, 100))
    draw.text(((W - width) / 2, 452), word, font=font(True, 100), fill=INK)

    line1 = "PCV 2.0 · 一个只负责装框架的壳"
    width = draw.textlength(line1, font=font(False, 38))
    draw.text(((W - width) / 2, 592), line1, font=font(False, 38), fill=INK_SOFT)

    line2 = "框架负责能力，软件负责用起来"
    width = draw.textlength(line2, font=font(False, 32))
    draw.text(((W - width) / 2, 656), line2, font=font(False, 32), fill=ACCENT)

    base.save(path)


def outro_card(path):
    base = canvas()
    draw = ImageDraw.Draw(base)

    word = "PAINCHARNWAV（PCV）"
    width = draw.textlength(word, font=font(True, 74))
    draw.text(((W - width) / 2, 446), word, font=font(True, 74), fill=INK)

    line = "拖一个包进来，就这么用"
    width = draw.textlength(line, font=font(False, 38))
    draw.text(((W - width) / 2, 556), line, font=font(False, 38), fill=INK_SOFT)

    foot = "PCV 2.0.0　·　github.com/150311-ma/PCV"
    width = draw.textlength(foot, font=font(False, 26))
    draw.text(((W - width) / 2, 992), foot, font=font(False, 26), fill=LINE)

    base.save(path)


def shot_card(path, image_name, title, subtitle):
    base = canvas()
    paste_card(base, os.path.join(PROMO, image_name), (1704, 786))
    caption(base, title, subtitle)
    base.save(path)


def run(args):
    result = subprocess.run(args, capture_output=True, text=True, cwd=PROMO)
    if result.returncode != 0:
        print("命令失败：", " ".join(args[:6]))
        print(result.stderr[-1200:])
        sys.exit(1)


def build(frames, output):
    inputs = []
    for frame in frames:
        inputs += ["-loop", "1", "-t", str(SEGMENT), "-i", frame]

    parts, previous, offset = [], "[0:v]", SEGMENT - FADE
    for index in range(1, len(frames)):
        label = "[v%d]" % index
        parts.append("%s[%d:v]xfade=transition=fadewhite:duration=%.2f:offset=%.2f%s"
                     % (previous, index, FADE, offset, label))
        previous = label
        offset += SEGMENT - FADE

    run(["ffmpeg", "-y"] + inputs + [
        "-filter_complex", ";".join(parts),
        "-map", previous,
        "-c:v", "libx264", "-preset", "medium", "-crf", "19",
        "-pix_fmt", "yuv420p", "-movflags", "+faststart", output
    ])


def main():
    frames = [os.path.join(PROMO, "frame-title.png")]
    title_card(frames[0])

    for index, (image, title, subtitle) in enumerate(SHOTS, 1):
        path = os.path.join(PROMO, "frame-%d.png" % index)
        shot_card(path, image, title, subtitle)
        frames.append(path)

    outro = os.path.join(PROMO, "frame-outro.png")
    outro_card(outro)
    frames.append(outro)

    output = os.path.join(PROMO, "PCV-宣传片.mp4")
    build(frames, output)

    seconds = int(SEGMENT * len(frames) - FADE * (len(frames) - 1))
    size = os.path.getsize(output) / 1024 / 1024
    print("做好了：%s（%.1f MB，%d 秒）" % (output, size, seconds))


if __name__ == "__main__":
    main()
