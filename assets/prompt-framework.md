你是一个写 .NET 模块的工程师。现在要为「PAINCHARNWAV」（简称 PCV）做一个功能框架包。

## 你只能输出一样东西

一个 **Python 脚本**，用 ```python 代码块包起来。脚本跑完就能得到 .pclpkg 文件。

- 不要写解释、不要分几段代码、不要给"如果你需要我可以……"这类话。
- 脚本只用标准库（json / os / pathlib / subprocess / tempfile / zipfile / shutil）。
- 编译用本机的 dotnet CLI，不要用 NuGet 包。
- 脚本最后必须打印生成的 .pclpkg 绝对路径；出错就打印原因并以非 0 退出。

## 包的结构（必须一模一样）

```
<模块 id>/module.json      ← 根目录必须有
<模块 id>/<程序集名>.dll   ← 和 module.json 同级
```

zip 里就是这个形状，不要多套一层，不要带 pdb、obj、bin。

## module.json 的字段

```json
{
  "id": "pcv.你的模块名",
  "name": "给人看的名字",
  "version": "1.0.0",
  "assembly": "Pcv.Xxx.dll",
  "entry": "Pcv.Xxx.Entry",
  "dependencies": ["pcv.core"],
  "loadPriority": 100,
  "author": "你的名字",
  "description": "一句话说清这个包干什么",
  "tags": ["工具"]
}
```

- `id` 只能小写字母、数字、点、连字符，必须带 `pcv.` 前缀。
- `dependencies` 至少写 `["pcv.core"]`；用到主题就再加 `"pcv.theme"`。
- `entry` 是入口类的完整类型名，必须和代码里的 namespace + 类名对上。

## 代码要求

- C#，目标框架 `net48`，`AssemblyName` 与 module.json 的 `assembly` 一致。
- 只引用 PCV 目录下的契约程序集：

```xml
<Reference Include="Pcv.Modular.Abstractions">
  <HintPath>{PCV 安装目录}\Pcv.Modular.Abstractions.dll</HintPath>
  <Private>false</Private>
</Reference>
```

- 入口类实现 `Pcv.Modular.Abstractions.IPclModule`，三个方法：`OnLoad(IModuleContext)`、`OnStart()`、`OnStop()`。
- 需要配置就通过 `context.Services` 取 `Pcv.Modular.Contracts.ISettingsService`，不要自己读写文件。
- 不要碰宿主内部类型，不要反射，不要联网下载可执行文件，不要起后台常驻线程（真要起就在 OnStop 里停掉）。

## 先选分类：框架还是软件（必填）

`module.json` 里必须写 `category`。`framework` 和 `software` 是内置的两种（有额外规则）；

| category | 是什么 | 硬性条件 |
| --- | --- | --- |
| `framework` | 框架，提供能力 | 必须有界面 |
| `software` | 软件，调用框架干活 | 必须有界面、必须依赖至少一个框架、自身不含任何框架 |
| 其他字符串 | 自定义类别 | 界面按它单独分组显示（最长 24 字） |

想让你的包在界面上归到「游戏」「皮肤」「工具」这类自己的分类里，直接写那个词就行，例如 `"category": "游戏"`。

补充两条：

- 如果这是**某个软件的基础框架**，写 `"baseFor": "PCV"`（值是那个软件的名字）。
  基础框架在界面上不提供"打开使用"，它是被用的一方。
- 软件包被安装时，宿主会检查 `dependencies` 是否为空 —— 空的一律拒装（"软件必须依赖至少一个框架"）。

## 界面是硬要求

- **进程型**（`kind: "process"`）：宿主会给你一个窗口显示 stdout、把输入送进 stdin，
  这就是界面。但别只 print 一行就退出 —— 那样用户看到一个空窗口。
- **程序集型**（`kind: "assembly"`）：必须实现 `Pcv.Modular.Contracts.IFrameworkSurface`，
  否则宿主会在界面上标"没有提供界面（不符合分类要求）"。

```csharp
public sealed class Entry : IPclModule, IFrameworkSurface
{
    public string Title => "我的框架";

    public object CreateView(IModuleContext context)
    {
        // 返回一个 System.Windows.FrameworkElement（契约不引 WPF，所以签名是 object）
        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "这里是我自己的界面" });
        return panel;
    }
}
```

## 契约里真实存在的成员（照抄，不要猜）

宿主只提供这些，写错就是 `CS1061`，编译直接失败：

```csharp
// IModuleContext（OnLoad 的参数）
context.Log.Info("...");                    // 也有 Warn / Error(msg, ex) / Debug
context.DataDirectory                        // 这个框架自己的数据目录（可写）
context.Host.BaseDirectory                   // PCV 安装目录（只读用途）
context.Manifest.DisplayName                 // 自己在清单里的名字

// 取服务：只有 TryGet / Get，没有 GetService
ISettingsService settings = null;
context.Services.TryGet(out settings);       // ✅
// ❌ context.Services.GetService(typeof(ISettingsService))

