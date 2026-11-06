# 来源与许可

## 这是什么

PCV（PAINCHARNWAV）是**第三方基于 PCL 独立进行的二次创作**，与 PCL 官方无关，也不代表官方立场。

- 上游仓库：<https://github.com/Meloong-Git/PCL>
- 上游作者：龙腾猫跃
- 本仓库：<https://github.com/150311-ma/PCV>（独立仓库，不再是 PCL 的分支）

## 许可现状：代码已不含上游源码

从 1.0.0 起，原先从上游改写来的两个模块（`PCL.Core`、`PCL.Theme`）已经重写为 `Pcv.Core`、`Pcv.Theme`；
最后一块 `PCL.Launch.Microsoft`（微软登录）也已重写为 `Pcv.Launch.Microsoft`。

**现在的代码库里没有一行上游源码。** 上游的 `LICENCE-PCL` 仍然留在仓库根目录，作用是两件事：

1. 记录来源——这个项目的架构、契约思路、主题配色算法都是从 PCL 学来的；
2. 信用——龙腾猫跃的工作值得留个署名，哪怕代码已经不在了。

既然不再包含上游代码，严格意义上就不再是"重度使用"，`LICENCE-PCL` 里那五条（PCL 前缀命名、
关于页署名、MC 正版劝导等）不再构成本项目的约束。但这几条我们**照样做了**，
署名和劝导都还在——不是因为必须，是因为应该。

## 本项目的许可：MIT

以下目录是这次改造里**新写的**，不含上游代码，按 MIT 授权：

```
src/Pcv.Modular.Abstractions/    契约（接口、清单、服务）
src/Pcv.Modular.Host/            程序集加载器
src/Pcv.Modular.Packages/        包校验与安装
src/Pcv.Modular.Runtime/         进程型框架运行时
src/Pcv.Modular.Cli/             命令行
src/Pcv.Shell/                   壳（界面、构建、安装）
src/Pcv.*/                       自检 / 统计 / 清理
installer/                       安装程序
tools/                           打包与构建脚本
samples/                         框架与软件示例
assets/prompt-framework.md       给 AI 的提示词
```

许可全文见 [`LICENSE`](LICENSE)（MIT，Copyright (c) 2026 150311-ma）。

想快速了解"能做什么、不能做什么"，看 [`OPEN-SOURCE.md`](OPEN-SOURCE.md)。

## 与上游要求的关系

| # | 上游对重度使用的要求 | 状态 |
| --- | --- | --- |
| 1 | 明确表示是第三方独立二次创作 | ✅ 版权页 + 本文件 |
| 2 | 名称以 PCL 开头 + 第三方后缀 | ➖ 已不适用：代码不再含上游内容，叫 PCV 名正言顺 |
| 3 | 关于页署名 + 赞助链接 + 公开源码 | ✅ 版权页就是关于页 |
| 4 | MC 启动功能加正版劝导 | ✅ 离线登录启动前会打印劝导 |
| 5 | 无赞助解锁 / 主色相蓝色 | ✅ |

## 如果你要用这里的代码

- 只想用"加载器 + 契约 + 进程运行时"这一层（`Pcv.Modular.*`）：MIT，署名即可，随便用。
- 想连同 `PCL.Core` / `PCL.Theme` / `Pcv.Launch.Microsoft` 一起用：请先读仓库根目录的 `LICENCE-PCL`，
  那是上游的规矩，不是我的。
