# installer/ — 安装程序

`PCV.Setup` 是一个**单文件安装器**（WPF，.NET Framework 4.8）。不依赖 Inno Setup / NSIS 之类的外部工具，
整个安装程序就是这一个工程——解包、快捷方式、卸载项全在自己身上。

## 它做的事

1. 结束正在运行的 `PCV.exe`（避免文件占用）；
2. 把内嵌的 `payload.zip` 解到目标目录，**逐条校验路径**，越界就中止（防 zip-slip）；
3. 从自己身上复制一份 `Uninstall.exe` 到安装目录；
4. 桌面 + 开始菜单建快捷方式；
5. 写 HKCU 卸载项（`设置 → 应用` 里能看到）。

装到 `%LOCALAPPDATA%\Programs\PAINCHARNWAV`，**不需要管理员权限**。

## 参数

| 参数 | 作用 |
| --- | --- |
| （无） | 图形界面 |
| `--silent --dir <路径>` | 静默安装 |
| `--silent --dir <路径> --no-shortcuts` | 不建快捷方式 |
| `--silent --dir <路径> --no-registry` | 不写卸载项（自动化测试用） |
| `--uninstall` | 卸载（这个 exe 被复制成 `Uninstall.exe` 时用的就是它） |
| `--shortcut <lnk> --target <exe>` | 给已装好的目录补一个快捷方式 |
| `--render <png>` | 离屏渲染安装界面（会话抓不到窗口时用） |

## 构建

```bat
python tools/build_installer.py
```

流程：构建整个方案 → 摆一份干净目录（不含 logs/PCLData）→ 打 `payload.zip` → 编译安装器 →
输出 `dist/PCV-Setup-<版本>.exe`。

`payload.zip` 是**构建产物**，但为了能直接 `dotnet build` 这个工程，仓库里保留了一份。
改了模块或界面之后记得重跑一次脚本，否则安装包里的还是旧的。

## 已知限制

- 装在 `Program Files` 需要管理员权限，默认路径刻意避开了；
- 不做增量升级：覆盖安装等于整目录替换，用户数据在 `PCLData/`（不在 payload 里）不受影响。
