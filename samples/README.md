# samples/ — 示例

五个例子，覆盖「框架怎么写」「软件怎么写」「怎么用 AI 生成一个」三条路。
**都能直接跑**，不需要额外依赖（除了 Python 本身）。

| 目录 | 是什么 | 怎么用 |
| --- | --- | --- |
| `py-demo/` | 最小的**进程型框架**：一个 `main.py` + 一份 `module.json` | 打包后拖进 PCV，或 `--build` |
| `app-demo/` | 最小的**软件**：有界面、依赖 `pcl.core` 和 `py.demo`，自己不干活 | 同上；打开时会自动拉起它依赖的框架 |
| `mc-launcher/` | **Minecraft 启动框架**：版本清单、client.jar、库、natives、离线登录、启动参数 | 完整实现，`mclib/` 里分了模块 |
| `relative-demo/build.py` | **构建脚本**：自己编译成 dll 再打成 `.pclpkg`，全程相对路径 | `PCV.exe --build samples/relative-demo/build.py` |
| `deepseek-mclaunch/build-mclaunch.py` | **AI 生成的构建脚本**修正版：模拟 DeepSeek 输出，改了两处 API 写法 | 同上，是「粘贴 AI 输出」这条路的真实素材 |

## 两种框架形态，各有一个例子

- `py-demo` / `mc-launcher`：`kind: process`，任意语言，自带独立控制台窗口，
  和宿主通过「一行一条 JSON」通信；
- `relative-demo/build.py`：生成的是 `kind: assembly`（.NET 程序集），进程内加载，
  由宿主渲染它给的 WPF 界面。

## 关于 `deepseek-mclaunch/`

这份脚本原样来自 DeepSeek，**直接用会编译失败**（`IServiceRegistry.GetService` 和
`ISettingsService.Get` 是它猜的 API，实际是 `TryGet` / `GetValue`）。仓库里这份是改过的，
保留它的价值在于：提醒「AI 写的构建脚本第一次通常要修两行」。

`relative-demo/build.py` 更值得参考：它演示了**全程相对路径**的写法——
产物写进宿主给的工作目录、契约 dll 路径从 `sys.argv[2]` 拿，换台机器不用改一行。

## 自己试

```bat
:: 打包并安装一个示例
python tools/pack_module.py samples/py-demo dist/pcv.py.demo-3.2.0.pclpkg
src\Pcv.Modular.Cli\bin\Release\Pcv.Modular.Cli.exe --modules "%LOCALAPPDATA%\Programs\PAINCHARNWAV\modules" --install dist\pcv.py.demo-3.2.0.pclpkg

:: 或者让宿主自己跑构建脚本（跑完自动装）
PCV.exe --build samples/relative-demo/build.py
```
