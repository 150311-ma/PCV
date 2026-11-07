# -*- coding: utf-8 -*-
"""仓库体检：把「容易悄悄过期、又没人会发现」的东西变成会报红的检查。

为什么单独写：这个项目踩过太多次「版本号改了四处漏了第五处」「文档里写的
脚本其实不在仓库里」「README 还停留在上个版本」。这些都不会让构建失败，
只会让下一个人照着一份看起来很正式的过期说明去做事。

检查项：
    1. 版本号：所有写死版本的位置必须等于 Installer.Version
    2. 文档引用：README / CONTRIBUTING / 模板里提到的仓库内脚本必须真实存在
    3. 模块清单：id / assembly / entry / category 合法
    4. 随包模块表：README 那张表必须和 build_installer.py 的 PACKAGES 对得上
    5. 源码卫生：没有绝对路径、没有硬编码令牌
    6. 市场清单：index.json 与 catalog.json / dist 里的包一致（含 sha256）
    7. 工作流：两个闸门文件都在

用法：
    python tools/check_repo.py                 # 体检，退出码 0=健康 / 1=有问题
    python tools/check_repo.py --inject-fault  # 反向验证：注入已知故障，确认体检会报红
    python tools/check_repo.py --root <目录>   # 体检另一份仓库（内部用）

--inject-fault 是硬性要求：一个从不报红的体检等于没有体检。
"""
import argparse
import json
import os
import pathlib
import re
import shutil
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
VERSION_FILE = "installer/PCV.Setup/Installer.cs"
SKIP_DIRS = {"bin", "obj", ".git", ".vs", "PCLData", "logs", "stage", ".build"}
TEXT_SUFFIXES = {".py", ".cs", ".json", ".md", ".xaml", ".yml", ".yaml", ".csproj", ".cmd", ".txt"}
# 复制一份做故障注入时不要搬这些大东西，检查器只看文本和文件名
# dist/*.pclpkg 必须带上：check_market 要对它们算 sha256。
# 它们每个只有几 KB，带上不会拖慢反向验证；排除掉的话市场检查会因为
# "dist 里没这个包" 一直报红，任何注入都算"抓到了"，那是假阳性。
IGNORE_BIG = shutil.ignore_patterns("*.exe", "*.dll", "*.pdb", "*.mp4", "*.png", "*.jpg",
                                    "*.zip", "bin", "obj", ".git")


class Result:
    def __init__(self):
        self.items = []  # (名称, 是否通过, 说明)

    def add(self, name, ok, detail=""):
        self.items.append((name, bool(ok), str(detail)))

    @property
    def failures(self):
        return [item for item in self.items if not item[1]]

    def render(self):
        lines = []
        for name, ok, detail in self.items:
            label = "PASS" if ok else "FAIL"
            lines.append(f"{label}  {name}" + (f"  ← {detail}" if detail else ""))
        return "\n".join(lines)


def text(root, relative):
    path = root / relative
    return path.read_text(encoding="utf-8") if path.is_file() else ""


def current_version(root):
    match = re.search(r'public const string Version\s*=\s*"([\d.]+)"', text(root, VERSION_FILE))
    return match.group(1) if match else None


def walk(root, suffixes=None):
    """遍历仓库里的文本文件，跳过构建产物。"""
    for base, dirs, names in os.walk(root):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in sorted(names):
            path = pathlib.Path(base) / name
            if suffixes is None or path.suffix.lower() in suffixes:
                yield path


def manifests(root):
    """所有 module.json（源码与示例），跳过 bin/obj 里的拷贝。"""
    for path in walk(root, {".json"}):
        if path.name == "module.json":
            yield path


def module_table_section(readme):
    """只取 README「随包安装的模块」那一段，别把目录表 / 文档表算进来。"""
    match = re.search(r"^## 随包安装的模块\s*$(.*?)(?=^## |\Z)", readme, re.M | re.S)
    return match.group(1) if match else ""


# ---------------------------------------------------------------- 各项检查

