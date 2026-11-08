# -*- coding: utf-8 -*-
"""把 PCV 打成一个安装程序（单文件 exe）。

做四件事：
  1. Release 构建整个解决方案；
  2. 摆一份"干净"的待安装目录（只有程序本体和模块，不带 logs / PCLData / .backup）；
  3. 把它压成 payload.zip，塞进安装器工程；
  4. 编译安装器，产物 = dist\PCV-Setup-<当前版本>.exe。

安装器本身不带任何运行时依赖：解包、快捷方式、注册表全在自己身上。
"""
import os
import pathlib
import tempfile
import shutil
import subprocess
import sys
import zipfile

MODULAR = pathlib.Path(__file__).resolve().parent.parent      # modular/
ROOT = MODULAR.parent.parent                                  # 工作区
SHELL_BIN = MODULAR / "src" / "Pcv.Shell" / "bin" / "Release"
CLI = MODULAR / "src" / "Pcv.Modular.Cli" / "bin" / "Release" / "Pcv.Modular.Cli.exe"
SETUP = MODULAR / "installer" / "PCV.Setup"
# 暂存目录放系统临时目录：放进仓库会被当成源码提交上去（上一版就干过）
# 暂存目录：先用 LOCALAPPDATA，%TEMP% 里被杀软盯着时会出现 ".staging 访问被拒绝"
STAGE_ROOT = pathlib.Path(os.environ.get("LOCALAPPDATA", tempfile.gettempdir())) / "PCV-Setup-Build"
STAGE_ROOT.mkdir(parents=True, exist_ok=True)
STAGE = STAGE_ROOT / "stage"
PAYLOAD = SETUP / "payload.zip"
DIST = MODULAR / "dist"

# 默认随安装包带上的框架包（成品 .pclpkg）
PACKAGES = [
    "Pcv.Diagnostics-3.0.0.pclpkg",
    "Pcv.Stat-3.0.0.pclpkg",
    "Pcv.Cleaner-3.0.0.pclpkg",
    "Pcv.Demo.Hello-3.0.0.pclpkg",
    "pcv.py.demo-3.0.0.pclpkg",
    "pcv.app.demo-3.0.0.pclpkg",
    "pcv.mc.launcher-3.0.0.pclpkg",
    "pcv.rel.demo-3.0.0.pclpkg",
]

# 安装目录里不该出现的东西
EXCLUDE_DIRS = {"logs", "PCLData", ".backup", ".trash", ".staging", "bin", "obj"}


def run(args, **kwargs):
    result = subprocess.run(args, capture_output=True, text=True, **kwargs)
    if result.returncode != 0:
        print("命令失败：" + " ".join(str(a) for a in args[:8]))
        print(result.stdout[-1500:])
        print(result.stderr[-1500:])
        sys.exit(1)
    return result.stdout


def stage_files():
    """把要装的东西摆到临时 stage 目录。"""
    if STAGE.exists():
        shutil.rmtree(STAGE)
    STAGE.mkdir(parents=True)

    copied = 0
    for source in SHELL_BIN.iterdir():
        if source.is_file() and source.suffix.lower() in (".exe", ".dll", ".config"):
            shutil.copy2(source, STAGE / source.name)
            copied += 1

    built_modules = SHELL_BIN / "modules"
    modules = STAGE / "modules"
    modules.mkdir()
    if built_modules.is_dir():
        for folder in built_modules.iterdir():
            if folder.is_dir() and not folder.name.startswith("."):
                shutil.copytree(folder, modules / folder.name)
                copied += 1

    for package in PACKAGES:
        path = DIST / package
        if not path.is_file():
            print("  跳过（包不存在）：" + package)
            continue
        run([str(CLI), "--modules", str(modules), "--install", str(path), "--overwrite"])
        copied += 1

    print("  摆好 %d 项，%d 个模块" % (copied, len([p for p in modules.iterdir() if p.is_dir()])))


def write_integrity():
    """按摆好的 stage 目录算文件指纹。

    必须在这里算：stage 里才是最终要装出去的字节。算早了（对着 bin 算）
    会和装到用户机器上的对不上，启动时就会误报「文件被改过」。
    """
    sys.path.insert(0, str(MODULAR / "tools"))
    import build_integrity
    document = build_integrity.build(STAGE)
    if not document["files"]:
        print("  stage 里没有可比对的文件，跳过指纹清单")
        return
    (STAGE / build_integrity.MANIFEST_NAME).write_bytes(build_integrity.manifest_bytes(document))
    print("  指纹清单：%d 个文件" % len(document["files"]))


def make_payload():
    if PAYLOAD.exists():
        PAYLOAD.unlink()

    count = 0
    with zipfile.ZipFile(PAYLOAD, "w", zipfile.ZIP_DEFLATED) as archive:
        for base, dirs, files in os.walk(STAGE):
            dirs[:] = [d for d in dirs if d not in EXCLUDE_DIRS]
            for name in files:
                if Path_lower_ends(name):
                    continue
                full = pathlib.Path(base) / name
                relative = full.relative_to(STAGE).as_posix()
                archive.write(full, relative)
                count += 1

    size = PAYLOAD.stat().st_size / 1024 / 1024
    print("  打包 %d 个文件，%.2f MB" % (count, size))


def Path_lower_ends(name):
    lowered = name.lower()
    return any(lowered.endswith(x) for x in (".pdb", ".log", ".tmp", ".part"))


def build_setup():
    run(["dotnet", "build", str(SETUP / "PCV.Setup.csproj"), "-c", "Release", "-v:q"])
    produced = SETUP / "bin" / "Release" / "PCV-Setup.exe"
    if not produced.is_file():
        print("没生成 PCV-Setup.exe")
        sys.exit(1)

    DIST.mkdir(parents=True, exist_ok=True)
    output = DIST / "PCV-Setup-3.0.0.exe"
    shutil.copy2(produced, output)
    return output


def main():
    print("[1/5] Release 构建")
    run(["dotnet", "build", str(MODULAR / "Pcv.sln"), "-c", "Release", "-v:q"])

    print("[2/5] 摆待安装目录")
    stage_files()

    print("[3/5] 算文件指纹")
    write_integrity()

    print("[4/5] 打 payload.zip")
    make_payload()

    print("[5/5] 编译安装器")
    output = build_setup()

    shutil.rmtree(STAGE, ignore_errors=True)

    size = output.stat().st_size / 1024 / 1024
    print("\n产物：%s（%.2f MB）" % (output, size))
    print("试试：\"%s\" --silent --dir %%TEMP%%\\pcv-test --no-shortcuts --no-registry" % output)


if __name__ == "__main__":
    main()
