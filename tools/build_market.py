# -*- coding: utf-8 -*-
"""从 market/catalog.json 生成市场清单 + 同步到博客静态目录。

为什么要有这个脚本（而不是手抄一份 index.json）：
    清单里的 size / sha256 / version 会随每次重新打包变化。手抄的结局一定
    是「客户端下完包，校验和不匹配，装不了」，而且没人会想到去查清单。
    所以这里让 dist/ 里的包当真相源，脚本现场读出这三个值填进 index.json。

catalog.json（人工维护）→ index.json（产物，三处分发用同一份字节）：
    1. market/index.json        仓库内，供 tools/check_repo.py 比对
    2. assets 内嵌资源           Pcv.Shell.csproj 直接引用 market/index.json
    3. 博客 public/dist/web     PCV 客户端从线上拉的那一份

用法：
    python tools/build_market.py                         # 生成 + 自校验
    python tools/build_market.py --check                 # 只校验，不写
    python tools/build_market.py --blog <博客根目录>     # 额外同步到博客三处
    python tools/build_market.py --inject-fault          # 反向验证：改坏一份，确认校验会报红

--inject-fault 是硬性要求：一个从不报红的校验等于没有校验。
"""
import argparse
import datetime
import hashlib
import io
import json
import os
import pathlib
import shutil
import sys
import tempfile
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
CATALOG = ROOT / "market" / "catalog.json"
INDEX = ROOT / "market" / "index.json"
DIST = ROOT / "dist"
# 博客侧三处：源码 public（Astro 原样拷贝）、构建产物 dist、发布用 server/web
BLOG_TARGETS = [
    pathlib.Path("blog/public/market"),
    pathlib.Path("blog/dist/market"),
    pathlib.Path("server/web/market"),
]
# Worker 里的市场直出分支用这两个标记框住，方便幂等替换（不要手抄两份 JS）
MARKET_START = "    // ── PCV 市场：静态直出"
MARKET_END = "    // ── 访问密码：解锁入口 ──"


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def plain_filename(name):
    """清单里的 file 只能是纯文件名，不许拼出任意 URL。"""
    return (isinstance(name, str) and name and "/" not in name and "\\" not in name
            and ".." not in name and name.lower().endswith(".pclpkg"))


def manifest_of(package_path):
    """从 .pclpkg 里读 module.json，不解压到磁盘。"""
    with zipfile.ZipFile(package_path) as archive:
        names = [n for n in archive.namelist() if n.replace("\\", "/").lower() == "module.json"]
        if not names:
            raise ValueError("包里没有 module.json")
        return json.loads(archive.read(names[0]).decode("utf-8-sig"))


def build(root):
    """返回 (index 对象, 问题列表)。问题列表为空才允许写产物。"""
    catalog = json.loads((root / "market" / "catalog.json").read_text(encoding="utf-8"))
    problems = []
    items = []
    seen = set()

    for entry in catalog.get("items", []):
        file_name = entry.get("file") or ""
        if not plain_filename(file_name):
            problems.append(f"{entry.get('id')}: file 不是合法纯文件名 → {file_name}")
            continue
        module_id = (entry.get("id") or "").strip()
        if not module_id or module_id in seen:
            problems.append(f"id 缺失或重复：{module_id}")
            continue
        seen.add(module_id)

        package = root / "dist" / file_name
        if not package.is_file():
            problems.append(f"{module_id}: dist 里没有 {file_name}")
            continue
        try:
            manifest = manifest_of(package)
        except (ValueError, KeyError, zipfile.BadZipFile, UnicodeDecodeError) as error:
            problems.append(f"{module_id}: 包内 module.json 读不出来（{error}）")
            continue

        if manifest.get("id") != module_id:
            problems.append(f"{module_id}: 包内 id 是 {manifest.get('id')}，对不上")
        version = manifest.get("version") or ""
        if version and version not in file_name:
            problems.append(f"{module_id}: 文件名没带包内版本 {version}")
        for field in ("name", "author", "summary", "category", "icon", "accent"):
            if not str(entry.get(field) or "").strip():
                problems.append(f"{module_id}: 清单缺 {field}")
                break

        items.append({
            "id": module_id,
            "name": entry["name"],
            "version": version,
            "author": entry["author"],
            "summary": entry["summary"],
            "category": entry["category"],
            "icon": entry["icon"],
            "accent": entry["accent"],
            "official": bool(entry.get("official", False)),
            "tags": entry.get("tags", []),
            "file": file_name,
            "size": package.stat().st_size,
            "sha256": sha256(package),
        })

    index = {
        "schema": catalog.get("schema", 1),
        "updated": datetime.date.today().isoformat(),
        "site": catalog.get("site", ""),
        "items": items,
    }
    return index, problems