def check_versions(root, version, result):
    """版本号：从 Installer.Version 取唯一基准，其余位置逐个比。"""
    if not version:
        result.add("能读出 Installer.Version", False, VERSION_FILE + " 里没找到版本号")
        return

    sites = [
        ("installer/PCV.Setup/PCV.Setup.csproj", r"<Version>([\d.]+)</Version>"),
        ("src/Pcv.Modular.Abstractions/ModuleManifest.cs", r'HostInfoVersion\s*\{[^}]*\}\s*=\s*"([\d.]+)"'),
        ("src/Pcv.Modular.Host/Runtime.cs", r'HostVersion\s*\{[^}]*\}\s*=\s*"([\d.]+)"'),
        ("src/Pcv.Shell/App.xaml.cs", r'HostVersion\s*=\s*"([\d.]+)"'),
        ("src/Pcv.Shell/MainWindow.xaml", r'x:Name="LabVersion"[^>]*?Text="([\d.]+)"'),
        ("tools/build_installer.py", r"PCV-Setup-([\d.]+)\.exe"),
        ("README.md", r"PCV-Setup-([\d.]+)\.exe"),
        ("samples/relative-demo/build.py", r'VERSION\s*=\s*"([\d.]+)"'),
        ("samples/deepseek-mclaunch/build-mclaunch.py", r'VERSION\s*=\s*"([\d.]+)"'),
    ]
    wrong = []
    for relative, pattern in sites:
        found = re.findall(pattern, text(root, relative))
        if not found:
            wrong.append(f"{relative} 没找到版本号（正则没匹配上）")
        else:
            for value in found:
                if value != version:
                    wrong.append(f"{relative} = {value}，应为 {version}")
    result.add(f"写死版本号的位置都是 {version}", not wrong,
               "；".join(wrong[:6]) + (f" …共 {len(wrong)} 处" if len(wrong) > 6 else ""))

    # 所有 module.json
    wrong = []
    count = 0
    for path in manifests(root):
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (ValueError, UnicodeDecodeError) as error:
            wrong.append(f"{path.relative_to(root)} 解析失败：{error}")
            continue
        count += 1
        if data.get("version") != version:
            wrong.append(f"{path.relative_to(root)} = {data.get('version')}，应为 {version}")
    result.add(f"{count} 份 module.json 版本都是 {version}", not wrong, "；".join(wrong[:6]))

    # 打包清单与 dist 产物文件名
    packages = re.findall(r'"([^"]+\.pclpkg)"', text(root, "tools/build_installer.py"))
    wrong = [p for p in packages if not p.endswith(f"-{version}.pclpkg")]
    result.add(f"PACKAGES 的 {len(packages)} 个包都用 {version}", bool(packages) and not wrong,
               "；".join(wrong[:5]))

    dist = root / "dist"
    if dist.is_dir():
        artifacts = [p.name for p in dist.iterdir()
                     if p.suffix.lower() in (".pclpkg", ".exe") and not p.name.startswith(".")]
        wrong = [name for name in artifacts if version not in name]
        result.add(f"dist 里 {len(artifacts)} 个产物都用 {version}", not wrong, "；".join(wrong[:6]))

    # README 的随包模块表：版本列必须一致（只取那一段，别把目录表也算进来）
    table = re.findall(r"^\|\s*`[^`]+`\s*\|[^|]*\|\s*([\d.]+)\s*\|",
                       module_table_section(text(root, "README.md")), re.M)
    wrong = [v for v in table if v != version]
    result.add(f"README 模块表 {len(table)} 行版本都是 {version}", bool(table) and not wrong,
               "；".join(wrong[:6]))


def check_doc_links(root, result):
    """文档里点名的仓库内脚本/文件必须真的在仓库里。"""
    docs = ["README.md", "CONTRIBUTING.md", "CHANGELOG.md", "ARCHITECTURE.md",
            "src/README.md", "tools/README.md", "samples/README.md", "installer/README.md",
            "docs/README.md", "assets/README.md", "dist/README.md",
            ".github/PULL_REQUEST_TEMPLATE.md"]
    # 只看像是仓库内路径的引用；URL、示例产物、占位符都跳过
    # 路径部分用 \w（含汉字）而不是 [A-Za-z0-9_]：文档里写
    # `tools/这个脚本.py` 也是引用，早期只认 ASCII 的版本会整条漏掉
    # ——反向验证注入的恰好就是中文路径，于是「验证通过」其实是假阳性。
    pattern = re.compile(r"(?<![\w/])((?:src|tools|samples|installer|assets|docs|dist|\.github)/"
                         r"[\w./-]+\.(?:py|cmd|cs|json|md|yml|yaml|sln|exe|pclpkg))")
    missing = []
    checked = 0
    for relative in docs:
        content = text(root, relative)
        if not content:
            continue
        for match in pattern.finditer(content):
            candidate = match.group(1)
            if "<" in candidate or "*" in candidate or "x.y.z" in candidate:
                continue
            checked += 1
            if not (root / candidate).exists():
                missing.append(f"{relative} → {candidate}")
    result.add(f"文档引用的 {checked} 个仓库内文件都存在", not missing, "；".join(missing[:8]))


