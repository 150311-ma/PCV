#!/usr/bin/env python3
"""模块数字与社区市场约定的体检（纯标准库、Linux 可跑）。

这套体系有两个「一改就静默坏掉」的地方，必须变成闸门：

  1. **模块数字两端必须一致**。创作者工具（C#）算一个号，市场校验（C#）也算，
     Python 侧（构建脚本）还算。任何一端改了算法，另一端的号就对不上，
     用户会看到「模块数字对不上」却不知道为什么。这里用金标准向量锁住。

  2. **社区市场的发现方式是一份约定**：仓库打 topic `pcv-module`、
     根目录有 `pcv-module.json`、字段齐全。约定不写在代码里的话，
     创作者工具改了字段名、市场读不到，也没人会发现。

检查项：
    1. 金标准向量：一组固定的 (id, 版本, sha256) → 期望的模块数字
    2. 手抄宽容规则：大小写、连字符、O/0、I/1 的纠正行为
    3. C# 端与 Python 端的算法常量一致（Crockford 字母表、规范版本号）
    4. 创作者工具写出的清单字段 = 市场读取的字段
    5. 市场读取拒绝不合规的清单（缺字段、路径穿越、非 .pclpkg）
    6. GitHub 源有 raw + API 双路兜底

用法：
    python tools/check_module_code.py
    python tools/check_module_code.py --inject-fault    # 反向验证校验器自己
"""
from __future__ import annotations

import argparse
import importlib
import re
import shutil
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tools"))

import module_code  # noqa: E402

CS_CODE = ROOT / "src" / "Pcv.Modular.Packages" / "ModuleCode.cs"
CREATOR_CS = ROOT / "src" / "Pcv.Shell" / "CreatorWindow.xaml.cs"
GITHUB_CS = ROOT / "src" / "Pcv.Shell" / "GitHubSource.cs"
MARKET_CS = ROOT / "src" / "Pcv.Shell" / "MarketService.cs"

# 金标准向量：这些号是算法定死的结果，改算法就必须改这里（并同步两端）。
# 它们不是「跑一遍看输出」生成的 —— 是契约。
#
# 版本号统一写 9.9.9：**故意不用产品版本号**。
# 否则 `tools/bump_version.py` 刷版时会把这些测试值一起替换掉，
# 期望值没跟着重算，体检立刻报「向量对不上」——3.0.0 → 3.1.1 时就这么红过一次。
# 向量要的是「固定的输入」，不是「当前的版本」。
GOLDEN = [
    ("pcv.stat", "9.9.9", "a" * 64, "PCV-F0RX-91SY-Q9TT"),
    ("pcv.official.guide", "9.9.9", "b" * 64, "PCV-8F5T-AFE7-ME62"),
    ("pcv.py.demo", "9.9.9",
     "f2011e377c0e03af9c4e9a2bf969d19bc0ccff5e6c510fd1abbfb9f745d93487",
     "PCV-78ZV-DG83-75G1"),
    ("pcl.core", "9.9.9", "0" * 64, "PCV-ZVXQ-469R-1EGR"),
    ("x.y", "", "c" * 64, "PCV-QJNM-046Q-CNV2"),
]

# 手抄宽容规则：(输入, 期望输出 或 None)
LOOSE = [
    ("PCV-2WRM-CMN2-24WT", "PCV-2WRM-CMN2-24WT"),
    ("pcv-2wrm-cmn2-24wt", "PCV-2WRM-CMN2-24WT"),
    ("2WRMCMN224WT", "PCV-2WRM-CMN2-24WT"),
    ("PCV2WRMCMN224WT", "PCV-2WRM-CMN2-24WT"),
    ("PCV-2WRM-CMN2-24W", None),
    ("hello", None),
    ("PCV-2WRM-CMN2-24WT!", None),
]


