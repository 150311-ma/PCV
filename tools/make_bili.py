# -*- coding: utf-8 -*-
"""出 B 站投稿用的静音备份片和封面。

和"能播就行"的区别：
  · 30fps（B 站转码对 25/29.97 都可以，但 30 更稳，避免转码后轻微抖动）
  · H.264 High@4.1 + yuv420p：B 站转码器最不容易出问题的一组参数
  · **带一条静音 AAC 音轨**：纯无声视频在部分播放器/上传流程里会被判"异常"
  · faststart：边下边播，封面能更快抓出来
  · 封面 1146×717（16:9），这是 B 站封面推荐尺寸

**文件分工**（以前两支脚本都写 PCV-B站版.mp4，谁后跑谁覆盖，很坑）：
  · make_narrated.py → PCV-B站版.mp4（有旁白有配乐，投稿用这个）
  · 本脚本        → PCV-B站静音版.mp4（无声备份）+ PCV-B站封面.jpg
两者都只依赖 frame-*.png，谁先跑谁后跑都不会互相盖掉。

用法：python tools/make_bili.py
前置：先跑过 tools/make_promo.py，frame-*.png 要在。
"""
import os
import subprocess
import sys

from PIL import Image, ImageDraw

import promo_style as S
from make_promo import frame_names

PROMO = S.PROMO
SEGMENT, FADE, FPS = S.SEGMENT, S.FADE, S.FPS


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
    inputs += ["-f", "lavfi", "-t", str(SEGMENT * len(frames)),
               "-i", "anullsrc=r=48000:cl=stereo"]

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
    """1146×717 封面：浅色 + 图标 + 大字 + 右侧一张新 UI 真截图。"""
    width, height = 1146, 717
    image = S.canvas(width, height)
    draw = ImageDraw.Draw(image)

    icon = Image.open(S.LOGO).convert("RGBA").resize((86, 86), Image.LANCZOS)
    image.paste(icon, (72, 86), icon)

    draw.text((178, 94), "PCV %s" % S.VERSION, font=S.font(True, 30), fill=S.ACCENT)

    draw.text((72, 206), "PAINCHARNWAV", font=S.font(True, 88), fill=S.INK)

    draw.rounded_rectangle([74, 344, 80, 418], 3, fill=S.ACCENT)
    draw.text((102, 344), "一个只负责装框架的壳", font=S.font(False, 38), fill=S.INK)
    draw.text((102, 402), "浅色 / 深色 · 官方市场 · 自动更新", font=S.font(False, 24), fill=S.INK_SOFT)

    draw.text((72, 626), "拖包 · 粘贴 AI 脚本 · 自动安装", font=S.font(False, 22), fill=S.INK_SOFT)

    # 右侧放一张真实的新 UI 截图，封面上一眼能看出软件长什么样
    shot = os.path.join(PROMO, "01-home.png")
    if os.path.isfile(shot):
        # 往下放，别压住上面那行 PAINCHARNWAV（它比截图长）
        S.paste_card(image, shot, (492, 308), x=596, y=356, radius=14)

    image.save(path, quality=95)


def main():
    frames = [os.path.join(PROMO, name) for name in frame_names()]
    for frame in frames:
        if not os.path.isfile(frame):
            print("缺少帧：" + os.path.basename(frame) + "，先跑 tools/make_promo.py")
            sys.exit(1)

    video = os.path.join(PROMO, "PCV-B站静音版.mp4")
    build_video(frames, video)

    cover = os.path.join(PROMO, "PCV-B站封面.jpg")
    build_cover(cover)

    info = run(["ffprobe", "-v", "error", "-show_entries",
                "format=duration,size,bit_rate:stream=codec_name,width,height,r_frame_rate,channels",
                "-of", "default=noprint_wrappers=1", video])
    print(info.strip())

    size = os.path.getsize(video) / 1024 / 1024
    print("\n静音备份：%s（%.1f MB，%d 帧）" % (video, size, len(frames)))
    print("封面：%s" % cover)


if __name__ == "__main__":
    main()
