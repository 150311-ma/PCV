# -*- coding: utf-8 -*-
"""拼启动命令并拉起来。Java 找不对就用大白话说清楚，不让用户猜。"""
import os
import re
import subprocess

import meta


def _candidate_javas():
    """把这台机器上能找到的 java.exe 都收进来，供按版本挑。"""
    found = []

    home = os.environ.get("JAVA_HOME")
    if home:
        found.append(os.path.join(home, "bin", "java.exe" if os.name == "nt" else "java"))

    path = os.environ.get("PATH", "")
    for folder in path.split(";" if os.name == "nt" else ":"):
        if folder.strip():
            found.append(os.path.join(folder.strip('"'), "java.exe" if os.name == "nt" else "java"))

    # 常见安装位置：换台机器也不用改代码
    roots = [
        r"C://Program Files//Java",
        r"C://Program Files//Eclipse Adoptium",
        r"C://Program Files//Zulu",
        r"C://Program Files//Microsoft",
        r"C://Program Files (x86)//Java",
        os.path.join(os.environ.get("LOCALAPPDATA", ""), "Programs", "Eclipse Adoptium"),
        "/usr/lib/jvm",
        "/Library/Java/JavaVirtualMachines",
    ]
    for root in roots:
        if not root or not os.path.isdir(root):
            continue
        try:
            for name in os.listdir(root):
                home_dir = os.path.join(root, name)
                if not os.path.isdir(home_dir):
                    continue
                found.append(os.path.join(home_dir, "bin", "java.exe" if os.name == "nt" else "java"))
                found.append(os.path.join(home_dir, "Contents", "Home", "bin", "java"))
        except OSError:
            pass

    seen = set()
    result = []
    for candidate in found:
        key = candidate.lower()
        if key in seen or not os.path.isfile(candidate):
            continue
        seen.add(key)
        result.append(candidate)
    return result


def find_java(preferred=None, required=None):
    """挑 Java。

    有指定就用指定的；否则在机器上所有 JDK 里挑：先找主版本刚好等于 required 的，
    没有再找最小的"够新"的，最后退而求其次用最新的。
    这样一台机器装了好几个 JDK 也不会挑错 —— 换台机器同理。
    """
    if preferred and os.path.isfile(preferred):
        return preferred

    candidates = _candidate_javas()
    if not candidates:
        return None

    if required:
        scanned = [(find_one_major(candidate), candidate) for candidate in candidates]
        exact = [item for item in scanned if item[0] == required]
        if exact:
            return exact[0][1]
        newer = sorted([item for item in scanned if item[0] >= required], key=lambda item: item[0])
        if newer:
            return newer[0][1]
        return max(scanned, key=lambda item: item[0])[1]

    return candidates[0]


def find_one_major(java_path):
    return java_major(java_path)


def java_major(java_path):
    try:
        output = subprocess.run([java_path, "-version"], capture_output=True, text=True, timeout=20)
    except Exception:  # noqa: BLE001
        return 0

    text = (output.stderr or "") + (output.stdout or "")
    match = re.search(r'version "(\d+)(?:\.(\d+))?', text)
    if not match:
        return 0

    major = int(match.group(1))
    if major == 1 and match.group(2):     # 1.8.0_xxx 这种老写法
        return int(match.group(2))
    return major


def build_command(java_path, version_json, version_id, paths, version_dir, account,
                  memory_mb=2048, game_dir=None, extra_jvm=None):
    game_dir = game_dir or paths["root"]
    natives = os.path.join(version_dir, "natives")
    classpath = os.pathsep.join(meta.classpath_entries(
        version_json, paths["libraries"], os.path.join(version_dir, version_id + ".jar")))

    # 1.13 之后才有 arguments；更老的用 minecraftArguments，meta 里已经兼容
    values = {
        "auth_player_name": account["name"],
        "version_name": version_id,
        "game_directory": game_dir,
        "assets_root": paths["assets"],
        "assets_index_name": (version_json.get("assetIndex") or {}).get("id", version_id),
        "auth_uuid": account["uuid"].replace("-", ""),
        "auth_access_token": account["access_token"],
        "auth_session": account["access_token"],
        "user_type": account["user_type"],
        "version_type": version_json.get("type", "release"),
        "natives_directory": natives,
        "launcher_name": "PCV",
        "launcher_version": "1.0.0",
        "classpath": classpath,
        "classpath_separator": os.pathsep,
        "library_directory": paths["libraries"],
        "auth_xuid": account.get("xuid", ""),
        "clientid": "",
        "user_properties": "{}",
    }

    command = [java_path]
    command += ["-Xmx%dm" % memory_mb, "-Xms%dm" % min(512, memory_mb)]
    command += (extra_jvm or [])

    logging = (version_json.get("logging") or {}).get("client")
    if logging:
        log_file = os.path.join(paths["assets"], "log_configs", os.path.basename(logging["file"]["id"]))
        if os.path.isfile(log_file):
            command.append("-Dlog4j.configurationFile=" + log_file)

    command += meta.jvm_arguments(version_json, values)
    if "-cp" not in command and "-classpath" not in command:
        command += ["-cp", classpath]

    command.append(version_json["mainClass"])
    command += meta.game_arguments(version_json, values)
    return command


def launch(command, game_dir, say):
    say("→ 启动命令（截断显示）：")
    joined = " ".join('"%s"' % part if " " in part else part for part in command)
    say("  " + (joined[:900] + ("…" if len(joined) > 900 else "")))

    process = subprocess.Popen(command, cwd=game_dir,
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               text=True, encoding="utf-8", errors="replace",
                               bufsize=1)
    return process
