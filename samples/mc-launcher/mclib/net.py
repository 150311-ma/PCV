# -*- coding: utf-8 -*-
"""HTTP 小工具：带重试、带进度、失败会说话。

只用标准库，不引第三方 —— 框架包里多一个依赖，用户就多一份装不上的风险。
"""
import hashlib
import json
import os
import sys
import time
import urllib.error
import urllib.request

UA = "PCV-McLauncher/1.0 (+https://github.com/150311-ma/PCL)"


def get_text(url, timeout=30, retries=3):
    last = None
    for attempt in range(retries):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": UA})
            with urllib.request.urlopen(request, timeout=timeout) as response:
                return response.read().decode("utf-8")
        except urllib.error.HTTPError as error:
            last = "HTTP %s %s" % (error.code, url)
            if error.code in (403, 404):
                break
        except Exception as error:  # noqa: BLE001
            last = "%s: %s" % (type(error).__name__, url)
        time.sleep(0.8 * (attempt + 1))
    raise RuntimeError("下载失败（%s）" % last)


def get_json(url, timeout=30, retries=3):
    return json.loads(get_text(url, timeout, retries))


def sha1_of(path):
    digest = hashlib.sha1()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def download(url, target, expected_sha1=None, expected_size=None, label=None):
    """下到 .part 再改名；已有且校验通过就跳过（重跑不重下）。"""
    if os.path.isfile(target) and expected_sha1:
        try:
            if os.path.getsize(target) == (expected_size or os.path.getsize(target)) \
                    and sha1_of(target) == expected_sha1:
                return False
        except OSError:
            pass

    os.makedirs(os.path.dirname(target) or ".", exist_ok=True)
    temporary = target + ".part"
    try:
        request = urllib.request.Request(url, headers={"User-Agent": UA})
        with urllib.request.urlopen(request, timeout=60) as response, open(temporary, "wb") as handle:
            while True:
                block = response.read(1 << 16)
                if not block:
                    break
                handle.write(block)
        if expected_sha1 and sha1_of(temporary) != expected_sha1:
            raise RuntimeError("校验失败：" + (label or url))
        if os.path.isfile(target):
            os.remove(target)
        os.rename(temporary, target)
        return True
    finally:
        if os.path.isfile(temporary):
            try:
                os.remove(temporary)
            except OSError:
                pass


def say(text):
    """往窗口写一行。宿主按行显示，flush 保证不等缓冲。"""
    sys.stdout.write(text + "\n")
    sys.stdout.flush()


def control(payload):
    sys.stdout.write("@@PCV " + json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()