def verify(root, index, problems):
    """产物自检：清单里每个字段都能和 dist 里的包对上。"""
    if problems:
        return problems
    local = []
    index_path = root / "market" / "index.json"
    if not index_path.is_file():
        return ["market/index.json 不存在"]
    try:
        on_disk = json.loads(index_path.read_text(encoding="utf-8"))
    except (ValueError, OSError) as error:
        return [f"market/index.json 读不出来：{error}"]
    if on_disk != index:
        local.append("market/index.json 与 dist 现状不一致，重跑本脚本")
    # 即使整体不一致，也要把具体是哪项坏了说出来：
    # 「一句话概括」对修问题没用，定位到字段才有用。
    for item in on_disk.get("items", []):
        name = item.get("file") or ""
        if not plain_filename(name):
            local.append(f"{item.get('id')}: file 字段不合法 → {name}")
            continue
        package = root / "dist" / name
        if not package.is_file():
            local.append(f"{item.get('id')}: 指向的包不在 dist 里")
            continue
        if item.get("size") != package.stat().st_size:
            local.append(f"{item.get('id')}: size 不对")
        if item.get("sha256") != sha256(package):
            local.append(f"{item.get('id')}: sha256 不对")
    return local


def sync_blog(blog_root, index_bytes):
    """同一份字节写到博客三处，写完逐文件 MD5 校验（含包文件）。"""
    if not (blog_root / "blog" / "public").is_dir() or not (blog_root / "server" / "web").is_dir():
        raise SystemExit("博客根目录不对：找不到 blog/public 或 server/web")

    packages = sorted(item["file"] for item in json.loads(index_bytes.decode("utf-8"))["items"])
    report = []
    for relative in BLOG_TARGETS:
        target = blog_root / relative
        target.mkdir(parents=True, exist_ok=True)
        (target / "index.json").write_bytes(index_bytes)
        for name in packages:
            shutil.copy2(DIST / name, target / name)
        # 逐文件校验：源 / 产物逐个比对，不做「目录大小一致」这种假校验
        expected = {"index.json": hashlib.md5(index_bytes).hexdigest()}
        for name in packages:
            expected[name] = hashlib.md5((DIST / name).read_bytes()).hexdigest()
        actual = {}
        for name in expected:
            path = target / name
            actual[name] = hashlib.md5(path.read_bytes()).hexdigest() if path.is_file() else "缺失"
        bad = [name for name in expected if expected[name] != actual[name]]
        # 清掉目录里不属于本清单的旧包，避免线上留下装不上的孤儿
        for path in target.iterdir():
            if path.is_file() and path.name not in expected:
                path.unlink()
        report.append((str(relative), len(expected), bad))
    return report


def sync_routes_worker(blog_root):
    """把 public/ 下的 _routes.json 与 Worker 市场分支同步到 dist 和 server/web。

    为什么不能只改 public：发布脚本发的是 server/web，而 Astro 构建产物在
    blog/dist；三份不同步的话，本地看着能用、线上却拿不到清单（或拿到被
    文章改写器改坏的 JSON）。这类"两处各存一份然后烂掉"的情况必须闸住。
    """
    source_worker = blog_root / "blog/public/_worker.js"
    source_routes = blog_root / "blog/public/_routes.json"
    text = source_worker.read_text(encoding="utf-8")
    start = text.find(MARKET_START)
    end = text.find(MARKET_END, start)
    if start < 0 or end < 0:
        raise SystemExit("public/_worker.js 里找不到市场直出分支的起止标记，没法同步")
    snippet = text[start:end]

    routes = source_routes.read_bytes()
    if b"/market/*" not in routes:
        raise SystemExit("public/_routes.json 没有排除 /market/*，静态层会被 Worker 改写")

    report = []
    for relative in (pathlib.Path("blog/dist"), pathlib.Path("server/web")):
        target_dir = blog_root / relative
        (target_dir / "_routes.json").write_bytes(routes)

        worker = target_dir / "_worker.js"
        content = worker.read_text(encoding="utf-8")
        a = content.find(MARKET_START)
        if a >= 0:
            b = content.find(MARKET_END, a)
            if b < 0:
                raise SystemExit(f"{relative}/_worker.js 有起始标记却没有结束标记")
            content = content[:a] + snippet + content[b:]
        else:
            anchor = content.find(MARKET_END)
            if anchor < 0:
                raise SystemExit(f"{relative}/_worker.js 找不到插入锚点")
            content = content[:anchor] + snippet + content[anchor:]
        worker.write_text(content, encoding="utf-8")

        ok = (b"/market/*" in (target_dir / "_routes.json").read_bytes()
              and MARKET_START in worker.read_text(encoding="utf-8"))
        report.append((str(relative), ok))
    return report


