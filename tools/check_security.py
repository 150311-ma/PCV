#!/usr/bin/env python3
"""安全系统体检（纯标准库、Linux 可跑）。

安全系统最怕「看着在，实际上没有」——比如某个检查被改成了永远返回真，
或者弹窗窗口被误删了，于是「检测到异常就结束进程」这件事根本没发生，
但代码里还留着 SecurityGuard 这个名字。

这个脚本把「安全系统真的接上了」变成会报红的检查：

  1. 安全守卫必须在壳工程里，而且得在模块加载之前被调用
     （晚一步就晚了：第三方程序集已经进了进程）
  2. 检出之后必须弹窗 + 结束进程，两件事都要在
  3. 结束进程得是真结束（Kill / Exit），不能只是打个日志
  4. ildasm 屏蔽属性必须写在公共 props 里（所有程序集都算）
  5. Release 不能带符号文件（.pdb 会把变量名和源码路径一起发出去）
  6. 指纹清单的生成脚本必须在，且清单格式与读取端一致

用法：
    python tools/check_security.py
    python tools/check_security.py --inject-fault     # 反向验证：注入故障，确认它会报红
"""
from __future__ import annotations

import argparse
import re
import shutil
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SHELL = ROOT / "src" / "Pcv.Shell"
GUARD = SHELL / "SecurityGuard.cs"
ALERT_XAML = SHELL / "SecurityAlertWindow.xaml"
ALERT_CS = SHELL / "SecurityAlertWindow.xaml.cs"
APP = SHELL / "App.xaml.cs"
PROPS = ROOT / "Directory.Build.props"
INTEGRITY_TOOL = ROOT / "tools" / "build_integrity.py"