def check_manifests(root, result):
    """模块清单字段合法性。"""
    problems = []
    count = 0
    for path in manifests(root):
        relative = str(path.relative_to(root)).replace("\\", "/")
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (ValueError, UnicodeDecodeError) as error:
            problems.append(f"{relative} 不是合法 JSON：{error}")
            continue
        count += 1
        module_id = (data.get("id") or "").strip()
        if not re.fullmatch(r"[a-z0-9]+(?:\.[a-z0-9]+)+", module_id):
            problems.append(f"{relative} id 不是小写点分：{module_id}")
        # 进程型框架用 script/runtime 启动，本来就没有 assembly/entry；
        # 拿程序集那套去要求它，是校验器自己的误报，不是清单坏了。
        kind = (data.get("kind") or "assembly").strip().lower()
        if kind == "process":
            if not (data.get("script") or "").strip():
                problems.append(f"{relative} 是 process 型但缺 script")
            if not (data.get("runtime") or "").strip():
                problems.append(f"{relative} 是 process 型但缺 runtime")
        elif kind == "assembly":
            if not (data.get("assembly") or "").strip():
                problems.append(f"{relative} 缺 assembly")
            if not (data.get("entry") or "").strip():
                problems.append(f"{relative} 缺 entry")
        else:
            problems.append(f"{relative} kind 只能是 assembly / process，实际 {kind}")
        category = (data.get("category") or "").strip()
        if len(category) > 24:
            problems.append(f"{relative} category 超过 24 字")
        dependencies = data.get("dependencies") or []
        if not isinstance(dependencies, list):
            problems.append(f"{relative} dependencies 不是数组")
        elif any(not isinstance(item, str) or not item.strip() for item in dependencies):
            problems.append(f"{relative} dependencies 里有空值")
    result.add(f"{count} 份 module.json 字段合法", not problems, "；".join(problems[:6]))


def check_package_table(root, result):
    """README 的随包模块表不能自己编一份名单——必须和 PACKAGES 对得上。"""
    packages = re.findall(r'"([^"]+\.pclpkg)"', text(root, "tools/build_installer.py"))
    packaged_ids = set()
    for name in packages:
        stem = name.rsplit("-", 1)[0]
        packaged_ids.add(stem.lower())
    table_ids = set(re.findall(r"^\|\s*`([^`]+)`\s*\|",
                               module_table_section(text(root, "README.md")), re.M))
    # 表里有三个是壳自带的（core / theme / microsoft），不在 PACKAGES 里
    built_in = {"pcv.core", "pcv.theme", "pcv.launch.microsoft"}
    extra = table_ids - packaged_ids - built_in
    missing = packaged_ids - table_ids
    # PACKAGES 用文件名前缀，表里用模块 id，两者大小写/点分可能不同，只报明显的差异
    detail = []
    if extra:
        detail.append("表里多出：" + ", ".join(sorted(extra)))
    if missing:
        detail.append("表里少了：" + ", ".join(sorted(missing)))
    result.add(f"README 模块表（{len(table_ids)} 行）与 PACKAGES（{len(packaged_ids)} 个）对得上",
               not detail, "；".join(detail))


def check_hygiene(root, result):
    """源码与脚本里不该有本机绝对路径或硬编码令牌。"""
    absolute = re.compile(r"[A-Za-z]:[\\/](?:Users|Documents|Desktop)[\\/]|/Users/[A-Za-z0-9_.-]+/")
    secret = re.compile(r"ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|"
                        r"(?:GH_TOKEN|GITHUB_TOKEN)\s*=\s*[\"'][^\"']{10,}[\"']")
    problems = []
    for path in walk(root, {".py", ".cs", ".json", ".xaml", ".csproj"}):
        relative = str(path.relative_to(root)).replace("\\", "/")
        try:
            content = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        for number, line in enumerate(content.splitlines(), 1):
            if absolute.search(line):
                problems.append(f"{relative}:{number} 绝对路径")
            if secret.search(line):
                problems.append(f"{relative}:{number} 疑似硬编码令牌")
    result.add("源码与脚本没有绝对路径 / 硬编码令牌", not problems, "；".join(problems[:6]))


def check_market(root, result):
    """市场清单：index.json 必须是 dist 现状的真实反映，不能手抄后忘改。

    复用 build_market.build() 现场重算一份，和仓库里那份逐字段比。
    不自己再写一套解析 —— 两份校验逻辑必然漂移。
    """
    try:
        sys.path.insert(0, str(root / "tools"))
        import build_market
        expected, problems = build_market.build(root)
        issues = build_market.verify(root, expected, problems)
        result.add(f"市场清单 {len(expected['items'])} 个条目与 dist 一致", not issues,
                   "；".join(issues[:6]))
    except Exception as error:
        result.add("市场清单与 dist 一致", False, f"检查器自身出错：{error}")


def check_workflows(root, result):
    expected = {
        ".github/workflows/pr-guard.yml": "pr-guard",
        ".github/workflows/repo-check.yml": "repo-check",
    }
    missing = [name for name, marker in expected.items()
               if not (root / name).is_file() or marker not in text(root, name)]
    result.add("两个闸门工作流都在", not missing,
               "缺：" + ", ".join(missing) if missing else "")