def inject_fault():
    """造一份坏清单，确认 verify() 真的会红。"""
    probe = pathlib.Path(tempfile.mkdtemp(prefix="pcv-market-probe-")) / "repo"
    shutil.copytree(ROOT, probe, ignore=shutil.ignore_patterns(
        "*.exe", "*.dll", "*.pdb", "*.mp4", "*.png", "*.jpg", "bin", "obj", ".git"))
    results = []

    def scene(name, mutate, keyword):
        target = probe / "market" / "index.json"
        backup = target.read_bytes()
        try:
            target.write_bytes(mutate(backup))
            _, problems = build(probe)
            issues = verify(probe, json.loads(target.read_text(encoding="utf-8")), problems)
            hit = any(keyword in issue for issue in issues)
            results.append((name, hit, "" if hit else "没报这一项：" + " | ".join(issues)[:180]))
        finally:
            target.write_bytes(backup)

    def bad_hash(data):
        obj = json.loads(data.decode("utf-8"))
        obj["items"][0]["sha256"] = "0" * 64
        return json.dumps(obj, ensure_ascii=False, indent=2).encode("utf-8")

    def bad_size(data):
        obj = json.loads(data.decode("utf-8"))
        obj["items"][0]["size"] = 1
        return json.dumps(obj, ensure_ascii=False, indent=2).encode("utf-8")

    def bad_file(data):
        obj = json.loads(data.decode("utf-8"))
        obj["items"][0]["file"] = "https://example.com/evil.pclpkg"
        return json.dumps(obj, ensure_ascii=False, indent=2).encode("utf-8")

    scene("校验和被改会报红", bad_hash, "sha256")
    scene("大小被改会报红", bad_size, "size")
    scene("file 拼成外部 URL 会报红", bad_file, "file 字段不合法")

    shutil.rmtree(probe.parent, ignore_errors=True)
    print("== 市场清单校验器反向验证（注入已知故障） ==")
    print()
    for name, ok, detail in results:
        print(("PASS  " if ok else "FAIL  ") + name + ("  ← " + detail if detail else ""))
    failed = sum(1 for _, ok, _ in results if not ok)
    print()
    print(f"{len(results) - failed}/{len(results)} 个故障被正确抓到")
    return 0 if failed == 0 else 1


def main():
    parser = argparse.ArgumentParser(description="生成并校验 PCV 市场清单")
    parser.add_argument("--check", action="store_true", help="只校验，不写文件")
    parser.add_argument("--blog", help="博客项目根目录，把清单与包同步到三处")
    parser.add_argument("--inject-fault", action="store_true", help="反向验证校验器本身")
    args = parser.parse_args()

    if args.inject_fault:
        return inject_fault()

    index, problems = build(ROOT)
    if problems:
        print("清单不能用：")
        for item in problems:
            print("  ✗ " + item)
        return 1

    payload = (json.dumps(index, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    if not args.check:
        INDEX.write_bytes(payload)
        print(f"已生成 market/index.json：{len(index['items'])} 个条目，{len(payload)} 字节")
    else:
        print(f"只校验：{len(index['items'])} 个条目")

    issues = verify(ROOT, index, problems)
    if issues:
        print("校验没过：")
        for item in issues:
            print("  ✗ " + item)
        return 1
    print("校验通过：每个条目的 size / sha256 / version 都和 dist 里的包一致")

    if args.blog:
        print()
        blog = pathlib.Path(args.blog).resolve()
        reports = sync_blog(blog, payload)
        for relative, count, bad in reports:
            flag = "OK" if not bad else "FAIL " + ", ".join(bad)
            print(f"  {relative}: {count} 个文件 MD5 校验 {flag}")
        gate = sync_routes_worker(blog)
        for relative, ok in gate:
            print(f"  {relative}: _routes.json / _worker.js 同步 {'OK' if ok else 'FAIL'}")
        if any(bad for _, _, bad in reports):
            return 1
        if any(not ok for _, ok in gate):
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
