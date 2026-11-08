#!/usr/bin/env python3
"""TRM 设计令牌体检（纯标准库、Linux 可跑）。

上游 TRM UI 的第一条硬规矩是「颜色只从变量取」：写死的颜色在另一套主题下
必然是错的，而且只有换主题那一刻才看得出来。这个脚本把规矩变成闸门：

  1. 浅色 / 深色两份字典的 key 必须一一对应（少一个就静默掉回默认值）
  2. 界面 XAML 里不许出现裸 #RRGGBB / "White" 之类
  3. 界面 C# 里不许出现 Color.FromRgb(0x..) / Solid("#..")
  4. 所有 Trm* 资源引用必须真的有定义
  5. 颜色令牌只能用 DynamicResource（StaticResource 换主题时不会动）

白名单只有两处「定义处」：Styles/TrmTheme*.xaml，以及 App.xaml 里
Pcv.Theme 模块的 ColorBrush* 运行时插槽。

用法：
    python tools/check_trm_theme.py
    python tools/check_trm_theme.py --inject-fault     # 反向验证校验器本身
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
STYLES = SHELL / "Styles"
LIGHT = STYLES / "TrmTheme.xaml"
DARK = STYLES / "TrmTheme.Dark.xaml"

KEY_RE = re.compile(r'x:Key="([^"]+)"')
HEX_RE = re.compile(r'"#[0-9A-Fa-f]{6,8}"')
REF_RE = re.compile(r'\{(DynamicResource|StaticResource)\s+(Trm[A-Za-z0-9]+)\}')
CS_RGB_RE = re.compile(r'Color\.From(?:Rgb|Argb)\(\s*0x[0-9A-Fa-f]{2}')
CS_HEX_RE = re.compile(r'"#[0-9A-Fa-f]{6,8}"')
CS_SOLID_RE = re.compile(r'\bSolid\("#')

# 这些是「定义处」，不是界面。
SKIP_CS = {"Trm.cs", "TrmThemeService.cs"}
# Lighten() 之类的颜色计算用变量，不是写死的主题色；只闸 0x 常量三元组。


def defined_keys(path: Path) -> set[str]:
    return set(KEY_RE.findall(path.read_text(encoding="utf-8")))


def xaml_files(root: Path) -> list[Path]:
    return sorted((root / "src" / "Pcv.Shell").glob("*.xaml"))


def cs_files(root: Path) -> list[Path]:
    return sorted((root / "src" / "Pcv.Shell").glob("*.cs"))


def check(root: Path) -> list[str]:
    errors: list[str] = []
    styles = root / "src" / "Pcv.Shell" / "Styles"
    light, dark = styles / "TrmTheme.xaml", styles / "TrmTheme.Dark.xaml"
    for f in (light, dark):
        if not f.is_file():
            return ["缺少设计令牌字典：" + str(f.relative_to(root))]
    lk, dk = defined_keys(light), defined_keys(dark)
    if lk != dk:
        errors.append("两套主题 key 不一致：浅色多 %s / 深色多 %s"
                      % (sorted(lk - dk), sorted(dk - lk)))
    keys = lk | dk

    # 1) 界面 XAML：裸色、资源引用、StaticResource
    for f in xaml_files(root):
        text = f.read_text(encoding="utf-8")
        rel = str(f.relative_to(root))
        lines = text.splitlines()
        for i, line in enumerate(lines, 1):
            # App.xaml 里 ColorBrush* 是 Pcv.Theme 的运行时插槽，属于定义处
            if f.name == "App.xaml" and re.search(r'x:Key="ColorBrush', line):
                continue
            for m in HEX_RE.finditer(line):
                errors.append("%s:%d 界面里写了裸色值 %s，改用 {DynamicResource TrmXxx}"
                              % (rel, i, m.group(0)))
            for m in re.finditer(r'(Foreground|Fill|Stroke|Background|BorderBrush)="(White|Black|Transparent)"', line):
                errors.append("%s:%d 界面里写了裸颜色名 %s，改用令牌"
                              % (rel, i, m.group(2)))
            for m in REF_RE.finditer(line):
                kind, key = m.group(1), m.group(2)
                if key not in keys:
                    errors.append("%s:%d 引用了不存在的令牌 %s" % (rel, i, key))
                if kind == "StaticResource":
                    errors.append("%s:%d 颜色/度量令牌 %s 用了 StaticResource，换主题不会更新，改 DynamicResource"
                                  % (rel, i, key))

    # 2) 界面 C#：裸色值
    for f in cs_files(root):
        if f.name in SKIP_CS:
            continue
        text = f.read_text(encoding="utf-8")
        rel = str(f.relative_to(root))
        for i, line in enumerate(text.splitlines(), 1):
            if CS_RGB_RE.search(line) or CS_HEX_RE.search(line) or CS_SOLID_RE.search(line):
                errors.append("%s:%d 代码里写了裸色值，改用 Trm.Xxx：%s"
                              % (rel, i, line.strip()[:100]))
    return errors


def inject_fault() -> int:
    """往临时副本里注入 5 类已知故障，确认每一类都被同一个 check() 抓到。"""
    cases = [
        ("界面 XAML 裸色", "MainWindow.xaml", '<Window ',
         'Foreground="#123456" '),
        ("引用不存在的令牌", "MainWindow.xaml", '<Window ',
         'Tag="{DynamicResource TrmNoSuchKey}" '),
        ("StaticResource 换不动主题", "MainWindow.xaml", '<Window ',
         'ToolTip="{StaticResource TrmInk}" '),
        ("代码裸色", "MainWindow.xaml.cs", "namespace Pcv.Shell",
         '// fault\n    internal static class F { static object X = new SolidColorBrush(Color.FromRgb(0x12, 0x34, 0x56)); }\n'),
        ("深色字典少一个 key", "Styles/TrmTheme.Dark.xaml",
         '<sys:String x:Key="TrmThemeMarker">dark</sys:String>', ''),
    ]
    caught = 0
    with tempfile.TemporaryDirectory(prefix="pcv-trm-fault-") as tmp:
        root = Path(tmp)
        (root / "src" / "Pcv.Shell" / "Styles").mkdir(parents=True)
        for f in xaml_files(ROOT):
            shutil.copy2(f, root / "src" / "Pcv.Shell" / f.name)
        for f in cs_files(ROOT):
            shutil.copy2(f, root / "src" / "Pcv.Shell" / f.name)
        for name in ("TrmTheme.xaml", "TrmTheme.Dark.xaml"):
            shutil.copy2(STYLES / name, root / "src" / "Pcv.Shell" / "Styles" / name)

        for label, target, anchor, payload in cases:
            dst = root / "src" / "Pcv.Shell" / target
            original = dst.read_text(encoding="utf-8")
            if anchor not in original:
                print("  × %s：注入锚点没找到，校验器自身有问题" % label)
                continue
            dst.write_text(original.replace(anchor, anchor + payload, 1)
                           if payload else original.replace(anchor, "", 1), encoding="utf-8")
            errs = check(root)
            hit = bool(errs)
            print("  %s %s%s" % ("✓" if hit else "×", label,
                                 "" if hit else " —— 没报红！"))
            if hit:
                caught += 1
                print("      " + errs[0][:130])
            dst.write_text(original, encoding="utf-8")

    print("\n故障注入：%d/%d 抓到" % (caught, len(cases)))
    return 0 if caught == len(cases) else 1


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--inject-fault", action="store_true", help="反向验证校验器本身")
    args = ap.parse_args()
    if args.inject_fault:
        print("反向验证：故意注入 5 类不合规改动")
        return inject_fault()

    errs = check(ROOT)
    if errs:
        print("TRM 主题体检失败（%d 项）：" % len(errs))
        for e in errs:
            print("  - " + e)
        return 1
    print("TRM 主题体检通过：两份字典 %d 个 key 对齐；%d 个界面 XAML / %d 个界面 C# 无裸色；资源引用全部有定义"
          % (len(defined_keys(LIGHT)), len(xaml_files(ROOT)),
             len([f for f in cs_files(ROOT) if f.name not in SKIP_CS])))
    return 0


if __name__ == "__main__":
    sys.exit(main())
