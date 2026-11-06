# -*- coding: utf-8 -*-
"""最小的进程型框架：只用标准库，不需要任何 SDK。

跟宿主说话就两种行：
  · 普通 print —— 直接显示在窗口里
  · 以 @@PCV 开头的 JSON —— 控制消息（改标题、调别的框架）
"""
import json
import sys

DATA_DIR = sys.argv[1] if len(sys.argv) > 1 else "（没传）"
MODULE_ID = sys.argv[2] if len(sys.argv) > 2 else "pcv.py.demo"

print("Python 框架起来了：" + sys.version.split()[0])
print("可以直接在这里 print，也可以读输入。")
print("输入 peers 试试跟别的框架打招呼，输入 exit 退出。")


def send(payload):
    sys.stdout.write("@@PCV " + json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()


send({"type": "title", "text": "Python 示例框架"})

for raw in sys.stdin:
    line = raw.strip()
    if not line:
        continue

    # 宿主发来的控制消息（init / peer / stop）——框架自己决定怎么处理
    if line.startswith("@@PCV "):
        try:
            message = json.loads(line[6:])
        except ValueError:
            continue

        kind = message.get("type")
        if kind == "init":
            peers = message.get("peers") or []
            print("宿主告诉我：数据目录是 " + DATA_DIR)
            print("同时还在跑的框架：" + ("、".join(peers) if peers else "（只有我）"))
        elif kind == "peer":
            print("「%s」跟我说：%s" % (message.get("from"), message.get("text")))
        continue

    if line == "exit":
        break

    if line == "peers":
        # 互调：宿主只负责转发，我拿不到对方的进程信息
        send({"type": "call", "target": "pcv.stat", "text": "你在吗？我是 Python 示例"})
        print("已经通过宿主问了一下 pcv.stat")
        continue

    print("收到：" + line)

print("结束。")
