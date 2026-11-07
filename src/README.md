# src/ — 源代码

十六个工程，分四层。**依赖只能从上往下**，反过来就是设计错了。

```
壳（可执行）
  Pcv.Shell                 PCV.exe：六页界面、市场、桌面图标、新手引导、托盘
                            HostControl 实现受控宿主能力并注册进 Services

加载与包管理（库，被壳引用）
  Pcv.Modular.Host          扫 modules/、解析清单、依赖排序、装配服务
  Pcv.Modular.Packages      .pclpkg 校验与安装（防路径穿越、依赖检查、备份回滚）
  Pcv.Modular.Runtime       进程型框架：起进程、一行一 JSON 的协议、窗口
  Pcv.Modular.Cli           命令行：列表 / 安装 / 自检

契约（所有模块都只依赖它）
  Pcv.Modular.Abstractions  IPclModule · IModuleContext · ModuleManifest
                            IHostControl（框架能用的宿主能力）· 其它服务接口

模块（各自一个 dll + module.json）
  Pcv.Core                  基础：路径、JSON 配置、颜色换算
  Pcv.Theme                 主题：12 套配色写进 WPF 资源
  Pcv.Launch.Microsoft      微软登录：设备码 → XBL → XSTS → MC 档案
  Pcv.Diagnostics           环境自检
  Pcv.Stat                  使用统计（只记本地）
  Pcv.Cleaner               垃圾清理
  Pcv.Demo.Hello            最小可跑示例
  Pcv.Official.Guide        官方入门指南（市场包，演示 IHostControl 切页）
  Pcv.Official.Notes        随手记（市场包，演示模块私有数据目录）
  Pcv.Official.Dock         快捷面板（市场包，演示读模块清单 / 打开模块）
```

三个 `Pcv.Official.*` 是**市场上架的官方包**，不随安装包分发：
打好的 `.pclpkg` 在 `dist/`，展示信息在 `market/catalog.json`。
清单里声明 `icon` / `accent` / `desktop` / `order` 可以控制桌面图标的样子和顺序。

## 几条不能破的规矩

1. **壳不引用模块工程。** `Pcv.Shell.csproj` 里对模块的引用都带 `ReferenceOutputAssembly="false"`，
   只用来保证构建顺序。新增一个模块不需要改壳的代码——把目录丢进 `modules/` 就行。
2. **契约工程不引用 WPF。** `IFrameworkSurface.CreateView` 返回 `object`，
   非 UI 模块不该为了一个签名拖 WPF 进来。
3. **模块之间不互相引用程序集。** 需要对方的能力就从 `context.Services` 取接口。
4. **模块目录就是它的全部身份。** 目录名 = 模块 Id，里面的 `module.json` 说了算；
   装卸就是增删目录，不需要注册表。

## 加一个模块

1. 复制 `Pcv.Demo.Hello` 改名；
2. 写 `module.json`（id 小写点分、assembly、entry、category、dependencies）；
3. `python tools/pack_module.py <输出目录> ../dist/<id>-<版本>.pclpkg`；
4. 拖进 PCV，或者 `PCV.Modular.Cli.exe --install ...`。

写不出来就让 AI 写：把 `assets/prompt-framework.md` 整段丢给模型，它会给一个完整的构建脚本。
