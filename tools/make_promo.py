# -*- coding: utf-8 -*-
"""按 TRM UI 的视觉语言做宣传片（浅色 / 干净 / 蓝点缀）。

黑底电影感那版和软件界面完全是两套语言，早就推倒重做了。现在：
浅底 + 圆角截图 + 柔和投影 + 深色字 + 蓝竖条，字幕直接画进画面，
ffmpeg 只负责淡入淡出拼接，不再叠 drawtext（也就不会踩字体转义那些坑）。

色值、字号、版本号全部来自 tools/promo_style.py，它再去读
Styles/TrmTheme.xaml 与 Installer.cs —— 界面换主题，宣传片不会留在旧蓝。

素材全部是真实界面：PCV.exe --page <页> --render <png> 离屏渲染出来的。
"""
import os
import subprocess
import sys

from PIL import Image, ImageDraw

import promo_style as S

W, H = S.W, S.H
SEGMENT, FADE = S.SEGMENT, S.FADE
PROMO = S.PROMO

# (截图, 标题, 一行说明) —— 顺序就是片子的叙事顺序，帧号由这里推出来
SHOTS = [
    ("01-home.png", "应用桌面", "打开就是装好的软件，能力都向框架要"),
    ("02-frameworks.png", "框架在后", "五个框架按类别排好，基础框架只被调用"),
    ("03-install.png", "粘贴就能装", "拖包、挑文件、粘一段 AI 输出，跑完自动装"),
    ("04-market.png", "市场里挑", "官方做好的包直接列出来，点一下就装"),
    ("05-dark.png", "浅色与深色", "同一套设计令牌，换主题不用重开窗口"),
]


def frame_names():
    """唯一的帧名单：其余两支脚本都从这里取，不再各写一份 range(1, 5)。"""
    return (["frame-title.png"]
            + ["frame-%d.png" % i for i in range(1, len(SHOTS) + 1)]
            + ["frame-outro.png"])


def caption(base, title, subtitle):
    """左下角：蓝色竖条 + 标题 + 一行说明。"""
    draw = ImageDraw.Draw(base)
    bar_x, bar_y = 176, 918
    draw.rounded_rectangle([bar_x, bar_y + 4, bar_x + 6, bar_y + 66], 3, fill=S.ACCENT)
    draw.text((bar_x + 26, bar_y), title, font=S.font(True, 46), fill=S.INK)
    draw.text((bar_x + 28, bar_y + 64), subtitle, font=S.font(False, 29), fill=S.INK_SOFT)


def title_card(path):
    base = S.canvas()
    draw = ImageDraw.Draw(base)

    icon = Image.open(S.LOGO).convert("RGBA").resize((120, 120), Image.LANCZOS)
    base.paste(icon, ((W - 120) // 2, 282), icon)

    word = "PAINCHARNWAV"
    width = draw.textlength(word, font=S.font(True, 100))
    draw.text(((W - width) / 2, 442), word, font=S.font(True, 100), fill=S.INK)

    line1 = "PCV %s · 一个只负责装框架的壳" % ".".join(S.VERSION.split(".")[:2])
    width = draw.textlength(line1, font=S.font(False, 38))
    draw.text(((W - width) / 2, 582), line1, font=S.font(False, 38), fill=S.INK_SOFT)

    line2 = "框架负责能力，软件负责用起来"
    width = draw.textlength(line2, font=S.font(False, 32))
    draw.text(((W - width) / 2, 646), line2, font=S.font(False, 32), fill=S.ACCENT)

    line3 = "TRM 设计系统 · 浅色 / 深色 · 官方市场 · 自动更新"
    width = draw.textlength(line3, font=S.font(False, 26))
    draw.text(((W - width) / 2, 706), line3, font=S.font(False, 26), fill=S.INK_SOFT)

    base.save(path)


def outro_card(path):
    base = S.canvas()
    draw = ImageDraw.Draw(base)

    word = "PAINCHARNWAV（PCV）"
    width = draw.textlength(word, font=S.font(True, 74))
    draw.text(((W - width) / 2, 446), word, font=S.font(True, 74), fill=S.INK)

    line = "拖一个包进来，就这么用"
    width = draw.textlength(line, font=S.font(False, 38))
    draw.text(((W - width) / 2, 556), line, font=S.font(False, 38), fill=S.INK_SOFT)

    foot = "PCV %s　·　github.com/150311-ma/PCV" % S.VERSION
    width = draw.textlength(foot, font=S.font(False, 26))
    draw.text(((W - width) / 2, 992), foot, font=S.font(False, 26), fill=S.LINE)

    base.save(path)


def shot_card(path, image_name, title, subtitle):
    base = S.canvas()
    S.paste_card(base, os.path.join(PROMO, image_name), (1704, 786))
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
    missing = [name for name, _, _ in SHOTS
               if not os.path.isfile(os.path.join(PROMO, name))]
    if missing:
        print("缺截图：", missing)
        sys.exit(1)

    names = frame_names()
    frames = [os.path.join(PROMO, name) for name in names]

    title_card(frames[0])
    for index, (image, title, subtitle) in enumerate(SHOTS, 1):
        shot_card(frames[index], image, title, subtitle)
    outro_card(frames[-1])

    output = os.path.join(PROMO, "PCV-宣传片.mp4")
    build(frames, output)

    seconds = SEGMENT * len(frames) - FADE * (len(frames) - 1)
    size = os.path.getsize(output) / 1024 / 1024
    print("做好了：%s（%.1f MB，%.1f 秒，%d 帧）" % (output, size, seconds, len(frames)))
    print("色板取自 %s，版本 %s" % (os.path.basename(S.THEME), S.VERSION))


if __name__ == "__main__":
    main()