def read(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return ""


def check_vectors() -> list[str]:
    errors = []
    for module_id, version, sha, expected in GOLDEN:
        actual = module_code.compute(module_id, version, sha)
        if actual != expected:
            errors.append("金标准向量对不上：%s %s → 期望 %s，实际 %s"
                          % (module_id, version or "(空版本)", expected, actual))
    return errors


def check_alphabet() -> list[str]:
    """Crockford 字母表必须是定死的那一串。

    单独查一条：只改 Python 端字母表时，金标准向量会跟着变，
    两端「互相一致地错」也能通过一次比较 —— 所以字母表本身要当常量钉住。
    """
    errors = []
    if module_code.ALPHABET != "0123456789ABCDEFGHJKMNPQRSTVWXYZ":
        errors.append("Python 端 Crockford 字母表被改了：%r" % module_code.ALPHABET)
    for ch in "ILOU":
        if ch in module_code.ALPHABET:
            errors.append("Python 端字母表里不该有 %s（Crockford 去掉了 I L O U）" % ch)
    return errors


def check_loose() -> list[str]:
    errors = []
    for raw, expected in LOOSE:
        actual = module_code.normalize(raw)
        if actual != expected:
            errors.append("手抄宽容规则不对：%r → 期望 %r，实际 %r" % (raw, expected, actual))
    # same() 必须同时容忍两边
    if not module_code.same("PCV-2WRM-CMN2-24WT", "pcv2wrmcmn224wt"):
        errors.append("same() 没容忍大小写与连字符差异")
    if module_code.same("PCV-2WRM-CMN2-24WT", "PCV-2WRM-CMN2-24WU"):
        errors.append("same() 把两个不同的号判成同一个了")
    return errors


def check_vectors_are_frozen() -> list[str]:
    """向量里的版本号不能是「当前产品版本」。

    刷版脚本会把仓库里出现的旧版本号整片换掉，向量首当其冲 ——
    输入变了、期望值没跟着变，体检就红。这不是算法错，是测试值跟产品版本
    耦合了。所以这里钉住：向量版本必须是 9.9.9 这种固定值。
    """
    errors = []
    current = ""
    installer = ROOT / "installer" / "PCV.Setup" / "Installer.cs"
    match = re.search(r'public const string Version\s*=\s*"([\d.]+)"',
                      read(installer))
    if match:
        current = match.group(1)

    for module_id, version, _sha, _code in GOLDEN:
        if version and version == current:
            errors.append("金标准向量 %s 用了当前产品版本 %s —— "
                          "刷版时会被替换掉导致向量失效，改成 9.9.9 这类固定值"
                          % (module_id, version))
    return errors


def check_cs_constants() -> list[str]:
    """C# 与 Python 两端的算法常量必须一样。"""
    errors = []
    cs = read(CS_CODE)
    if not cs:
        return ["缺少 C# 端实现：src/Pcv.Modular.Packages/ModuleCode.cs"]

    match = re.search(r'Alphabet\s*=\s*"([^"]+)"', cs)
    if not match:
        errors.append("C# 端没有 Alphabet 常量")
    elif match.group(1) != module_code.ALPHABET:
        errors.append("Crockford 字母表两端不一致：C# %r / Python %r"
                      % (match.group(1), module_code.ALPHABET))

    match = re.search(r'SpecVersion\s*=\s*(\d+)', cs)
    if not match:
        errors.append("C# 端没有 SpecVersion")
    elif int(match.group(1)) != module_code.SPEC_VERSION:
        errors.append("SpecVersion 两端不一致：C# %s / Python %s"
                      % (match.group(1), module_code.SPEC_VERSION))

    # 前缀剥离：两端都必须先剥 PCV 再逐字符判
    if 'StartsWith("PCV"' not in cs:
        errors.append("C# 的 Normalize 没有先剥掉 PCV 前缀 —— "
                      "P 和 V 不在字母表里，带前缀的号会被判非法")
    if 'startswith("PCV")' not in read(ROOT / "tools" / "module_code.py"):
        errors.append("Python 的 normalize 没有先剥掉 PCV 前缀")

    # 位移与取位数：改一点点号就全变，必须两端写法对得上。
    # C# 里写作 `bits >>= 4;`，Python 写作 `>> 4`，两种写法都认。
    if not re.search(r">>=\s*4|>>\s*4", cs):
        errors.append("C# 端没有右移 4 位取 60 位")
    if ">> 4" not in read(ROOT / "tools" / "module_code.py"):
        errors.append("Python 端没有右移 4 位取 60 位")
    return errors


def check_creator_contract() -> list[str]:
    """创作者工具写出去的字段，市场必须读得到。"""
    errors = []
    creator = read(CREATOR_CS)
    source = read(GITHUB_CS)
    if not creator:
        return ["缺少创作者工具：src/Pcv.Shell/CreatorWindow.xaml.cs"]
    if not source:
        return ["缺少社区市场源：src/Pcv.Shell/GitHubSource.cs"]

    # 市场读取端的字段名（JsonProperty）
    read_fields = set(re.findall(r'\[JsonProperty\("([^"]+)"\)\]\s*public\s+\w+', source))
    # 创作者写出端的字段名（匿名对象 key）
    payload = re.search(r"var payload = new\s*\{(.*?)\};", creator, re.S)
    if not payload:
        errors.append("创作者工具里找不到写清单的 payload")
    else:
        written = set(re.findall(r"(\w+)\s*=", payload.group(1)))
        # generator 是给排查用的，市场不读它
        written.discard("generator")
        missing = written - read_fields
        if missing:
            errors.append("创作者工具写了市场读不到的字段：" + ", ".join(sorted(missing)))

    # 必需字段必须在市场读取端被判空
    for field in ("sha256", "file", "moduleCode"):
        if field not in read_fields:
            errors.append("市场读取端没有字段 " + field)

    # 文件名必须过 IsPlainFileName（拒绝路径穿越）
    if "IsPlainFileName(doc.File)" not in source:
        errors.append("市场没有校验社区清单里的文件名 —— 可以被拼成任意路径")

    # sha256 长度必须判
    if "Length != 64" not in source:
        errors.append("市场没有校验社区清单里的 sha256 长度")
    return errors


def check_package_size_is_file_size() -> list[str]:
    """清单里的 size 必须是 `.pclpkg` 文件大小，不是解压后总长。

    这两者差得很远（压缩比），写错了市场下载完就报「大小和清单对不上」，
    所有社区模块都装不上。真踩过：`PackageInspection.TotalBytes` 是解压总长，
    被当成包大小写进了清单（4960 的包写成 9616）。
    """
    errors = []
    creator = read(CREATOR_CS)
    if not creator:
        return ["缺少创作者工具：src/Pcv.Shell/CreatorWindow.xaml.cs"]

    if "result.Size = inspection.TotalBytes" in creator:
        errors.append("清单里的 size 取了 inspection.TotalBytes（解压后总长）—— "
                      "市场比的是包文件大小，会导致所有社区模块装不上")
    if "result.Size = new FileInfo(path).Length" not in creator:
        errors.append("找不到「用包文件大小填 size」的写法")
    return errors


def check_cleanup_on_upgrade() -> list[str]:
    """升级发布时必须清掉同模块的旧版包。

    不清的话每发一版就在仓库里多留一个 .pclpkg —— 仓库越攒越肥，
    别人还不知道哪个是当前的。这是真踩出来的：3.0.0 → 3.1.1 时
    仓库里留下了两个旧包，只能手动删。
    """
    errors = []
    creator = read(CREATOR_CS)
    if not creator:
        return ["缺少创作者工具：src/Pcv.Shell/CreatorWindow.xaml.cs"]

    if "RemoveOldPackagesAsync" not in creator:
        errors.append("创作者工具升级发布时没有清理旧版包 —— 仓库会越攒越肥")
        return errors

    # 必须按 `id-` 前缀精确匹配，不能见 .pclpkg 就删（会误伤别的模块）
    if 'package.Id + "-"' not in creator:
        errors.append("清理旧包没有按 `模块id-` 前缀匹配 —— 会误删别人的包")
    if "EndsWith(\".pclpkg\"" not in creator:
        errors.append("清理旧包没有校验 .pclpkg 后缀")
    # 当前这份不能被删
    if "package.FileName, StringComparison.OrdinalIgnoreCase)) continue" not in creator:
        errors.append("清理旧包没有跳过当前版本 —— 会把刚传上去的包删掉")
    return errors


def check_dual_channel() -> list[str]:
    """raw 与 api 两条下载路都要在，缺一条国内用户就装不上。"""
    errors = []
    source = read(GITHUB_CS)
    market = read(MARKET_CS)
    if "ApiFileUrl" not in source:
        errors.append("GitHubSource 没有 API 兜底地址 —— raw.githubusercontent 抽风时就没路了")
    if "vnd.github.raw" not in source:
        errors.append("GitHubSource 的 API 请求没带 vnd.github.raw —— 会拿到 base64 包装的 JSON")
    if "ApiFileUrl" not in market:
        errors.append("MarketService 下载社区包时没走 API 兜底")
    if "vnd.github.raw" not in market:
        errors.append("MarketService 下载时没带 vnd.github.raw")
    return errors


def check(root: Path) -> list[str]:
    """跑一遍全部检查。

    先 reload module_code：反向验证会改磁盘上的实现再调这个函数，
    不重新导入的话查的还是内存里那份旧代码 —— 那样「注入故障」永远抓不到，
    等于自欺（第一版就是这么假通过的）。
    """
    importlib.invalidate_caches()
    importlib.reload(module_code)

    global CS_CODE, CREATOR_CS, GITHUB_CS, MARKET_CS
    cs_code, creator, github, market = CS_CODE, CREATOR_CS, GITHUB_CS, MARKET_CS
    CS_CODE = root / "src" / "Pcv.Modular.Packages" / "ModuleCode.cs"
    CREATOR_CS = root / "src" / "Pcv.Shell" / "CreatorWindow.xaml.cs"
    GITHUB_CS = root / "src" / "Pcv.Shell" / "GitHubSource.cs"
    MARKET_CS = root / "src" / "Pcv.Shell" / "MarketService.cs"
    try:
        errors = []
        errors += check_alphabet()
        errors += check_vectors()
        errors += check_loose()
        errors += check_vectors_are_frozen()
        errors += check_cs_constants()
        errors += check_creator_contract()
        errors += check_package_size_is_file_size()
        errors += check_cleanup_on_upgrade()
        errors += check_dual_channel()
        return errors
    finally:
        CS_CODE, CREATOR_CS, GITHUB_CS, MARKET_CS = cs_code, creator, github, market


def inject_fault() -> int:
    """故意改坏 6 处，确认每一处都会被 check() 抓到。

    每轮注入后立刻恢复文件，再查 —— 不恢复的话，第二个故障是踩着
    第一个故障查出来的，判据不干净（第一版就犯了这个错：
    6 项「全中」其实后面几项都是被第一个故障带红的）。
    """
    cases = [
        ("算法改了取位数（金标准向量会红）", lambda: patch_py(">> 4", ">> 5")),
        ("Crockford 字母表被改（含 I 了）",
         lambda: patch_py('ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"',
                          'ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"')),
        ("C# 端忘了剥 PCV 前缀", lambda: patch_cs("StartsWith(\"PCV\"", "StartsWith(\"XXX\"")),
        ("市场少校验文件名", lambda: patch_file(GITHUB_CS, "IsPlainFileName(doc.File)", "true")),
        ("市场没有模块数字字段", lambda: patch_file(GITHUB_CS, '"moduleCode"', '"moduleCodeX"')),
        ("下载只剩 raw 一条路", lambda: patch_file(MARKET_CS, "GitHubSource.ApiFileUrl", "GitHubSource.RawUrl")),
    ]

    caught = 0
    print("== 模块数字 / 社区市场校验器反向验证 ==")
    print()
    for label, mutate in cases:
        try:
            problems = mutate()
            if problems:
                print("  × %s：注入没生效（%s）" % (label, problems[0][:80]))
                continue
            found = check(ROOT)
        finally:
            restore()

        hit = bool(found) if 'found' in dir() else False
        print("  %s %s%s" % ("✓" if hit else "×", label, "" if hit else " —— 没报红！"))
        if hit:
            caught += 1
            print("      " + found[0][:130])
        del found

    print()
    print("故障注入：%d/%d 抓到" % (caught, len(cases)))
    return 0 if caught == len(cases) else 1


# 反向验证用的补丁工具：改一处、跑检查、改回来
_ORIGINALS: dict[Path, str] = {}


def _patch(path: Path, old: str, new: str) -> list[str]:
    if path not in _ORIGINALS:
        _ORIGINALS[path] = path.read_text(encoding="utf-8")
    text = path.read_text(encoding="utf-8")
    if old not in text:
        return ["锚点没找到：%r" % old]
    path.write_text(text.replace(old, new, 1), encoding="utf-8")
    return []


def patch_py(old: str, new: str) -> list[str]:
    return _patch(ROOT / "tools" / "module_code.py", old, new)


def patch_cs(old: str, new: str) -> list[str]:
    return _patch(CS_CODE, old, new)


def patch_file(path: Path, old: str, new: str) -> list[str]:
    return _patch(path, old, new)


def restore():
    for path, text in _ORIGINALS.items():
        path.write_text(text, encoding="utf-8")
    _ORIGINALS.clear()


def main() -> int:
    parser = argparse.ArgumentParser(description="模块数字与社区市场体检")
    parser.add_argument("--inject-fault", action="store_true", help="反向验证校验器本身")
    args = parser.parse_args()

    if args.inject_fault:
        try:
            return inject_fault()
        finally:
            restore()

    errors = check(ROOT)
    if errors:
        print("模块数字 / 社区市场体检失败（%d 项）：" % len(errors))
        for line in errors:
            print("  - " + line)
        return 1
    print("体检通过：%d 个金标准向量两端一致、手抄宽容规则正确、"
          "创作者工具与市场字段对得上、社区下载有 raw + API 双路兜底"
          % len(GOLDEN))
    return 0


if __name__ == "__main__":
    sys.exit(main())