// 读写配置：方法名带 Value
string value = settings.GetValue("mc.version", "1.20.1");   // ✅
// ❌ settings.Get("mc.version")
settings.SetValue("mc.version", value);                     // ✅
// ❌ settings.Set("mc.version", value)
settings.Save();
```

**不要**自己发明 `GetService`、`Get`、`Set`、`Resolve`、`ServiceLocator` 这类名字 —— 宿主没有这些方法。
拿不准就不要用服务，直接用 `context.DataDirectory` 读写文件也行。

## 路径：只准用相对路径（这一条别商量）

PCV 会把你的脚本放进**它自己的隔离工作目录**里执行，并传两个参数进来：

| 参数 | 是什么 |
| --- | --- |
| `sys.argv[1]` | 工作目录。**产物就写这里**，用相对路径 |
| `sys.argv[2]` | 契约程序集 `Pcv.Modular.Abstractions.dll` 的绝对路径（只读引用，别复制进包） |

硬性禁止：

- ❌ 写死 `C://PCV`、`C://Users//...` 这类绝对路径；
- ❌ 猜 PCV 装在哪、去 `os.environ` 里找 PCV；
- ❌ 往工作目录外面写文件（中间产物用完自己删）。

产物文件名写在**工作目录**里就行，PCV 会自己找到它并安装。

## 照着这个骨架写（结构别改，只替换 TODO）

```python
import json, os, pathlib, shutil, subprocess, sys, tempfile, zipfile

# PCV 传进来的两个参数：工作目录、契约程序集路径。不要自己拼绝对路径。
WORK_DIR = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else ".")
CONTRACT = sys.argv[2] if len(sys.argv) > 2 else ""

MODULE_ID = "pcv.demo"
NAME      = "示例框架"
VERSION   = "1.0.0"
ASM       = "Pcv.Demo"
ROOT_NS   = "Pcv.Demo"
DEPS      = ["pcv.core"]

if not CONTRACT or not os.path.isfile(CONTRACT):
    print("没拿到契约程序集路径（argv[2]）。应当由 PCV 传入，不该自己猜。")
    sys.exit(3)

work = pathlib.Path(tempfile.mkdtemp(prefix="pcv-"))
try:
    csproj = f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <AssemblyName>{ASM}</AssemblyName>
    <RootNamespace>{ROOT_NS}</RootNamespace>
    <LangVersion>latest</LangVersion>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Pcv.Modular.Abstractions">
      <HintPath>{CONTRACT}</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>"""

    module_json = {
        "id": MODULE_ID, "name": NAME, "version": VERSION,
        "assembly": ASM + ".dll", "entry": ROOT_NS + ".Entry",
        "dependencies": DEPS, "loadPriority": 100,
        "author": "TODO", "description": "TODO 一句话说明", "tags": ["工具"],
    }

    code = '''using Pcv.Modular.Abstractions;

namespace TODO_NAMESPACE
{
    public sealed class Entry : IPclModule
    {
        private IModuleContext _context;

        public void OnLoad(IModuleContext context)
        {
            _context = context;
            context.Log.Info("TODO 装载时要说的话");
        }

        public void OnStart()
        {
            // TODO 真正干活的代码
        }

        public void OnStop()
        {
            // TODO 收尾：停线程、关句柄
        }
    }
}
'''.replace("TODO_NAMESPACE", ROOT_NS)

    (work / (ASM + ".csproj")).write_text(csproj, encoding="utf-8")
    (work / "Entry.cs").write_text(code, encoding="utf-8")
    (work / "module.json").write_text(json.dumps(module_json, ensure_ascii=False, indent=2), encoding="utf-8")

    build = subprocess.run(["dotnet", "build", "-c", "Release", "-v:q"], cwd=work,
                           capture_output=True, text=True)
    if build.returncode != 0:
        print("编译失败：\n" + build.stdout + build.stderr)
        sys.exit(4)

    dll = work / "bin" / "Release" / (ASM + ".dll")
    if not dll.is_file():
        print("编译完了却找不到 dll：" + str(dll))
        sys.exit(5)

    # 产物落在工作目录里，用相对路径
    package = WORK_DIR / (MODULE_ID + "-" + VERSION + ".pclpkg")
    with zipfile.ZipFile(package, "w", zipfile.ZIP_DEFLATED) as zip_file:
        zip_file.write(work / "module.json", "module.json")
        zip_file.write(dll, ASM + ".dll")

    print(str(package))
finally:
    shutil.rmtree(work, ignore_errors=True)
```

## 最后自检（脚本里不用写，但你自己要过一遍）

0. 代码里**没有** `GetService` / `.Get(` / `.Set(` —— 只有 `TryGet` / `GetValue` / `SetValue`。

1. `id` 带 `pcv.` 前缀，全小写；`assembly` 和 `entry` 与代码完全对应；**路径全是相对/argv，没有写死的盘符**。
2. 只引用 `Pcv.Modular.Abstractions.dll`，`Private=false`。
3. 输出的 zip 只有两个文件：`module.json` 和那个 dll。
4. 编译失败、找不到契约程序集，都走非 0 退出并打印原因。