def run_checks(root):
    result = Result()
    version = current_version(root)
    check_versions(root, version, result)
    check_doc_links(root, result)
    check_manifests(root, result)
    check_package_table(root, result)
    check_hygiene(root, result)
    check_market(root, result)
    check_workflows(root, result)
    return result


# ---------------------------------------------------------------- 反向验证

def inject_fault():
    """造一份坏仓库，确认每个检查项真的会红。

    不通过反向验证的体检不许作为「通过」的依据。
    """
    probe = pathlib.Path(tempfile.mkdtemp(prefix="pcv-check-probe-")) / "repo"
    shutil.copytree(ROOT, probe, ignore=IGNORE_BIG)
    scenarios = []

    def scenario(name, mutate, keyword):
        """改坏一个文件 → 跑体检 → 必须有包含 keyword 的失败项。"""
        target = None
        backup = None
        try:
            target, backup = mutate(probe)
            result = run_checks(probe)
            failed = [f"{n} {d}" for n, ok, d in result.items if not ok]
            hit = any(keyword in item for item in failed)
            scenarios.append((name, hit, "" if hit else "体检没报这一项；失败列表：" + " | ".join(failed)[:200]))
        finally:
            if target is not None and backup is not None:
                target.write_text(backup, encoding="utf-8")

    def bad_version(p):
        target = p / "src/Pcv.Core/module.json"
        backup = target.read_text(encoding="utf-8")
        version = current_version(p)
        changed = backup.replace(f'"version": "{version}"', '"version": "0.0.1"')
        if changed == backup:
            raise RuntimeError("故障没注进去：version 字段没找到")
        target.write_text(changed, encoding="utf-8")
        return target, backup

    def bad_link(p):
        target = p / "README.md"
        backup = target.read_text(encoding="utf-8")
        # 中文文件名也要能被引出来：只认 ASCII 的正则会把它整条漏掉
        target.write_text(backup + "\n\n参考 `tools/这个脚本不存在.py`。\n", encoding="utf-8")
        return target, backup

    def bad_link_ascii(p):
        target = p / "README.md"
        backup = target.read_text(encoding="utf-8")
        target.write_text(backup + "\n\n参考 `tools/no_such_script.py`。\n", encoding="utf-8")
        return target, backup

    def bad_manifest(p):
        target = p / "src/Pcv.Core/module.json"
        backup = target.read_text(encoding="utf-8")
        target.write_text(backup.replace('"id": "pcv.core"', '"id": "BAD ID"'), encoding="utf-8")
        return target, backup

    def bad_path(p):
        target = p / "tools/check_repo.py"
        backup = target.read_text(encoding="utf-8")
        target.write_text(backup + '\n# C:\\Users\\someone\\secret\n', encoding="utf-8")
        return target, backup

    def bad_market(p):
        target = p / "market/index.json"
        backup = target.read_text(encoding="utf-8")
        data = json.loads(backup)
        data["items"][0]["sha256"] = "0" * 64
        target.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        return target, backup

    scenario("版本号改错会报红", bad_version, "module.json 版本")
    # 判据写具体路径，不写「文档引用」这种检查项名字：
    # 后者只要这一项因为别的原因红了就会假通过。
    scenario("文档引用不存在的中文脚本会报红", bad_link, "tools/这个脚本不存在.py")
    scenario("文档引用不存在的英文脚本会报红", bad_link_ascii, "tools/no_such_script.py")
    scenario("模块 id 不合法会报红", bad_manifest, "module.json 字段合法")
    scenario("源码里塞绝对路径会报红", bad_path, "绝对路径")
    scenario("市场清单校验和被改会报红", bad_market, "市场清单")

    shutil.rmtree(probe.parent, ignore_errors=True)
    print("== 体检器反向验证（注入已知故障） ==")
    print()
    for name, passed, detail in scenarios:
        print(("PASS  " if passed else "FAIL  ") + name + ("  ← " + detail if detail else ""))
    failed = sum(1 for _, ok, _ in scenarios if not ok)
    print()
    print(f"{len(scenarios) - failed}/{len(scenarios)} 个故障被正确抓到")
    return 0 if failed == 0 else 1


def main():
    parser = argparse.ArgumentParser(description="PCV 仓库体检")
    parser.add_argument("--root", help="体检哪一份仓库，默认脚本所在仓库")
    parser.add_argument("--inject-fault", action="store_true", help="反向验证：注入故障确认体检会报红")
    args = parser.parse_args()

    if args.inject_fault:
        return inject_fault()

    root = pathlib.Path(args.root).resolve() if args.root else ROOT
    print(f"== 体检 {root} ==")
    print()
    result = run_checks(root)
    print(result.render())
    print()
    failed = len(result.failures)
    print(f"{len(result.items) - failed}/{len(result.items)} 项通过"
          + (f"，{failed} 项要修" if failed else "，仓库健康"))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
