# 架构与边界

## 一、分层

```
┌──────────────────────────────────────────────────────────────┐
│ Pcv.Shell   PCV.exe                                          │
│   界面（四页）· 构建脚本 · 安装/卸载 · 托盘                   │
└──────────────┬───────────────────────────────────────────────┘
               │ 引用库，不引用任何模块工程
┌──────────────┴───────────────────────────────────────────────┐
│ Pcv.Modular.Host      发现 / 校验 / 依赖排序 / 装载 / 启动    │
│ Pcv.Modular.Packages  .pclpkg 校验与安装（zip-slip 防护）      │
│ Pcv.Modular.Runtime   进程型框架的宿主端（起进程、协议、窗口）  │
│ Pcv.Modular.Cli       命令行：列表 / 安装 / 自检               │
└──────────────┬───────────────────────────────────────────────┘
               │ 只依赖契约
┌──────────────┴───────────────────────────────────────────────┐
│ Pcv.Modular.Abstractions                                     │
│   IPclModule · IModuleContext · ILogger · IServiceRegistry    │
│   IEventBus · ISettingsService · IThemeService                │
│   IMicrosoftAuthService · IFrameworkSurface · ModuleManifest  │
└──────────────┬───────────────────────────────────────────────┘
               │ 实现契约（模块之间互不引用程序集）
┌──────────────┴───────────────────────────────────────────────┐
│ modules/<id>/  ← 一个 dll + 一份 module.json，装就是放目录     │
└──────────────────────────────────────────────────────────────┘
```

**关键约束**：壳对模块工程的引用全是 `ReferenceOutputAssembly="false"`，
只用来排构建顺序。所以「新增一个模块」不需要改壳的一行代码。

## 二、加载流程

| 阶段 | 做什么 | 出问题怎么办 |
| --- | --- | --- |
| 发现 | 扫 `modules/*/module.json`，解析并 `Validate()` | 该模块记失败，其余照常 |
| 去重 | 同一 Id 多个版本时取版本号最高的那个 | — |
| 排序 | 依赖拓扑（就绪队列），同批按 `loadPriority` 再按 Id | 缺依赖 → 该模块"已跳过"；成环 → 涉及的全部失败 |
| 装载 | `Assembly.LoadFrom` + 反射建实例 + `OnLoad(context)` | 该模块失败，依赖它的变"已跳过" |
| 启动 | 按序 `OnStart()` | 该模块失败，已启动的不回滚 |
| 停止 | 逆序 `OnStop()` | 记错误，不阻断其它模块 |

模块通过 `context.Services.Register<T>(impl)` 提供能力，别人用 `TryGet<T>(out var x)` 取。
**没有 DI 容器**：注册表就是一个类型 → 实例的字典，够用且可预测。

## 三、包格式

`.pclpkg` 就是一个 zip：

```
module.json      清单（id / name / version / kind / assembly|script / entry / category / dependencies）
Pcv.Xxx.dll      程序集（只复制模块自己的 dll，契约由宿主提供）
```

安装 = 解到 `modules/<id>/`，旧目录先备份到 `modules/.backup/<id>-<时间>/`。
**没有注册表、没有全局状态**：删掉目录就是卸载。

校验在 `PackageInstaller` 里做，拦截三类：清单不合规、路径越界（zip-slip）、依赖缺失。
装之前会检查目标是否正在运行——进程型框架在跑时文件被占用，直接覆盖会失败。

## 四、两种框架形态

| | `assembly` | `process` |
| --- | --- | --- |
| 加载 | 进程内，`Assembly.LoadFrom` | 独立进程，宿主管生命周期 |
| 界面 | 实现 `IFrameworkSurface`，返回 WPF 控件 | 宿主开一个控制台窗口，收 stdout |
| 语言 | .NET（C# / VB） | 任意（python / node / exe），通过脚本解释器启 |
| 隔离 | 同一进程，共享内存 | 独立进程，拿不到宿主对象 |

`IFrameworkSurface` 返回 `object` 而不是 `FrameworkElement`，是**故意的**：
契约程序集不引用 WPF，只做后台清理的框架不该为一个签名拖 WPF 进来。

## 五、进程协议

宿主 ↔ 框架，stdin/stdout 上一行一条 JSON：

| 方向 | 消息 | 含义 |
| --- | --- | --- |
| 下行 | `init` | 初始化：id、数据目录、同僚列表 |
| 下行 | `stop` | 收尾退出 |
| 下行 | `call` | 调用另一个框架 |
| 下行 | `input` | 用户在窗口里敲的一行 |
| 上行 | `output` | 要显示的一行文本（`[stderr]` 前缀表示错误流） |
| 上行 | `title` | 改窗口标题 |
| 上行 | `call` | 请求调用别的框架 |
| 上行 | `reply` | 回应别人的 `call` |
| 上行 | `log` | 写进宿主日志 |

任何语言都能实现——不用我们的 SDK，不用特定运行时。`samples/py-demo` 是最小实现。

**编码**：宿主按字节读，UTF-8 严格优先，失败退回系统编码（中文 Windows 上 Java 吐 GBK）。
这条是踩过坑才加的：之前硬按 UTF-8 解，中文全是 `ʵ�ù���`。

## 六、构建与安装

```
dotnet build Pcv.sln -c Release
  → src/*/bin/Release/*.dll
  → Pcv.Shell 的 ArrangeModules 把 core / theme / login 拷进 bin/Release/modules/
python tools/build_installer.py
  → 摆干净目录（不含 logs、PCLData）→ payload.zip → 编 installer → dist/PCV-Setup-x.y.z.exe
```

安装器把 `payload.zip` 解到目标目录，写一份 `Uninstall.exe`（就是它自己，带 `--uninstall`），
建快捷方式、写 HKCU 卸载项。装到 `%LOCALAPPDATA%\Programs\PAINCHARNWAV`，不要管理员权限。

## 七、边界（写清楚，免得误会）

- **不是沙箱。** 进程型框架能读你的硬盘、能联网，和你手动跑一个 exe 一样。
  隔离的只是"拿不到宿主的内存对象"。
- **不做版本共存。** 同一 Id 只装一份，覆盖安装走备份。
- **不做热更新。** 覆盖安装已加载的模块，需要重启壳才彻底生效（界面会提示）。
- **只支持 Windows 10/11。** WPF + .NET Framework 4.8，没有跨平台计划。
- **模块没有签名校验。** 信任来源靠自己——别装来路不明的包。

## 八、历史

1.0.0 时还有三个从 PCL 改写来的模块（`PCL.Core` / `PCL.Theme` / `PCL.Launch.Microsoft`），
2.0.0 全部重写为 `Pcv.*`。为什么要重写、当时怎么判断的，记在 [CHANGELOG.md](CHANGELOG.md)。
