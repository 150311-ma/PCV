# -*- coding: utf-8 -*-
"""把一个已构建的模块目录打成 .pclpkg。

用法：
    python tools/pack_module.py src/PCL.Demo.Hello/bin/Debug dist/PCL.Demo.Hello.pclpkg

约定（和 PackageInstaller 的检查项一一对应）：
  · 包根目录必须有 module.json
  · module.json 里 assembly 指向的文件必须在包内
  · 只打白名单扩展名，避免把 pdb、日志、缓存一起塞进去
"""
import os
import sys
import zipfile

ALLOWED = {".dll", ".json", ".txt", ".md", ".xaml", ".png", ".jpg", ".ico", ".xml",
           ".py", ".js", ".mjs", ".ts", ".lua", ".rb", ".ps1", ".sh", ".cmd", ".bat",
           ".exe", ".jar", ".node", ".html", ".css", ".csv", ".yml", ".yaml", ".toml"}


def main(source, target):
    if not os.path.isdir(source):
        raise SystemExit(f"目录不存在：{source}")

    manifest = os.path.join(source, "module.json")
    if not os.path.isfile(manifest):
        raise SystemExit(f"{source} 里没有 module.json")

    os.makedirs(os.path.dirname(os.path.abspath(target)), exist_ok=True)
    count = 0
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as archive:
        for base, _, files in os.walk(source):
            for name in sorted(files):
                if os.path.splitext(name)[1].lower() not in ALLOWED:
                    continue
                path = os.path.join(base, name)
                # 相对路径用 /，保证跨平台解压一致
                relative = os.path.relpath(path, source).replace("\\", "/")
                archive.write(path, relative)
                count += 1

    size = os.path.getsize(target)
    print(f"已打包 {target}：{count} 个文件，{size / 1024:.1f} KB")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    main(sys.argv[1], sys.argv[2])
