# -*- coding: utf-8 -*-
"""生成 PCV 在线服务的静态数据，并同步到博客三处。

为什么要有它：客户端的服务设置、自动更新、在线凭据模块都从
`pcv-api/*.json` 取数据。手写这三份的结局一定是「版本号改了、线上没改」，
或者「接口清单里写了个根本不存在的路径」。所以：

    pcv-api/catalog.json（人工维护的真相源）
      + installer/PCV.Setup/Installer.cs 的 Version
      + market/index.json 的条目数
      → status.json / interfaces.json / version.json（产物，四处同一份字节）

Worker 里的 /pcv-api/* 只负责直出这三份和动态算轮换凭证，不存业务数据。

用法：
    python tools/build_pcv_api.py                         # 生成 + 自校验
    python tools/build_pcv_api.py --check                 # 只校验，不写
    python tools/build_pcv_api.py --blog <博客根目录>     # 额外同步到博客三处
    python tools/build_pcv_api.py --inject-fault          # 反向验证：改坏一份，确认校验会报红
"""
import argparse
import datetime
import hashlib
import json
import pathlib
import re
import shutil
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
API_DIR = ROOT / "pcv-api"
CATALOG = API_DIR / "catalog.json"
VERSION_FILE = ROOT / "installer" / "PCV.Setup" / "Installer.cs"
MARKET_INDEX = ROOT / "market" / "index.json"
BLOG_TARGETS = [
    pathlib.Path("blog/public/pcv-api"),
    pathlib.Path("blog/dist/pcv-api"),
    pathlib.Path("server/web/pcv-api"),
]
FILES = ("status.json", "interfaces.json", "version.json")
GITHUB_RELEASES = "https://github.com/150311-ma/PCV/releases/latest"
GITHUB_API = "https://api.github.com/repos/150311-ma/PCV/releases/latest"


