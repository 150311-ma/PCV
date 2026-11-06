# -*- coding: utf-8 -*-
"""版本清单与规则：把 Mojang 那套 JSON 读成"该下什么、该怎么拼 classpath"。"""
import os
import platform
import re

import net

VERSION_MANIFEST = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"


def pick_manifest(which="release"):
    manifest = net.get_json(VERSION_MANIFEST)
    latest = manifest.get("latest", {})
    if which in latest:
        return latest[which], manifest
    return which, manifest


def find_version(manifest, version_id):
    for item in manifest["versions"]:
        if item["id"] == version_id:
            return item
    raise RuntimeError("版本清单里没有 " + version_id)


def fetch_version_json(version_entry):
    return net.get_json(version_entry["url"])


def current_os():
    if os.name == "nt":
        return "windows"
    if platform.system() == "Darwin":
        return "osx"
    return "linux"


def current_arch():
    machine = platform.machine().lower()
    if machine in ("amd64", "x86_64", "x64"):
        return "x86_64"
    if machine in ("arm64", "aarch64"):
        return "arm64"
    return machine or "x86"


def rule_allows(rules, os_name=None, arch=None):
    """rules 里只要有一条"命中且 action=allow"就放行；命中 disallow 就拒绝。

    空 rules 一律放行（老版本没有 rules 字段）。
    """
    if not rules:
        return True

    os_name = os_name or current_os()
    arch = arch or current_arch()

    allowed = False
    for rule in rules:
        action = rule.get("action")
        # features 是"启动器要不要开某个特性"（quick play / demo 之类）。
        # 我们一律不开，所以带 features 的规则直接不匹配 ——
        # 否则 1.20+ 会把好几个 quickPlay 参数同时塞进去，游戏报
        # "Only one quick play option can be specified"。
        if rule.get("features"):
            continue

        conditions = rule.get("os") or {}
        if "name" in conditions and conditions["name"] != os_name:
            continue
        if "arch" in conditions and conditions["arch"] != arch:
            # Mojang 的规则里 x86 就是 32 位；x64 机器不该把 arm64 / x86 的 natives 一起带上
            continue
        if "version" in conditions:
            try:
                if not re.search(conditions["version"], platform.release()):
                    continue
            except re.error:
                pass

        if action == "disallow":
            return False
        if action == "allow":
            allowed = True
    return allowed


def natives_tag():
    """当前平台对应的 natives 后缀。x64 就是 natives-windows 本身，不带 -x86。"""
    os_name = current_os()
    arch = current_arch()

    if os_name == "windows":
        return "natives-windows" if arch == "x86_64" else "natives-windows-" + arch
    if os_name == "osx":
        return "natives-macos" if arch == "x86_64" else "natives-macos-arm64"
    return "natives-linux" if arch == "x86_64" else "natives-linux-arm64"


def is_native_library(library):
    return ":natives-" in (library.get("name") or "")


def library_files(library, libraries_dir):
    """一个 library 条目可能给出多个文件：普通 jar + natives。"""
    files = []
    downloads = library.get("downloads") or {}
    artifact = downloads.get("artifact")
    if artifact:
        files.append(_artifact_entry(artifact, libraries_dir))

    natives = library.get("natives") or {}
    classifier = natives.get(current_os())
    if classifier:
        classifier = classifier.replace("${arch}", "64" if current_arch() == "x86_64" else "32")
        native = (downloads.get("classifiers") or {}).get(classifier)
        if native:
            files.append(_artifact_entry(native, libraries_dir, is_native=True))

    # 老格式：没有 downloads 字段，只有 name + url
    if not artifact and library.get("url"):
        path = _path_from_name(library["name"], libraries_dir)
        files.append({"path": path, "url": library["url"], "sha1": None, "size": None})
    return files


def _artifact_entry(artifact, libraries_dir, is_native=False):
    return {
        "path": os.path.join(libraries_dir, artifact["path"].replace("/", os.sep)),
        "url": artifact["url"],
        "sha1": artifact.get("sha1"),
        "size": artifact.get("size"),
        "is_native": is_native,
    }


def _path_from_name(name, libraries_dir):
    group, artifact, version = name.split(":")
    return os.path.join(libraries_dir, group.replace(".", os.sep), artifact, version,
                        "%s-%s.jar" % (artifact, version))


def classpath_entries(version_json, libraries_dir, client_jar):
    entries = [client_jar]
    for library in version_json.get("libraries", []):
        if not rule_allows(library.get("rules")):
            continue
        # natives jar：只留当前平台那一份留。
        # 为什么要留在 classpath 上：LWJGL 3.4 会自己从 classpath 上的 natives jar 里取库，
        # 只靠 java.library.path 找不到 lwjgl.dll（实测就是这么挂的）。
        if is_native_library(library) and not (library.get("name") or "").endswith(natives_tag()):
            continue
        downloads = library.get("downloads") or {}
        artifact = downloads.get("artifact")
        if artifact:
            entries.append(os.path.join(libraries_dir, artifact["path"].replace("/", os.sep)))
        elif library.get("url"):
            entries.append(_path_from_name(library["name"], libraries_dir))
    return entries


def jvm_arguments(version_json, values):
    arguments = version_json.get("arguments") or {}
    jvm = arguments.get("jvm")
    if jvm is None:
        # 极老的版本：给一套能用的默认值
        jvm = ["-Djava.library.path=${natives_directory}", "-cp", "${classpath}"]
    return _expand(jvm, values)


def game_arguments(version_json, values):
    arguments = version_json.get("arguments") or {}
    game = arguments.get("game")
    if game is None:
        legacy = version_json.get("minecraftArguments", "")
        game = legacy.split(" ") if legacy else []
    return _expand(game, values)


def _expand(items, values):
    result = []
    index = 0
    while index < len(items):
        item = items[index]
        if isinstance(item, dict):
            if rule_allows(item.get("rules")):
                value = item.get("value")
                if isinstance(value, list):
                    result.extend(_substitute(v, values) for v in value)
                elif value is not None:
                    result.append(_substitute(value, values))
            index += 1
            continue
        result.append(_substitute(item, values))
        index += 1
    return result


def _substitute(text, values):
    def replace(match):
        key = match.group(1)
        return str(values.get(key, ""))
    return re.sub(r"\$\{([^}]+)\}", replace, text)
