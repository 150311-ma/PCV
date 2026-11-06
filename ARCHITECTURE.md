# 架构与边界

## 一、分层

```
┌──────────────────────────────────────────────────────────┐
│ Pcv.Shell（壳）                                          │
│   只做三件事：跑加载器 / 显示模块状态 / 退出时收尾        │
└───────────────▲──────────────────────────────────────────┘
                │ 只依赖契约
┌───────────────┴──────────────────────────────────────────┐
│ Pcv.Modular.Abstractions（契约）                          │
│   IPclModule · ModuleManifest · IServiceRegistry          │
│   IThemeService · IMicrosoftAuthService · ISettingsService│
└───────────────▲──────────────────────────────────────────┘
                │ 实现契约
┌───────────────┴──────────────────────────────────────────┐
│ pcv.core → pcv.theme → …（模块之间只依赖契约，不互相引用程序集）    │
│   每个模块 = 一个独立 dll + 一份 module.json              │
└──────────────────────────────────────────────────────────┘
                ▲
                │ 发现 / 解析 / 加载 / 启动
        Pcv.Modular.Host（加载器）
```

**关键约束**：壳不引用任何模块工程（`ReferenceOutputAssembly=false`，只保证构建顺序）。
所以"新增一个模块"不需要改壳的一行代码——把目录丢进 `modules/` 就行。

## 二、加载流程

| 阶段 | 做什么 | 失败时 |
| --- | --- | --- |
| 发现 | 扫 `modules/*/module.json`，解析并校验 | 该模块记失败，其余继续 |
| 解析 | 依赖拓扑排序（Kahn），同层按 `loadPriority` 再按 Id | 缺依赖 / 成环 → 失败或整组失败 |
| 加载 | `Assembly.LoadFrom` + 反射建实例 + `OnLoad` | 该模块失败，下游变"已跳过" |
| 启动 | 按序 `OnStart` | 该模块失败，已启动的不回滚 |
| 停止 | 逆序 `OnStop` | 记错误，不阻断其它模块 |

## 三、仓库边界

| 仓库 | 内容 | 状态 |
| --- | --- | --- |
| `150311-ma/PCL`（fork） | 上游源码 + `modular/` 骨架 | ✅ 加载器/核心/主题/登录可编译 |
| `PCL.UI.Controls` | `Controls/` 51 文件 | 🚧 源码归位 |
| `PCL.UI.Launch` | `Pages/PageLaunch` 22 文件 | 🚧 源码归位 |
| `PCL.UI.Download` | `Pages/PageDownload` 22 文件 | 🚧 源码归位 |
| `PCL.UI.Instance` | `Pages/PageInstance` 14 文件 | 🚧 源码归位 |
| `PCL.UI.Setup` | `Pages/PageSetup` 12 文件 | 🚧 源码归位 |
| `PCL.UI.Link` | `Pages/PageLink` 2 文件 | 🚧 源码归位 |
| `PCL.UI.Other` | `Pages/PageOther` 10 文件 | 🚧 源码归位 |

"源码归位"= `views/` 与上游逐字一致，`module.json` 与 `MIGRATION.md` 就位，
但**还没有**做契约适配，因此不产出可编译的 dll。这一步的状态写在各仓库 README 里，
不靠印象。

## 四、迁移记账

每个 UI 仓库的 `MIGRATION.md` 是由脚本从源码里数出来的，不是手写愿望单：

```
PageLaunchLeft.xaml.vb  837 行  Settings×33、RunInUi×15、FrmMain×11、Hint×2、ThemeUnlock×1
```

每改完一个文件就从表里删一行；表空了，这个模块才算真正独立。
重新统计：

```bash
python tools/split_ui.py      # 重新生成 ui-repos 下的清单（会覆盖，慎用）
```

## 五、下一步（按依赖顺序，不跳步）

1. `IPathService` / `INotificationService` / `IHostShell` 三个契约补进 Abstractions；
2. 用它们把 `PCL.UI.Controls` 的 28 个代码文件适配掉（它没有下游，风险最低）；
3. 再按 Launch → Download → Instance → Setup → Link → Other 的顺序推进；
4. 每推完一个模块，把 `PCL-repo/Plain Craft Launcher 2/` 里对应的旧文件删掉，
   避免同一份代码长期两处存在。

## 六、本体与骨架的关系（别搞混）

现在仓库里有两条线，边界必须说清楚，否则迟早有人以为模块已经接管了本体：

| | 本体 `Plain Craft Launcher 2/` | 骨架 `modular/` |
| --- | --- | --- |
| 状态 | **能编译、能跑**，界面和功能都是它 | 能编译、能跑，但只有壳与三个基础模块 |
| 编译方式 | `dotnet build`（已翻新为 SDK 风格） | `dotnet build Pcv.sln` |
| 作用 | 真正给用户用的程序 | 验证模块边界、承载新代码 |
| 谁依赖谁 | 谁也不依赖谁 | 谁也不依赖谁 |

两个**不共享代码**是有意为之：骨架先把「契约 + 加载器」跑通，本体继续可用。
等某个模块在骨架里稳定了，再把本体里对应的文件删掉、让本体走加载器取能力——
顺序反过来（先拆本体）只会得到两个都跑不起来的东西。

判断收敛进度的唯一标准：**本体里对应的旧文件被删掉了多少**。数文件，不看感觉。

