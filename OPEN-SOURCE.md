# PCV 开源声明

> 一句话：**PCV 的源代码是开源的，用 MIT 许可。** 你可以读、改、二次开发、商用，
> 只要保留版权声明；但别拿这个名字和图标去冒充官方，也别指望我们为你的数据负责。

这份文件是给"想用这个代码的人"看的。法律效力以 [`LICENSE`](LICENSE) 与
[`LICENCE-PCL`](LICENCE-PCL) 两份文件为准，这里只是把话说清楚。

---

## 开源的是什么

| 内容 | 许可 | 说明 |
| --- | --- | --- |
| 全部源代码（`src/`、`installer/`、`tools/`、`samples/`、`assets/`） | **MIT** | 见 [`LICENSE`](LICENSE) |
| 编译产物（`dist/` 里的 exe 与 .pclpkg） | **MIT** | 和源码同一份许可，随便分发 |
| 项目名称「PAINCHARNWAV / PCV」、图标 | **不授权** | 名称和图标不在 MIT 范围内，见下文「名称与图标」 |
| 第三方依赖（Newtonsoft.Json 等） | 各自许可 | 见各包自带的许可声明 |

2.0.0 起，仓库里的代码**全部是重写的**，不含任何上游 PCL 源码。
仓库根目录的 `LICENCE-PCL` 是上游的来源记录与署名，它不构成本项目代码的额外限制——
但**如果你自己把 PCL 的代码合进来，那部分仍然受那份 LICENCE 约束**，这点别搞混。

---

## 你可以做什么

- ✅ 读代码、学架构，拿去改自己的东西
- ✅ Fork、改、再发布（改名或注明来源都行，保留版权声明即可）
- ✅ 用在商业产品里，不需要给我们钱，也不需要问
- ✅ 拿 `Pcv.Modular.*` 这套「加载器 + 契约 + 框架包」的结构去做完全不相干的软件
- ✅ 把界面截图、宣传片用在介绍文章、视频里

## 你需要做什么

- ⚠️ 保留版权声明和许可文本（MIT 的唯一硬性要求，别删 `LICENSE`）
- ⚠️ 二次分发时说明这是**基于 PCV 的修改版**，别让人以为是官方发布
- ⚠️ 别用「PAINCHARNWAV」「PCV」当你的产品名，也别用项目图标——见下

## 名称与图标

MIT 授权的是代码，不是品牌。所以：

- 你可以说「基于 PCV 修改」，也可以在自己项目里提一句来源；
- 不要把自己的修改版**仍然叫 PCV** 发布，尤其是带官方图标、放到应用商店或群里分发——
  出事（被人装了不该装的东西）算谁的说不清；
- 想用这个名字做发行版，先来开个 issue 聊聊，通常都好说。

---

## 我们不承诺什么

这不是免责套话，是这个项目的真实边界：

- **没有担保。** 软件按「现状」提供。装框架、跑脚本、启动游戏，出了问题（数据丢了、账号封了、
  电脑蓝屏）都是你自己承担，MIT 里写得很清楚。
- **框架不是沙箱。** 进程型框架拿不到宿主的内存对象，但文件系统层面它和你是同一个用户权限。
  **别装来路不明的 `.pclpkg` 或构建脚本**——它能干的事和你手动跑一个 exe 一样多。
- **微软登录需要你自己的 ClientId。** 开源版不内置（带了等于把别人的配额挂外面）。
  令牌存在本地配置文件里，不上传任何服务器；但配置文件本身没有加密，注意保管。
- **Minecraft 相关功能不完整。** 1.17+ 需要对应版本的 Java，「进游戏」这一步没有完整验证过。
- **不做商业支持。** 有问题提 issue，看到就回；但别指望 SLA。

## 隐私

- 不收集任何使用数据，没有遥测、没有统计上报（`pcv.stat` 只统计**本地**的框架启停次数）。
- 唯一的联网行为：你主动触发的微软登录、Minecraft 下载、以及让 AI 生成脚本时的第三方服务。
- 配置与日志都在你自己的电脑上：`PCLData/` 与 `logs/`。

---

## 贡献

- 提 issue：说清楚「期望什么、实际什么、怎么复现」。带日志更好，`logs/modules.log` 就够。
- 提 PR：小而聚焦的改动最好合；动架构的先开 issue 聊，别写完才发现方向不对。
- 新增框架包：照着 [`assets/prompt-framework.md`](assets/prompt-framework.md) 的格式写，
  `python tools/pack_module.py` 能打包，`--build` 能自动装。

## 与上游 PCL 的关系

PCV 的架构思路、契约设计、主题配色算法，都是从 [PCL](https://github.com/Meloong-Git/PCL) 学来的，
作者是**龙腾猫跃（LTCat）**。这点记在 [`NOTICE.md`](NOTICE.md) 里，也写在程序的「版权」页里。

2.0.0 之后代码已经全部重写，但**这份情不因为代码没了就消失**。喜欢这个方向的话，
也可以去给上游点个赞助：<https://meloong.com/afd/a/LTCat>

---

## English summary

PCV (PAINCHARNWAV) is open source under the **MIT License** (see `LICENSE`).
All source code in this repository was rewritten from scratch as of 2.0.0 and contains
no code from the upstream PCL project; the root `LICENCE-PCL` file is kept for attribution only.
The project name and logo are **not** covered by the MIT grant — do not ship a fork under
the same name. No warranty of any kind; frameworks run as separate processes but are
**not** a security sandbox.
