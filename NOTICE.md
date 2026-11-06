# 来源与许可

## 这是什么

PCV（PAINCHARNWAV）是**第三方基于 PCL 独立进行的二次创作**，与 PCL 官方无关，也不代表官方立场。

- 上游仓库：<https://github.com/Meloong-Git/PCL>
- 上游作者：龙腾猫跃
- 本仓库：<https://github.com/150311-ma/PCV>（独立仓库，不再是 PCL 的分支）

## 整体许可：沿用上游文件

仓库根目录的 `LICENCE`（《PCL 分发有限许可》+《PCL 存储库合理使用指南》）**原样保留**，
继续作为本项目的许可依据，没有替换、也没有另起一套。

本项目**只剩一个**从上游继承而来的模块，版权归原作者，按该文件执行：

- `src/PCL.Launch.Microsoft/`（微软登录，按你的要求保留）

其余部分（`Pcv.Core` / `Pcv.Theme` / `Pcv.Modular.*` / `Pcv.Shell` / `Pcv.*` / `installer` / `tools` / `samples`）
已经全部重写，不含上游代码。

## 新增部分：MIT

以下目录是这次改造里**新写的**，不含上游代码，按 MIT 授权：

```
modular/src/Pcv.Modular.Abstractions/    契约（接口、清单、服务）
modular/src/Pcv.Modular.Host/            程序集加载器
modular/src/Pcv.Modular.Packages/        包校验与安装
modular/src/Pcv.Modular.Runtime/         进程型框架运行时
modular/src/Pcv.Modular.Cli/             命令行
modular/src/Pcv.Shell/                   壳（界面、构建、安装）
modular/src/Pcv.*/                       自检 / 统计 / 清理
modular/installer/                       安装程序
modular/tools/                           打包与构建脚本
modular/samples/                         框架与软件示例
modular/assets/prompt-framework.md       给 AI 的提示词
```

```
MIT License

Copyright (c) 2026 150311-ma

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## 与上游要求的关系（逐条对齐）

上游对"重度使用"（基于本仓库修改）提了五条，本项目的状态：

| # | 上游要求 | 状态 |
| --- | --- | --- |
| 1 | 明确表示是第三方独立二次创作 | ✅ 程序内「版权」页 + 标题栏"基于 PCL 修改" + 本文件 |
| 2 | 名称以 `Plain Craft Launcher (PCL)` 开头 + 第三方后缀 | ⚠️ 仍叫 PAINCHARNWAV / PCV，**未改名**（涉及产品命名，等作者定夺） |
| 3 | 关于页首位署名 + 赞助链接 + 公开源码 + 沿用本文件 | ✅ 程序内「版权」页即关于页，署名龙腾猫跃并给出 GitHub 与爱发电链接 |
| 4 | 含 MC 启动功能须有正版购买劝导 | ✅ Minecraft 框架在离线登录启动前打印正版劝导 |
| 5 | 不得有类似赞助解锁的功能 / 主色相须为蓝色 | ✅ 无赞助解锁；主色相为 PCL 蓝 |

### 关于"能不能算轻度使用"

**不能靠声明改变**，但可以靠改代码改变。上一版还带着 `PCL.Core` / `PCL.Theme`，
那是重度使用；现在这两个模块已经重写（`Pcv.Core` / `Pcv.Theme`），
只剩一个 571 行的微软登录模块仍是上游衍生。

严格说仍是「重度使用」（因为还有那一个模块 + MC 启动功能），
所以上面五条继续照做；等哪天登录模块也重写完，才谈得上"轻度"。

### 现状

未改名之前：**仅供个人学习与非商业分发**。要商用、改名后再分发，先把第 2 条处理掉。

## 如果你要用这里的代码

- 只想用"加载器 + 契约 + 进程运行时"这一层（`Pcv.Modular.*`）：MIT，署名即可，随便用。
- 想连同 `PCL.Core` / `PCL.Theme` / `PCL.Launch.Microsoft` 一起用：请先读仓库根目录的 `LICENCE`，
  那是上游的规矩，不是我的。
