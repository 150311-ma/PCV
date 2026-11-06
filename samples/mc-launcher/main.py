# -*- coding: utf-8 -*-
"""PCV Minecraft 框架：下载、登录、启动。

在窗口下面那行输入框里敲命令：

    login offline Steve        离线登录（UUID 按 OfflinePlayer:名字 算）
    login ms <ClientId>        微软正版，走设备码
    versions 20                看一下最近的版本
    install 1.20.1             下载：版本 JSON / client.jar / 库 / natives / 资源
    install 1.20.1 --skip-assets   跳过资源（只想快点试启动时用）
    run 1.20.1                 启动
    run 1.20.1 --dry           只打印启动命令，不真的开
    list                       已经装好了哪些
    java                       看当前用的是哪个 Java
    java <路径>                指定 Java
    set memory 4096            改内存（MB）
"""
import json
import os
import sys
import threading

# mclib 里的模块互相 import 时用的是扁平名字（import net / import meta），
# 所以先把那个目录塞进 sys.path，别搞成需要相对导入的包。
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "mclib"))

import auth        # noqa: E402
import install     # noqa: E402
import launch      # noqa: E402
import meta        # noqa: E402
import net         # noqa: E402

DATA_DIR = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "data")
GAME_PROCESS = None
STATE_FILE = os.path.join(DATA_DIR, "state.json")

say = net.say
control = net.control


# ── 状态 ────────────────────────────────────────────────────────────────────

def default_state():
    return {
        "root": os.path.join(DATA_DIR, "minecraft"),
        "account": None,
        "java": "",
        "memory": 2048,
        "client_id": "",
    }


def load_state():
    state = default_state()
    try:
        with open(STATE_FILE, encoding="utf-8") as handle:
            state.update(json.load(handle))
    except (OSError, ValueError):
        pass
    return state


def save_state(state):
    try:
        os.makedirs(DATA_DIR, exist_ok=True)
        with open(STATE_FILE, "w", encoding="utf-8") as handle:
            json.dump(state, handle, ensure_ascii=False, indent=2)
    except OSError as error:
        say("（配置没存上：%s）" % error)


# ── 命令 ────────────────────────────────────────────────────────────────────

def cmd_help(_state, _args):
    say("命令：")
    say("  login offline <名字>        离线登录")
    say("  login ms [ClientId]         微软正版（设备码，浏览器输码）")
    say("  versions [数量]             列出最近的正式版")
    say("  install <版本> [--skip-assets]")
    say("  run <版本> [--dry] [--memory 4096]")
    say("  list / java <路径> / set memory 4096")


def cmd_versions(_state, args):
    count = int(args[0]) if args and args[0].isdigit() else 12
    manifest = net.get_json(meta.VERSION_MANIFEST)
    releases = [v for v in manifest["versions"] if v["type"] == "release"][:count]
    say("最近的正式版：")
    for item in releases:
        say("  %-14s %s" % (item["id"], item.get("releaseTime", "")[:10]))
    say("最新正式版：%s" % manifest["latest"]["release"])


def cmd_list(state, _args):
    versions = install.installed_versions(state["root"])
    if not versions:
        say("还没装任何版本。先 `install 1.20.1`。")
        return
    say("已装 %d 个版本（%s）：" % (len(versions), state["root"]))
    for version in versions:
        say("  " + version)


def cmd_java(state, args):
    if args:
        state["java"] = args[0]
        save_state(state)
        say("Java 指定为：" + args[0])
    path = launch.find_java(state.get("java") or None)
    if not path:
        say("没找到 Java。装个 JDK 8（老版本）或 17（1.17+），或者 `java <路径>` 指一下。")
        return
    say("Java：%s（主版本 %d）" % (path, launch.java_major(path)))


def cmd_login(state, args):
    if not args:
        say("用法：login offline <名字> / login ms [ClientId]")
        return

    kind = args[0].lower()
    if kind == "offline":
        if len(args) < 2:
            say("用法：login offline <名字>")
            return
        account = auth.offline(args[1])
        state["account"] = account
        save_state(state)
        control({"type": "title", "text": "离线：" + account["name"]})
        say("离线登录成功：%s（UUID %s）" % (account["name"], account["uuid"]))
        return

    if kind in ("ms", "microsoft"):
        client_id = args[1] if len(args) > 1 else (state.get("client_id") or os.environ.get("PCL_MS_CLIENT_ID", ""))
        if args[1:]:
            state["client_id"] = args[1]
            save_state(state)
        try:
            account = auth.microsoft(client_id, lambda text: say("  " + text))
        except Exception as error:  # noqa: BLE001
            say("登录失败：" + str(error))
            return
        state["account"] = account
        save_state(state)
        control({"type": "title", "text": "正版：" + account["name"]})
        say("正版登录成功：%s（UUID %s）" % (account["name"], account["uuid"]))
        return

    say("不认识的登录方式：" + kind)


def cmd_install(state, args):
    if not args:
        say("用法：install <版本> [--skip-assets]")
        return
    version = args[0]
    skip_assets = "--skip-assets" in args
    try:
        install.install(version, state["root"], say, control, skip_assets=skip_assets)
        say("装好了：%s" % version)
        say("下一步：run %s" % version)
    except Exception as error:  # noqa: BLE001
        say("下载中断：" + str(error))