def read(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return ""


def check(root: Path) -> list[str]:
    errors: list[str] = []

    guard = read(root / "src/Pcv.Shell/SecurityGuard.cs")
    app = read(root / "src/Pcv.Shell/App.xaml.cs")
    alert_xaml = read(root / "src/Pcv.Shell/SecurityAlertWindow.xaml")
    alert_cs = read(root / "src/Pcv.Shell/SecurityAlertWindow.xaml.cs")
    props = read(root / "Directory.Build.props")
    integrity_tool = read(root / "tools/build_integrity.py")

    # 1) 守卫文件必须在，而且要有实质内容
    if not guard:
        errors.append("缺少安全守卫：src/Pcv.Shell/SecurityGuard.cs")
    else:
        # 四类检查一个都不能少，而且必须真的被 Inspect() 调用 ——
        # 光有个没人调用的方法等于没有。
        for name, pattern in (
            ("调试器检测", r"CheckDebugger"),
            ("外来模块检测", r"CheckForeignModules"),
            ("运行环境挂钩检测", r"CheckRuntimeHooks"),
            ("文件指纹检测", r"CheckIntegrity"),
        ):
            if not re.search(pattern, guard):
                errors.append("安全守卫里没有「%s」" % name)

        # Inspect() 里必须真的调到了这四个方法
        inspect = re.search(r"public static List<Threat> Inspect\(\)(.*?)\n        }", guard, re.S)
        if not inspect:
            errors.append("安全守卫里找不到 Inspect() —— 四类检查没有统一的入口")
        else:
            body = inspect.group(1)
            for name, method in (
                ("调试器检测", "CheckDebugger"),
                ("外来模块检测", "CheckForeignModules"),
                ("运行环境挂钩检测", "CheckRuntimeHooks"),
                ("文件指纹检测", "CheckIntegrity"),
            ):
                if method + "()" not in body:
                    errors.append("Inspect() 没有调用「%s」的 %s()" % (name, method))

    # 2) 必须在模块加载之前调用
    if guard and app:
        guard_call = app.find("SecurityGuard.Initialize()")
        if guard_call < 0:
            errors.append("App.xaml.cs 里没有调用 SecurityGuard.Initialize()")
        else:
            # 宿主加载模块的那一行
            module_run = app.find("_host.Run()")
            if module_run < 0:
                errors.append("App.xaml.cs 里找不到模块加载入口，安全自检的位置无从判断")
            elif guard_call > module_run:
                errors.append("安全自检跑在模块加载之后了 —— 得往前挪，"
                              "第三方程序集进了进程就来不及了")

    # 3) 检出之后要弹窗 + 结束进程
    if guard:
        if "SecurityAlertWindow" not in guard:
            errors.append("安全守卫检出后没有弹窗提醒用户")
        if "Terminate()" not in guard:
            errors.append("安全守卫检出后没有结束进程的分支")
        if not re.search(r"\.Kill\(\)", guard):
            errors.append("结束进程用的是温和的 Exit 而不是强制 Kill —— "
                          "用户点了确定却还有残留进程，不算数")

    # 4) 弹窗窗口本身：必须只有一个出口，且确实接到了结束进程
    if not alert_xaml or not alert_cs:
        errors.append("缺少安全告警窗口：SecurityAlertWindow.xaml(.cs)")
    else:
        if "SecurityGuard.Terminate()" not in alert_cs:
            errors.append("告警窗口的确定按钮没有接到结束进程上")
        if alert_xaml.count("<Button") > 1:
            errors.append("告警窗口有多个按钮 —— 应该只有「确定」一个出口，"
                          "不能给「忽略」留后门")

    # 5) ildasm 屏蔽 + Release 不带符号
    if not props:
        errors.append("缺少 Directory.Build.props")
    else:
        # 只认真正生效的那一行：注释里提到属性名不算数，
        # 属性被删掉但说明文字还在，是最容易被骗过去的情况。
        if not re.search(r'<AssemblyAttribute\s+Include="[^"]*SuppressIldasmAttribute"\s*/>', props):
            errors.append("Directory.Build.props 里没有真正启用 SuppressIldasmAttribute —— "
                          "ildasm 就能直接导出 IL")
        if not re.search(r"'\$\(Configuration\)'\s*==\s*'Release'", props):
            errors.append("Directory.Build.props 里没有针对 Release 的设置")
        elif not re.search(r"<DebugType>\s*none\s*</DebugType>", props):
            errors.append("Release 没关掉符号文件（DebugType=none）—— "
                          ".pdb 会把变量名和源码路径一起发出去")

    # 6) 指纹清单工具必须在，且读写两端用同一个文件名
    if not integrity_tool:
        errors.append("缺少指纹清单生成脚本：tools/build_integrity.py")
    else:
        if "integrity.json" not in integrity_tool:
            errors.append("build_integrity.py 里没有 integrity.json 这个文件名")
        if "sha256" not in integrity_tool.lower():
            errors.append("build_integrity.py 里没有算 sha256")

    # 读取端和生成端必须说同一个文件名
    manifest_cs = read(root / "src/Pcv.Shell/IntegrityManifest.cs")
    if manifest_cs and "integrity.json" not in manifest_cs:
        errors.append("壳里读清单用的文件名和工具生成的不是一个 —— "
                      "指纹永远对不上")

    return errors


def inject_fault() -> int:
    """往临时副本里注入 7 类已知故障，确认每一类都被同一个 check() 抓到。

    每种故障都真的改坏了一处，而不是「以为改坏了」——
    注入本身写错的话，这套反向验证就是假的。
    """
    cases = [
        # (说明, 相对路径, 变换函数)
        ("守卫文件整个没了", "src/Pcv.Shell/SecurityGuard.cs",
         lambda text: None),
        ("调试器检查被摘掉", "src/Pcv.Shell/SecurityGuard.cs",
         lambda text: text.replace("CheckDebugger()", "CheckDebuggerUnused()")),
        ("自检挪到模块加载之后", "src/Pcv.Shell/App.xaml.cs",
         lambda text: text.replace("SecurityGuard.Initialize();", "")
                        .replace("report = _host.Run();",
                                 "report = _host.Run();\n            SecurityGuard.Initialize();")),
        ("结束进程改成温和退出", "src/Pcv.Shell/SecurityGuard.cs",
         lambda text: text.replace("Process.GetCurrentProcess().Kill();", "Environment.Exit(0);")),
        ("告警窗口多一个「忽略」按钮", "src/Pcv.Shell/SecurityAlertWindow.xaml",
         lambda text: text.replace('<Button Content="确定"',
                                   '<Button Content="忽略" /><Button Content="确定"')),
        ("ildasm 屏蔽被去掉", "Directory.Build.props",
         lambda text: text.replace(
             '<AssemblyAttribute Include="System.Runtime.CompilerServices.SuppressIldasmAttribute" />',
             '<AssemblyAttribute Include="System.Reflection.AssemblyTitleAttribute" />')),
        ("Release 又开始带符号", "Directory.Build.props",
         lambda text: text.replace("<DebugType>none</DebugType>",
                                   "<DebugType>portable</DebugType>")),
    ]

    caught = 0
    with tempfile.TemporaryDirectory(prefix="pcv-security-fault-") as tmp:
        # copytree 要求目标不存在，所以往临时目录底下再开一层
        probe = Path(tmp) / "repo"
        shutil.copytree(ROOT, probe, ignore=shutil.ignore_patterns(
            "*.exe", "*.dll", "*.pdb", "*.mp4", "*.png", "*.jpg", "bin", "obj", ".git"))

        for label, relative, mutate in cases:
            target = probe / relative
            original = target.read_text(encoding="utf-8") if target.is_file() else None

            if original is None:
                print("  × %s：源文件本来就不存在，校验器自身有问题" % label)
                continue

            changed = mutate(original)
            if changed == original:
                print("  × %s：注入没生效（锚点没找到），校验器自身有问题" % label)
                continue

            if changed is None:
                target.unlink()
            else:
                target.write_text(changed, encoding="utf-8")

            errors = check(probe)
            hit = bool(errors)
            print("  %s %s%s" % ("✓" if hit else "×", label,
                                 "" if hit else " —— 没报红！"))
            if hit:
                caught += 1
                print("      " + errors[0][:130])

            target.write_text(original, encoding="utf-8")

    print("\n故障注入：%d/%d 抓到" % (caught, len(cases)))
    return 0 if caught == len(cases) else 1


def main() -> int:
    parser = argparse.ArgumentParser(description="PCV 安全系统体检")
    parser.add_argument("--inject-fault", action="store_true", help="反向验证：注入故障确认它会报红")
    args = parser.parse_args()

    if args.inject_fault:
        print("反向验证：故意注入 7 类不合规改动")
        return inject_fault()

    errors = check(ROOT)
    if errors:
        print("安全系统体检失败（%d 项）：" % len(errors))
        for line in errors:
            print("  - " + line)
        return 1
    print("安全系统体检通过：守卫四类检查齐全、自检在模块加载之前、"
          "检出后弹窗并强制结束进程、ildasm 已屏蔽、Release 不带符号文件、"
          "指纹清单读写两端一致")
    return 0


if __name__ == "__main__":
    sys.exit(main())
