import json, os, pathlib, shutil, subprocess, sys, tempfile, zipfile

# PCV 传进来的两个参数：工作目录、契约程序集路径。不要自己拼绝对路径。
WORK_DIR = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else ".")
CONTRACT = sys.argv[2] if len(sys.argv) > 2 else ""

MODULE_ID = "pcv.rel.demo"
NAME = "相对路径示例"
VERSION = "2.0.0"
ASM = "Pcv.RelDemo"
ROOT_NS = "Pcv.RelDemo"
DEPS = ["pcv.core"]

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
    <UseWPF>true</UseWPF>
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
        "category": "framework",
        "dependencies": DEPS, "loadPriority": 200,
        "author": "PCV", "description": "按新提示词生成：全部相对路径，产物落在工作目录。",
        "tags": ["示例"],
    }

    code = '''using System.Windows;
using System.Windows.Controls;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;

namespace Pcv.RelDemo
{
    public sealed class Entry : IPclModule, IFrameworkSurface
    {
        public string Title => "相对路径示例";

        public void OnLoad(IModuleContext context) { }
        public void OnStart() { }
        public void OnStop() { }

        public object CreateView(IModuleContext context)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "我是由一段 DeepSeek 脚本生成的框架。", FontSize = 14 });
            panel.Children.Add(new TextBlock
            {
                Text = "脚本没用任何绝对路径：产物写在 PCV 给的隔离工作目录里，装完自动出现。",
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0)
            });
            return panel;
        }
    }
}
'''

    (work / (ASM + ".csproj")).write_text(csproj, encoding="utf-8")
    (work / "Entry.cs").write_text(code, encoding="utf-8")
    (work / "module.json").write_text(json.dumps(module_json, ensure_ascii=False, indent=2), encoding="utf-8")

    build = subprocess.run(["dotnet", "build", "-c", "Release", "-v:q"], cwd=str(work),
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
