# dist/ — 打包产物

**这里的东西都是生成出来的**，不要手改；改完代码重新跑打包脚本。

| 文件 | 来源 |
| --- | --- |
| `PCV-Setup-<版本>.exe` | `python tools/build_installer.py` |
| `Pcv.<模块>-<版本>.pclpkg` | `python tools/pack_module.py src/<工程>/bin/Release ...` |
| `pcv.<示例>-<版本>.pclpkg` | 同上，源目录换成 `samples/` |
| `pcv.rel.demo-*` / `pcv.mclaunch-*` | 由**构建脚本**生成（`PCV.exe --build 脚本.py`），从 `PCLData/build/<时间>/` 拷过来 |

## 命名规矩

```
<Id>-<主>.<次>.<修>.pclpkg
```

Id 和包内 `module.json` 里的 `id` 必须一致——安装器按文件名找不到包会**静默跳过**
（`build_installer.py` 的 `PACKAGES` 名单里那行会打印"跳过"但不中断）。

## 升版本号时要一起改的地方

1. 各 `module.json` 的 `version`；
2. `tools/build_installer.py` 里 `PACKAGES` 的文件名；
3. `installer/PCV.Setup/Installer.cs` 的 `Version` 常量与 csproj 的 `<Version>`；
4. 示例构建脚本里写死的 `VERSION`（包名会跟着变）。

漏一处不会报错，只会让"装完之后少一个包"或者"卸载列表里版本号对不上"——
这类问题最难查，所以每次升版本都按这个清单过一遍。
