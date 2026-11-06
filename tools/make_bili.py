# -*- coding: utf-8 -*-
"""把宣传片导出成 B 站投稿用的版本，外加封面和简介。

和"能播就行"的区别：
  · 30fps（B 站转码对 25/29.97 都可以，但 30 更稳，避免转码后轻微抖动）
  · H.264 High@4.1 + yuv420p：B 站转码器最不容易出问题的一组参数
  · **带一条静音 AAC 音轨**：纯无声视频在部分播放器/上传流程里会被判"异常"
  · faststart：边下边播，封面能更快抓出来
  · 封面 1146×717（16:9），这是 B 站封面推荐尺寸

用法：python tools/make_bili.py
前置：先跑过 tools/make_promo.py，frame-*.png 要在。
"""
import os
import subprocess
import sys

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROMO = os.path.join(ROOT, "promo")

SEGMENT = 5.0
FADE = 0.5
FPS = 30

BG_TOP = (246, 248, 252)
BG_BOTTOM = (243, 246, 251)
INK = (19, 26, 36)
INK_SOFT = (107, 118, 136)
ACCENT = (19, 112, 243)


def font(bold, size):
    name = "msyhbd.ttc" if bold else "msyh.ttc"
    return ImageFont.truetype(os.path.join(PROMO, name), size)


def run(args):
    result = subprocess.run(args, capture_output=True, text=True, cwd=PROMO)
    if result.returncode != 0:
        print("命令失败：", " ".join(args[:8]))
        print(result.stderr[-1500:])
        sys.exit(1)
    return result.stdout


def build_video(frames, output):
    inputs = []
    for frame in frames:
        inputs += ["-loop", "1", "-t", str(SEGMENT), "-i", frame]

    # 静音音轨放在最后一路，避免 B 站把它当成"无音轨文件"
    inputs += ["-f", "lavfi", "-t", str(SEGMENT * len(frames)), "-i", "anullsrc=r=48000:cl=stereo"]

    parts, previous, offset = [], "[0:v]", SEGMENT - FADE
    for index in range(1, len(frames)):
        label = "[v%d]" % index
        parts.append("%s[%d:v]xfade=transition=fadewhite:duration=%.2f:offset=%.2f%s"
                     % (previous, index, FADE, offset, label))
        previous = label
        offset += SEGMENT - FADE

    # 统一成 30fps + yuv420p，B 站转码最不容易出问题的组合
    parts.append("%sfps=%d,format=yuv420p[vout]" % (previous, FPS))

    run(["ffmpeg", "-y"] + inputs + [
        "-filter_complex", ";".join(parts),
        "-map", "[vout]",
        "-map", "%d:a" % len(frames),
        "-c:v", "libx264", "-preset", "slow", "-crf", "18",
        "-profile:v", "high", "-level", "4.1",
        "-maxrate", "12M", "-bufsize", "24M",
        "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2",
        "-r", str(FPS), "-shortest", "-movflags", "+faststart",
        output
    ])


def build_cover(path):
    """1146×717 封面：浅色 + 图标 + 大字 + 一句人话。"""
    width, height = 1146, 717
    image = Image.new("RGB", (width, height), BG_TOP)
    draw = ImageDraw.Draw(image)

    for y in range(height):
        ratio = y / height
        color = tuple(int(BG_TOP[i] + (BG_BOTTOM[i] - BG_TOP[i]) * ratio) for i in range(3))
        draw.line([(0, y), (width, y)], fill=color)

    logo = os.path.join(ROOT, "PCL-repo", "modular", "assets", "pcv-256.png")
    if os.path.isfile(logo):
        icon = Image.open(logo).convert("RGBA").resize((86, 86), Image.LANCZOS)
        image.paste(icon, (78, 92), icon)

    draw.text((184, 100), "PCV 2.0.0", font=font(True, 30), fill=ACCENT)

    draw.text((78, 230), "PAINCHARNWAV", font=font(True, 96), fill=INK)

    draw.rounded_rectangle([80, 356, 86, 430], 3, fill=ACCENT)
    draw.text((108, 356), "一个只负责装框架的壳", font=font(False, 38), fill=INK)

    draw.text((108, 414), "框架负责能力，软件负责用起来", font=font(False, 26), fill=INK_SOFT)

    draw.text((78, 630), "装包的、写脚本的、拖进窗口就能用的那种", font=font(False, 22), fill=INK_SOFT)

    image.save(path, quality=95)


def main():
    frames = ["frame-title.png"] + ["frame-%d.png" % i for i in range(1, 5)] + ["frame-outro.png"]
    for frame in frames:
        if not os.path.isfile(os.path.join(PROMO, frame)):
            print("缺少帧：" + frame + "，先跑 tools/make_promo.py")
            sys.exit(1)

    video = os.path.join(PROMO, "PCV-B站版.mp4")
    build_video(frames, video)

    cover = os.path.join(PROMO, "PCV-B站封面.jpg")
    build_cover(cover)

    info = run(["ffprobe", "-v", "error", "-show_entries",
                "format=duration,size,bit_rate:stream=codec_name,width,height,r_frame_rate,channels",
                "-of", "default=noprint_wrappers=1", video])
    print(info.strip())

    size = os.path.getsize(video) / 1024 / 1024
    print("\n视频：%s（%.1f MB）" % (video, size))
    print("封面：%s" % cover)


if __name__ == "__main__":
    main()
