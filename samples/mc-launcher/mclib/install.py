# -*- coding: utf-8 -*-
"""下载一个版本需要的一切：版本 JSON、client.jar、libraries、natives、assets、日志配置。

重跑是安全的：已有且 sha1 对得上的文件直接跳过，所以断网重来不会从头下。
"""
import concurrent.futures
import json
import os
import zipfile

import meta
import net


def layout(root):
    return {
        "root": root,
        "versions": os.path.join(root, "versions"),
        "libraries": os.path.join(root, "libraries"),
        "assets": os.path.join(root, "assets"),
        "indexes": os.path.join(root, "assets", "indexes"),
        "objects": os.path.join(root, "assets", "objects"),
    }


def installed_versions(root):
    folder = os.path.join(root, "versions")
    if not os.path.isdir(folder):
        return []
    result = []
    for name in sorted(os.listdir(folder)):
        jar = os.path.join(folder, name, name + ".jar")
        data = os.path.join(folder, name, name + ".json")
        if os.path.isfile(jar) and os.path.isfile(data):
            result.append(name)
    return result


def install(version_id, root, say, control, skip_assets=False):
    paths = layout(root)
    os.makedirs(paths["versions"], exist_ok=True)

    say("→ 读版本清单…")
    entry = meta.find_version(net.get_json(meta.VERSION_MANIFEST), version_id)
    version_json = meta.fetch_version_json(entry)

    version_dir = os.path.join(paths["versions"], version_id)
    os.makedirs(version_dir, exist_ok=True)
    version_file = os.path.join(version_dir, version_id + ".json")
    with open(version_file, "w", encoding="utf-8") as handle:
        json.dump(version_json, handle, ensure_ascii=False, indent=2)
    say("  版本 JSON 就位：%s（类型 %s，需要 Java %s）"
        % (version_id, version_json.get("type", "?"), _java_major(version_json)))

    client = (version_json.get("downloads") or {}).get("client")
    if not client:
        raise RuntimeError("这个版本的 JSON 里没有 client.jar 下载地址")
    jar_path = os.path.join(version_dir, version_id + ".jar")
    control({"type": "title", "text": "下载 " + version_id})
    if net.download(client["url"], jar_path, client.get("sha1"), client.get("size")):
        say("  客户端 jar：%s（%.1f MB）" % ("已下载", os.path.getsize(jar_path) / 1048576))
    else:
        say("  客户端 jar：已存在，跳过")

    _install_libraries(version_json, paths, version_dir, say, control)
    _install_logging(version_json, paths, say)

    if skip_assets:
        say("→ 跳过 assets（--skip-assets）")
    else:
        _install_assets(version_json, paths, say, control)

    return version_file


def _java_major(version_json):
    return (version_json.get("javaVersion") or {}).get("majorVersion", 8)


def _install_libraries(version_json, paths, version_dir, say, control):
    libraries = version_json.get("libraries", [])
    natives_dir = os.path.join(version_dir, "natives")
    os.makedirs(natives_dir, exist_ok=True)

    total = 0
    skipped = 0
    for index, library in enumerate(libraries, 1):
        if not meta.rule_allows(library.get("rules")):
            continue

        for item in meta.library_files(library, paths["libraries"]):
            try:
                downloaded = net.download(item["url"], item["path"], item.get("sha1"), item.get("size"))
            except Exception as error:  # noqa: BLE001
                raise RuntimeError("库下载失败 %s：%s" % (os.path.basename(item["path"]), error))

            total += 1
            if not downloaded:
                skipped += 1

            # 新版 Minecraft 把 natives 拆成了独立库（名字带 :natives-windows），
            # 不再有 library.natives 字段；所以不能只看 is_native，得看文件名。
            if item.get("is_native") or item["path"].lower().endswith(meta.natives_tag() + ".jar"):
                _extract_natives(item["path"], natives_dir)

        if index % 20 == 0:
            control({"type": "title", "text": "库 %d/%d" % (index, len(libraries))})

    extracted = len([n for n in os.listdir(natives_dir)]) if os.path.isdir(natives_dir) else 0
    say("  库：%d 个文件（新下 %d，跳过 %d），natives 解出 %d 个文件到 versions\\%s\\natives"
        % (total, total - skipped, skipped, extracted, os.path.basename(version_dir)))


