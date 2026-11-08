# -*- coding: utf-8 -*-
"""模块数字（ModuleCode）的 Python 实现。

和 C# 端 `src/Pcv.Modular.Packages/ModuleCode.cs` 必须是同一套算法 ——
创作者工具用它算，PCV 也用它算，两边算出来不一样就等于没有这个号。

算法：
    sha256( id + "\\n" + version + "\\n" + package_sha256 ) → 取前 60 位
    → Crockford Base32 12 个字符 → PCV-XXXX-XXXX-XXXX

Crockford Base32 去掉了 I L O U，用户手抄不会把 1 认成 I、0 认成 O。

这个文件被三处用：
    tools/build_market.py     给官方包算号
    tools/make_creator_tool.py（没有，创作者工具在壳里）
    tools/check_module_code.py 拿金标准向量验两端一致
"""
from __future__ import annotations

import hashlib

# Crockford Base32：去 I L O U
ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

SPEC_VERSION = 1


def compute(module_id: str, version: str, package_sha256: str) -> str:
    """按「id + 版本 + 包 sha256」算模块数字。"""
    if not module_id or not module_id.strip():
        raise ValueError("模块 id 不能为空")
    if not package_sha256 or not package_sha256.strip():
        raise ValueError("包的 sha256 不能为空")

    payload = "%s\n%s\n%s" % (
        module_id.strip().lower(),
        (version or "").strip(),
        package_sha256.strip().lower(),
    )
    digest = hashlib.sha256(payload.encode("utf-8")).digest()

    # 前 8 字节拼成 64 位整数，右移 4 位留最高 60 位
    bits = int.from_bytes(digest[:8], "big") >> 4

    chars = []
    for _ in range(12):
        chars.append(ALPHABET[bits & 0x1F])
        bits >>= 5
    text = "".join(reversed(chars))

    return "PCV-%s-%s-%s" % (text[0:4], text[4:8], text[8:12])


def normalize(value: str) -> str | None:
    """把手抄来的号规整成标准写法；不成立返回 None。

    宽容规则与 C# 端一致：先剥掉 PCV 前缀，再忽略大小写与连字符，
    O→0、I/L→1，丢掉 U。
    """
    if not value or not value.strip():
        return None

    text_in = value.strip().upper()
    # 前缀要先剥掉：P/C/V 里有 P、V 不在字母表里，不剥会整条判非法
    if text_in.startswith("PCV"):
        text_in = text_in[3:]

    out = []
    for raw in text_in:
        if raw in "- ":
            continue
        ch = raw
        if ch == "O":
            ch = "0"
        elif ch in "IL":
            ch = "1"
        elif ch == "U":
            continue
        if ch not in ALPHABET:
            return None
        out.append(ch)
        if len(out) > 12:
            return None

    if len(out) != 12:
        return None
    text = "".join(out)
    return "PCV-%s-%s-%s" % (text[0:4], text[4:8], text[8:12])


def same(left: str, right: str) -> bool:
    a, b = normalize(left), normalize(right)
    return a is not None and a is not None and a == b


def sha256_of_file(path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()