def cmd_run(state, args):
    if not args:
        say("用法：run <版本> [--dry]")
        return

    version_id = args[0]
    dry = "--dry" in args
    memory = state.get("memory", 2048)
    if "--memory" in args:
        index = args.index("--memory")
        if index + 1 < len(args):
            try:
                memory = int(args[index + 1])
                state["memory"] = memory
                save_state(state)
            except ValueError:
                say("--memory 后面要跟数字")
                return

    account = state.get("account")
    if not account:
        say("还没登录。先 `login offline 名字` 或 `login ms <ClientId>`。")
        return

    version_dir = os.path.join(state["root"], "versions", version_id)
    version_file = os.path.join(version_dir, version_id + ".json")
    if not os.path.isfile(version_file):
        say("没装 %s。先 `install %s`。" % (version_id, version_id))
        return

    with open(version_file, encoding="utf-8") as handle:
        version_json = json.load(handle)

    need = (version_json.get("javaVersion") or {}).get("majorVersion", 8)
    java = launch.find_java(state.get("java") or None, need)
    if not java:
        say("找不到 Java，启动不了。装个 JDK 或者 `java <路径>`。")
        return
    have = launch.java_major(java)
    if have and have < need:
        say("这个版本要 Java %d，现在是 %d —— 高版本游戏用 Java 8 是起不来的。" % (need, have))
        return
    if have and have > need + 4:
        say("提醒：这个版本官方要求 Java %d，你用的是 %d。" % (need, have))
        say("     版本太新时 LWJGL 加载原生库会崩（org.lwjgl.system.Library）。崩了就先换 Java %d。" % (need, have, need))

    paths = install.layout(state["root"])
    command = launch.build_command(java, version_json, version_id, paths, version_dir,
                                   account, memory_mb=memory)

    if dry:
        say("（--dry）启动命令：")
        for index, part in enumerate(command):
            say("  [%02d] %s" % (index, part))
        return

    control({"type": "title", "text": "运行 " + version_id})
    say("→ 正在启动 %s（Java %d，内存 %d MB）" % (version_id, have, memory))
    try:
        process = launch.launch(command, paths["root"], say)
    except Exception as error:  # noqa: BLE001
        say("启动失败：" + str(error))
        return

    global GAME_PROCESS
    GAME_PROCESS = process
    threading.Thread(target=_pump, args=(process,), daemon=True).start()
    say("游戏进程 PID=%d，日志会继续往这里冒。" % process.pid)


def _pump(process):
    try:
        for line in process.stdout:
            say("  " + line.rstrip())
    except Exception:  # noqa: BLE001
        pass
    code = process.wait()
    say("游戏退出了（退出码 %d）" % code)
    control({"type": "title", "text": "Minecraft 已退出"})


def cmd_set(state, args):
    if len(args) < 2:
        say("用法：set memory 4096")
        return
    if args[0] == "memory":
        try:
            state["memory"] = int(args[1])
            save_state(state)
            say("内存改成 %d MB" % state["memory"])
        except ValueError:
            say("memory 要数字")
    else:
        say("只支持 set memory")


COMMANDS = {
    "help": cmd_help, "?": cmd_help,
    "versions": cmd_versions,
    "list": cmd_list,
    "java": cmd_java,
    "login": cmd_login,
    "install": cmd_install,
    "run": cmd_run,
    "set": cmd_set,
}


# ── 主循环 ──────────────────────────────────────────────────────────────────

def banner(state):
    say("PCV Minecraft 框架")
    say("  数据目录：%s" % state["root"])
    versions = install.installed_versions(state["root"])
    say("  已装版本：%s" % ("、".join(versions) if versions else "（还没有）"))
    account = state.get("account")
    say("  账号：%s" % ("%s（%s）" % (account["name"], account["kind"]) if account else "未登录"))
    java = launch.find_java(state.get("java") or None)
    say("  Java：%s" % (java if java else "没找到，先装一个"))
    say("")
    say("敲 help 看命令。第一次用：#  login offline Steve  →  install 1.20.1  →  run 1.20.1")
    control({"type": "title", "text": "Minecraft"})


def main():
    state = load_state()
    banner(state)

    for raw in sys.stdin:
        line = raw.strip()
        if not line:
            continue

        # 宿主发来的控制消息
        if line.startswith("@@PCV "):
            try:
                message = json.loads(line[6:])
            except ValueError:
                continue
            if message.get("type") == "stop":
                break
            if message.get("type") == "peer":
                say("「%s」发来：%s" % (message.get("from"), message.get("text")))
            continue

        parts = line.split()
        command = parts[0].lower()
        handler = COMMANDS.get(command)
        if not handler:
            say("没这个命令：%s（敲 help）" % command)
            continue

        try:
            handler(state, parts[1:])
        except Exception as error:  # noqa: BLE001
            say("命令出错：%s: %s" % (type(error).__name__, error))

    # 输入没了不代表要关游戏：等它自己退出，别把刚拉起来的进程丢下
    if GAME_PROCESS is not None and GAME_PROCESS.poll() is None:
        say("输入已经关闭，但游戏还在跑，等它退出…")
        GAME_PROCESS.wait()

    say("再见。")


if __name__ == "__main__":
    main()
