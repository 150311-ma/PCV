# -*- coding: utf-8 -*-
"""给宣传片配旁白和背景音乐，导出成片 + B 站版。

旁白：Microsoft Edge 的神经网络 TTS（zh-CN-XiaoxiaoNeural），本地合成、不依赖别人的音源。
音乐：**自己合成的**。理由写在下面——从网上找曲子做商业投稿有版权风险，
扒下来 B 站查重一撞一个准；这里用 numpy 生成一段原创垫底，版权干净。

节奏不是写死的 5 秒：每段的时长由那句旁白念完需要多久决定，字幕和画面跟着语音走。
"""
import asyncio
import math
import os
import subprocess
import sys
import wave

import numpy as np
import edge_tts
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROMO = os.path.join(ROOT, "promo")
AUDIO = os.path.join(ROOT, "audio")
SR = 48000
FADE = 0.6
LEAD = 0.9      # 每段旁白开念前留一点空
TAIL = 1.7      # 念完之后留的尾巴

VOICE = "zh-CN-XiaoxiaoNeural"
RATE = "-6%"

# (帧, 旁白)
LINES = [
    ("frame-title.png", "这是我们做的 PCV，一个只负责装框架的壳。"),
    ("frame-1.png", "第一页是软件。你自己装的那些，都在这儿。"),
    ("frame-2.png", "框架在下面，按类别分好。基础框架只负责被调用，不给你打开。"),
    ("frame-3.png", "装东西有三条路：拖包、挑文件，或者把 AI 给的脚本直接粘进来，跑完自己就装上了。"),
    ("frame-4.png", "每个框架一个独立窗口，点开就能用，不用重启。"),
    ("frame-outro.png", "拖一个包进来，就这么用。"),
]


def sh(args):
    result = subprocess.run(args, capture_output=True, text=True)
    if result.returncode != 0:
        print("命令失败：", " ".join(args[:8]))
        print(result.stderr[-1200:])
        sys.exit(1)
    return result.stdout


def duration_of(path):
    out = sh(["ffprobe", "-v", "error", "-show_entries", "format=duration",
              "-of", "default=nw=1:nk=1", path])
    return float(out.strip())


# ── 旁白 ────────────────────────────────────────────────────────────────────

async def synth(text, path):
    communicate = edge_tts.Communicate(text, VOICE, rate=RATE)
    await communicate.save(path)


def make_voice():
    voice_files = []
    for index, (_, text) in enumerate(LINES):
        mp3 = os.path.join(AUDIO, "line%d.mp3" % index)
        wav = os.path.join(AUDIO, "line%d.wav" % index)
        asyncio.run(synth(text, mp3))

        # 统一成 48k 立体声，顺带把 TTS 的 -3dB 左右留白削掉一点
        sh(["ffmpeg", "-y", "-i", mp3, "-ar", str(SR), "-ac", "2",
            "-af", "highpass=f=90,acompressor=threshold=-18dB:ratio=3:attack=8:release=180",
            wav])
        voice_files.append(wav)
    return voice_files


# ── 音乐（原创合成）─────────────────────────────────────────────────────────

def soft_pad(seconds):
    """
    一段极简垫底：I–V–vi–IV 循环 + 稀疏的钟琴音。
    全是正弦叠出来的，没有采样素材，所以不存在版权问题。
    """
    total = int((seconds + 4) * SR)
    track = np.zeros(total, dtype=np.float64)

    # C3 起的三和弦，单位 Hz
    chords = [
        [130.81, 164.81, 196.00, 261.63],   # C
        [98.00, 123.47, 146.83, 196.00],    # G
        [110.00, 130.81, 164.81, 220.00],   # Am
        [87.31, 110.00, 130.81, 174.61],    # F
    ]

    bar = 4.0                      # 每个和弦 4 秒
    for index, chord in enumerate(chords * int(math.ceil((seconds + 4) / (bar * 4)))):
        start = index * bar
        if start * SR >= total:
            break
        begin = int(start * SR)
        length = int(bar * SR)
        end = min(begin + length, total)
        span = end - begin

        envelope = np.ones(span)
        attack = int(0.9 * SR)
        release = int(1.1 * SR)
        envelope[:min(attack, span)] = np.linspace(0, 1, min(attack, span))
        envelope[-min(release, span):] *= np.linspace(1, 0, min(release, span))

        t = np.arange(span) / SR
        for note in chord:
            for harmonic, gain in ((1, 0.5), (2, 0.18), (3, 0.07)):
                detune = 1.0 + 0.0006 * ((note * 10) % 3 - 1)
                track[begin:end] += gain * np.sin(2 * np.pi * note * harmonic * detune * t) * envelope

        # 钟琴：每小节第 1、3 拍各一下，走和弦最高音
        for beat in (0.0, 2.0):
            hit = int((start + beat) * SR)
            hit_end = min(hit + int(1.6 * SR), total)
            if hit >= total:
                continue
            local = np.arange(hit_end - hit) / SR
            decay = np.exp(-3.2 * local)
            tone = np.sin(2 * np.pi * chord[-1] * 2 * local) * decay * 0.12
            track[hit:hit_end] += tone

    track /= max(1e-9, np.max(np.abs(track))) 
    track *= 0.32

    stereo = np.stack([track, track], axis=1)
    return stereo