def _extract_natives(jar_path, natives_dir):
    """把 natives 解到**扁平**目录里。

    LWJGL 3.4 的 natives jar 内部结构是 windows/x64/lwjgl.dll 这种分层路径，
    而 -Djava.library.path 只会到目录根找 lwjgl.dll —— 原样解压就是"找不到库"。
    所以只取文件名，平铺进 natives 目录。
    """
    try:
        with zipfile.ZipFile(jar_path) as archive:
            for name in archive.namelist():
                if name.endswith("/"):
                    continue
                base = os.path.basename(name)
                if not base.lower().endswith((".dll", ".so", ".dylib")):
                    continue
                target = os.path.join(natives_dir, base)
                try:
                    with archive.open(name) as source, open(target, "wb") as destination:
                        destination.write(source.read())
                except OSError:
                    # 文件被占用（比如上次的游戏进程还没退干净）就跳过，不致命
                    pass
    except Exception:  # noqa: BLE001
        pass


def _install_logging(version_json, paths, say):
    logging = (version_json.get("logging") or {}).get("client")
    if not logging:
        return
    target = os.path.join(paths["assets"], "log_configs", os.path.basename(logging["file"]["id"]))
    if net.download(logging["file"]["url"], target, logging["file"].get("sha1"), logging["file"].get("size")):
        say("  日志配置：已下载")
    return target


def _install_assets(version_json, paths, say, control):
    index = version_json.get("assetIndex")
    if not index:
        say("  这个版本没有 assetIndex，跳过资源")
        return

    os.makedirs(paths["indexes"], exist_ok=True)
    index_file = os.path.join(paths["indexes"], index["id"] + ".json")
    if net.download(index["url"], index_file, index.get("sha1"), index.get("size")):
        say("  资源索引：已下载（%s）" % index["id"])
    else:
        say("  资源索引：已存在")

    with open(index_file, encoding="utf-8") as handle:
        objects = json.load(handle).get("objects", {})

    total = len(objects)
    # 串行下 3000 个文件太慢（实测也就 2 个/秒），这里开线程池。
    # 每个文件是独立的，失败各自重试，不影响别人。
    # 同一个 hash 可能在索引里出现多次（不同名字指向同一份内容），
    # 不去重就会出现两个线程抢同一个 .part 文件 —— 上一轮就撞出了 WinError 32。
    pending = {}
    for name, item in objects.items():
        digest = item["hash"]
        target = os.path.join(paths["objects"], digest[:2], digest)
        if not os.path.isfile(target):
            pending[digest] = (target, item.get("size"))
    pending = [(d, t, s2) for d, (t, s2) in pending.items()]

    say("  资源：共 %d 个对象，缺 %d 个，开 12 路并发" % (total, len(pending)))
    done = 0
    downloaded = 0
    lock = __import__("threading").Lock()

    def fetch(digest, target, size):
        url = "https://resources.download.minecraft.net/%s/%s" % (digest[:2], digest)
        net.download(url, target, digest, size)
        return True

    with concurrent.futures.ThreadPoolExecutor(max_workers=12) as pool:
        futures = [pool.submit(fetch, d, t, s2) for d, t, s2 in pending]
        for future in concurrent.futures.as_completed(futures):
            with lock:
                done += 1
                try:
                    if future.result():
                        downloaded += 1
                except Exception as error:  # noqa: BLE001
                    say("    有个资源没下来（%s），下次 install 会补" % error)

                if done % 300 == 0:
                    control({"type": "title", "text": "资源 %d/%d" % (done, len(pending))})
                    say("    资源 %d/%d（新下 %d）" % (done, len(pending), downloaded))

    say("  资源：完成 %d 个（新下 %d，其余本来就有）" % (total, downloaded))