def json_bytes(obj):
    return (json.dumps(obj, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def current_version(root):
    match = re.search(r'public const string Version\s*=\s*"([\d.]+)"',
                      (root / "installer/PCV.Setup/Installer.cs").read_text(encoding="utf-8"))
    return match.group(1) if match else None


def build(root):
    """返回 (产物字节表, 问题列表)。问题非空一律不许写。"""
    problems = []
    try:
        catalog = json.loads((root / "pcv-api/catalog.json").read_text(encoding="utf-8"))
    except (ValueError, OSError) as error:
        return {}, [f"catalog.json 读不出来：{error}"]

    version = current_version(root)
    if not version:
        problems.append("读不出 Installer.cs 的 Version")

    try:
        items = len(json.loads((root / "market/index.json").read_text(encoding="utf-8"))["items"])
    except (ValueError, KeyError, OSError) as error:
        problems.append("market/index.json 读不出来：" + str(error))
        items = -1

    endpoints = catalog.get("endpoints", [])
    seen = set()
    for ep in endpoints:
        eid = str(ep.get("id") or "").strip()
        path = str(ep.get("path") or "")
        if not eid or eid in seen:
            problems.append("接口 id 缺失或重复：" + eid)
        seen.add(eid)
        if not path.startswith("/") or "://" in path or ".." in path:
            problems.append(f"{eid}: path 必须是站内绝对路径 → {path}")
        if ep.get("method") not in ("GET", "POST"):
            problems.append(f"{eid}: method 只能是 GET / POST")
        if not isinstance(ep.get("enabled"), bool):
            problems.append(f"{eid}: enabled 要是 true / false")
        if not isinstance(ep.get("needsKey"), bool):
            problems.append(f"{eid}: needsKey 要是 true / false")

    access = str(catalog.get("accessKey") or "")
    if not access or any(ch.isspace() for ch in access):
        problems.append("accessKey 不能为空，也不能带空白")
    site = str(catalog.get("site") or "")
    if not site.startswith("https://") or site.rstrip("/").count("/") != 2:
        problems.append("site 要写成 https://域名，不带路径")

    today = datetime.date.today().isoformat()
    status = {
        "schema": catalog.get("schema", 1),
        "updated": today,
        "site": site,
        "online": True,
        "marketEnabled": bool(catalog.get("marketEnabled", True)),
        "marketItems": items,
        "endpointCount": len(endpoints),
        "notice": str(catalog.get("notice") or ""),
        "adminKeyInterval": 600,
    }
    interfaces = {
        "schema": catalog.get("schema", 1),
        "updated": today,
        "site": site,
        "accessKey": access,
        "endpoints": endpoints,
    }
    latest = {
        "schema": 1,
        "version": version,
        "updated": today,
        "releaseUrl": GITHUB_RELEASES,
        "githubApi": GITHUB_API,
        "note": "客户端会同时问服务端和 GitHub，取两者里更新的那个版本。",
    }
    return {
        "status.json": json_bytes(status),
        "interfaces.json": json_bytes(interfaces),
        "version.json": json_bytes(latest),
    }, problems


def verify(root, payloads, problems):
    """产物自检：现场生成的字节必须和磁盘上的逐字节相同。"""
    if problems:
        return problems
    issues = []
    for name in FILES:
        path = root / "pcv-api" / name
        if not path.is_file():
            issues.append(f"缺产物 {name}")
        elif path.read_bytes() != payloads[name]:
            issues.append(f"{name} 与真相源不一致，重跑本脚本")
    return issues


def sync_blog(blog_root, payloads):
    report = []
    for relative in BLOG_TARGETS:
        target = blog_root / relative
        target.mkdir(parents=True, exist_ok=True)
        for name in FILES:
            (target / name).write_bytes(payloads[name])
        expected = {n: hashlib.md5(payloads[n]).hexdigest() for n in FILES}
        actual = {n: hashlib.md5((target / n).read_bytes()).hexdigest()
                  if (target / n).is_file() else "缺失" for n in FILES}
        bad = [n for n in FILES if expected[n] != actual[n]]
        # 清掉不属于本清单的旧文件，避免线上留着过期的 json
        for path in target.iterdir():
            if path.is_file() and path.name not in expected:
                path.unlink()
        report.append((str(relative), bad))
    return report


def inject_fault():
    """改坏一份临时副本，确认校验器真的会报红——不通过就不许当校验用。"""
    probe = pathlib.Path(tempfile.mkdtemp(prefix="pcv-api-probe-")) / "repo"
    shutil.copytree(ROOT, probe, ignore=shutil.ignore_patterns(
        "*.exe", "*.dll", "*.pdb", "*.mp4", "*.png", "*.jpg", "bin", "obj", ".git"))
    results = []

    def scene(name, mutate, keyword):
        target = probe / "pcv-api" / "catalog.json"
        backup = target.read_bytes()
        try:
            target.write_bytes(mutate(backup))
            payloads, problems = build(probe)
            issues = verify(probe, payloads, problems)
            hit = any(keyword in line for line in issues + problems)
            results.append((name, hit, "" if hit else "没报这一项：" + " | ".join(issues + problems)[:160]))
        finally:
            target.write_bytes(backup)

    def bad_path(data):
        obj = json.loads(data.decode("utf-8"))
        obj["endpoints"][0]["path"] = "https://evil.example.com/steal"
        return json.dumps(obj, ensure_ascii=False, indent=2).encode("utf-8")

    def bad_key(data):
        obj = json.loads(data.decode("utf-8"))
        obj["accessKey"] = ""
        return json.dumps(obj, ensure_ascii=False, indent=2).encode("utf-8")

    def bad_version(data):
        installer = probe / "installer/PCV.Setup/Installer.cs"
        backup = installer.read_bytes()
        try:
            installer.write_bytes(backup.replace(b'"3.1.1"', b'"0.0.1"'))
            payloads, problems = build(probe)
            issues = verify(probe, payloads, problems)
            # 版本号变了，产物必须跟着变；如果校验器没发现产物过期就是漏检
            hit = any("version.json" in line for line in issues) or any("不一致" in line for line in issues)
            results.append(("版本号改了产物会跟着变/被抓到", hit,
                            "" if hit else "校验器没发现产物过期"))
        finally:
            installer.write_bytes(backup)

    scene("接口拼成外部 URL 会报红", bad_path, "path")
    scene("接入密钥被清空会报红", bad_key, "accessKey")
    bad_version(None)
    shutil.rmtree(probe.parent, ignore_errors=True)

    print("== 在线服务数据校验器反向验证（注入已知故障） ==")
    print()
    failed = 0
    for name, ok, detail in results:
        print(("PASS  " if ok else "FAIL  ") + name + ("  ← " + detail if detail else ""))
        failed += 0 if ok else 1
    print()
    print(f"{len(results) - failed}/{len(results)} 个故障被正确抓到")
    return 0 if failed == 0 else 1


def main():
    parser = argparse.ArgumentParser(description="生成并校验 PCV 在线服务数据")
    parser.add_argument("--check", action="store_true", help="只校验，不写文件")
    parser.add_argument("--blog", help="博客项目根目录，把三份产物同步到三处")
    parser.add_argument("--inject-fault", action="store_true", help="反向验证校验器本身")
    args = parser.parse_args()

    if args.inject_fault:
        return inject_fault()

    payloads, problems = build(ROOT)
    if problems:
        print("数据不能用：")
        for line in problems:
            print("  ✗ " + line)
        return 1

    if not args.check:
        for name in FILES:
            (API_DIR / name).write_bytes(payloads[name])
        print("已生成：" + "、".join(f"{n}（{len(payloads[n])} 字节）" for n in FILES))

    issues = verify(ROOT, payloads, problems)
    if issues:
        print("校验没过：")
        for line in issues:
            print("  ✗ " + line)
        return 1
    print("校验通过：三份产物与 catalog.json / Installer.cs / market/index.json 一致")

    if args.blog:
        print()
        blog = pathlib.Path(args.blog).resolve()
        if not (blog / "blog" / "public").is_dir() or not (blog / "server" / "web").is_dir():
            print("博客根目录不对：找不到 blog/public 或 server/web")
            return 1
        for relative, bad in sync_blog(blog, payloads):
            print(f"  {relative}: {len(FILES)} 个文件 MD5 校验 " + ("OK" if not bad else "FAIL " + ",".join(bad)))
            if bad:
                return 1
    return 0


if __name__ == "__main__":
    main()