def write_wav(path, samples):
    data = np.clip(samples, -1, 1)
    pcm = (data * 32767).astype("<i2")
    with wave.open(path, "wb") as handle:
        handle.setnchannels(2)
        handle.setsampwidth(2)
        handle.setframerate(SR)
        handle.writeframes(pcm.tobytes())


# ── 合成 ────────────────────────────────────────────────────────────────────

def build(video_path, voice_files):
    durations = [max(4.6, duration_of(v) + LEAD + TAIL) for v in voice_files]
    starts, cursor = [], 0.0
    for value in durations:
        starts.append(cursor)
        cursor += value - FADE
    total = cursor + FADE

    music_wav = os.path.join(AUDIO, "music.wav")
    write_wav(music_wav, soft_pad(total))

    inputs, filters = [], []
    for index, (frame, _) in enumerate(LINES):
        inputs += ["-loop", "1", "-t", "%.2f" % durations[index], "-i", os.path.join(PROMO, frame)]

    for index, voice in enumerate(voice_files):
        inputs += ["-i", voice]

    inputs += ["-i", music_wav]

    # 视频：逐段 xfade
    previous = "[0:v]"
    offset = 0.0
    for index in range(1, len(LINES)):
        offset += durations[index - 1] - FADE
        label = "[v%d]" % index
        filters.append("%s[%d:v]xfade=transition=fadewhite:duration=%.2f:offset=%.2f%s"
                       % (previous, index, FADE, offset, label))
        previous = label
    filters.append("%sfps=30,format=yuv420p[vout]" % previous)

    # 旁白：按段落起点铺开
    voice_labels = []
    for index in range(len(LINES)):
        delay = int((starts[index] + LEAD) * 1000)
        filters.append("[%d:a]adelay=%d|%d,volume=1.0[s%d]" % (len(LINES) + index, delay, delay, index))
        voice_labels.append("[s%d]" % index)
    # [voice] 要被用两次（一次做侧链、一次进最终混音），必须先 asplit
    filters.append("%samix=inputs=%d:duration=longest:normalize=0[voiceall]"
                   % ("".join(voice_labels), len(voice_labels)))
    filters.append("[voiceall]asplit=2[voice][side]")

    # 音乐：压低 + 旁白说话时自动再让一档
    music_index = len(LINES) * 2
    filters.append("[%d:a]volume=0.42[bed]" % music_index)
    filters.append("[bed][side]sidechaincompress=threshold=0.04:ratio=4:attack=12:release=420[duck]")
    filters.append("[voice][duck]amix=inputs=2:duration=first:normalize=0[amixed]")
    # 统一响度：平台播放器不会替我们调音量，-16 LUFS 是网络视频的通行值
    filters.append("[amixed]loudnorm=I=-16:TP=-1.5:LRA=11[aout]")

    sh(["ffmpeg", "-y"] + inputs + [
        "-filter_complex", ";".join(filters),
        "-map", "[vout]", "-map", "[aout]",
        "-c:v", "libx264", "-preset", "slow", "-crf", "18",
        "-profile:v", "high", "-level", "4.1",
        "-c:a", "aac", "-b:a", "192k", "-ar", str(SR), "-ac", "2",
        "-shortest", "-movflags", "+faststart",
        video_path
    ])
    return total, durations


def main():
    os.makedirs(AUDIO, exist_ok=True)
    missing = [frame for frame, _ in LINES if not os.path.isfile(os.path.join(PROMO, frame))]
    if missing:
        print("缺帧：", missing, "\n先跑 tools/make_promo.py")
        sys.exit(1)

    voice_files = make_voice()
    output = os.path.join(PROMO, "PCV-B站版.mp4")
    total, durations = build(output, voice_files)

    size = os.path.getsize(output) / 1024 / 1024
    print("成片：%s（%.1f MB，%.1f 秒）" % (output, size, total))
    print("每段时长：" + " / ".join("%.1f" % d for d in durations))


if __name__ == "__main__":
    main()
