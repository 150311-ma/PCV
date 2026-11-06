# -*- coding: utf-8 -*-
"""示例软件：自己不含任何框架，靠调用框架干活。

它满足"软件"的三条：
  1. 有界面 —— 宿主给的窗口就是它的界面；
  2. 调用框架 —— 启动时通过 peer 消息问 pcv.stat（一个框架）要数据；
  3. 不含框架 —— 包里只有一个 main.py 和一份清单，没有别的框架代码。
"""
import json
import sys


def say(text):
    sys.stdout.write(text + "\n")
    sys.stdout.flush()


def control(payload):
    sys.stdout.write("@@PCV " + json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()


say("PCV 示例软件（不是框架，注意区别）")
say("我自己什么都不算——能力是向框架要的。")
say("")
say("正在调用框架 pcv.py.demo …")

# 这就是"调用框架"：宿主只做转发，我不知道对方跑在哪、也不知道它内部怎么实现
control({"type": "call", "target": "pcv.py.demo", "text": "你好，我是示例软件"})
say("已经发出请求，回执会出现在下面。")
say("")
say("输入任意内容会原样回显；输入 exit 退出。")

for raw in sys.stdin:
    line = raw.strip()
    if not line:
        continue

    if line.startswith("@@PCV "):
        try:
            message = json.loads(line[6:])
        except ValueError:
            continue
        if message.get("type") == "peer":
            say("框架「%s」回了：%s" % (message.get("from"), message.get("text")))
        elif message.get("type") == "callFailed":
            say("调用失败：%s（大概那个框架没在跑，先把它开起来）" % message.get("reason"))
        continue

    if line == "exit":
        break
    say("收到：" + line)

say("关掉了。")
