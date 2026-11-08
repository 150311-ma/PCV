#!/usr/bin/env python3
"""生成 PCV 的文件指纹清单 integrity.json。

为什么要有它：
    我们挡不住专业逆向，但能挡住「下载到一份被人改过的 PCV」这种最常见的
    情况。清单里是每个文件的 sha256，程序启动时逐个对一遍；对不上就提醒
    用户，然后按用户确认结束进程。

    清单放在程序同目录，跟着安装包一起分发。

清单里写什么：
    只写程序真正依赖的文件 —— 主程序、壳用到的 dll、随包的模块 dll。
    不写日志、缓存、用户的配置（那些本来就会变）。

用法：
    python tools/build_integrity.py                     # 对 Release 输出目录生成
    python tools/build_integrity.py --out <目录>        # 指定目录
    python tools/build_integrity.py --check             # 只校验，不写
    python tools/build_integrity.py --inject-fault      # 反向验证：改坏一个文件，确认校验会报红
"""
from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import shutil
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
DEFAULT_SOURCE = ROOT / "src" / "Pcv.Shell" / "bin" / "Release"
MANIFEST_NAME = "integrity.json"

# 这些扩展名的东西参与指纹比对
WANTED_SUFFIXES = {".exe", ".dll"}

# 这些目录不参与：会变、跟程序本体无关
SKIP_DIRS = {"logs", "PCLData", ".backup", ".trash", ".staging", "obj", "ref"}


def sha256_of(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def collect(source: pathlib.Path) -> list[dict]:
    """列出参与指纹比对的文件（相对路径 + sha256）。"""
    entries: list[dict] = []
    if not source.is_dir():
        return entries
    for path in sorted(source.rglob("*")):
        if not path.is_file():
            continue
        if path.name == MANIFEST_NAME:
            continue
        if path.suffix.lower() not in WANTED_SUFFIXES:
            continue
        relative = path.relative_to(source)
        if any(part in SKIP_DIRS for part in relative.parts):
            continue
        entries.append({"path": relative.as_posix(), "sha256": sha256_of(path)})
    return entries


def build(source: pathlib.Path) -> dict:
    files = collect(source)
    return {
        "schema": 1,
        "note": "PCV 发布时的文件指纹。启动时对不上会提醒用户并结束进程。",
        "files": files,
    }

def manifest_bytes(document: dict) -> bytes:
    return (json.dumps(document, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def verify(source: pathlib.Path, document: dict) -> list[str]:
    """现场重算一遍，逐个比对磁盘上的清单。"""
    issues: list[str] = []
    expected = {item["path"]: item["sha256"] for item in document.get("files", [])}
    actual = {item["path"]: item["sha256"] for item in collect(source)}

    for path, digest in sorted(expected.items()):
        if path not in actual:
            issues.append("清单里有 " + path + "，磁盘上却没有")
        elif actual[path] != digest:
            issues.append(path + " 的指纹和清单对不上")

    for path in sorted(actual):
        if path not in expected:
            issues.append(path + " 没有进清单")

    return issues


def inject_fault() -> int:
    """造一份坏目录，确认校验真的会报红 —— 不通过就不许当校验用。"""
    scenarios = []

    def scene(name: str, mutate, keyword: str) -> None:
        with tempfile.TemporaryDirectory(prefix="pcv-integrity-") as tmp:
            root = pathlib.Path(tmp)
            (root / "modules" / "pcv.core").mkdir(parents=True)
            (root / "PCV.exe").write_bytes(b"MZ fake exe")
            (root / "modules" / "pcv.core" / "Pcv.Core.dll").write_bytes(b"fake dll")
            document = build(root)
            (root / MANIFEST_NAME).write_bytes(manifest_bytes(document))

            mutate(root)

            issues = verify(root, document)
            hit = any(keyword in line for line in issues)
            scenarios.append((name, hit, "" if hit else "校验没报这一项：" + " | ".join(issues)[:160]))

    def tamper_file(root: pathlib.Path) -> None:
        (root / "modules" / "pcv.core" / "Pcv.Core.dll").write_bytes(b"tampered dll")

    def remove_file(root: pathlib.Path) -> None:
        (root / "modules" / "pcv.core" / "Pcv.Core.dll").unlink()

    def add_file(root: pathlib.Path) -> None:
        (root / "modules" / "pcv.core" / "Extra.dll").write_bytes(b"extra")

    scene("文件被改内容会报红", tamper_file, "指纹和清单对不上")
    scene("文件不见了会报红", remove_file, "磁盘上却没有")
    scene("多出没进清单的文件会报红", add_file, "没有进清单")

    print("== 文件指纹校验器反向验证（注入已知故障） ==")
    print()
    failed = 0
    for name, ok, detail in scenarios:
        print(("PASS  " if ok else "FAIL  ") + name + ("  ← " + detail if detail else ""))
        failed += 0 if ok else 1
    print()
    print(f"{len(scenarios) - failed}/{len(scenarios)} 个故障被正确抓到")
    return 0 if failed == 0 else 1


def main() -> int:
    parser = argparse.ArgumentParser(description="生成 PCV 的文件指纹清单")
    parser.add_argument("--source", help="待扫描的目录，默认 Release 输出目录")
    parser.add_argument("--out", help="清单写到哪，默认写进 source 目录")
    parser.add_argument("--check", action="store_true", help="只校验，不写文件")
    parser.add_argument("--inject-fault", action="store_true", help="反向验证校验器本身")
    args = parser.parse_args()

    if args.inject_fault:
        return inject_fault()

    source = pathlib.Path(args.source).resolve() if args.source else DEFAULT_SOURCE
    if not source.is_dir():
        print("目录不存在：" + str(source))
        return 1

    document = build(source)
    if not document["files"]:
        print("目录里没有可比对的文件：" + str(source))
        return 1

    target = pathlib.Path(args.out).resolve() if args.out else source / MANIFEST_NAME

    if args.check:
        if not target.is_file():
            print("清单不存在：" + str(target))
            return 1
        issues = verify(source, json.loads(target.read_text(encoding="utf-8")))
        if issues:
            print("校验没过：")
            for line in issues:
                print("  ✗ " + line)
            return 1
        print("校验通过：" + str(len(document["files"])) + " 个文件的指纹与清单一致")
        return 0

    target.write_bytes(manifest_bytes(document))
    size = target.stat().st_size
    print("已生成 %s（%d 个文件，%.1f KB）" % (target, len(document["files"]), size / 1024))
    return 0


if __name__ == "__main__":
    sys.exit(main())
