# assets/ — 图标与提示词

| 文件 | 用途 |
| --- | --- |
| `pcv.ico` | 可执行文件图标（安装器与 PCV.exe） |
| `pcv-256.png` / `pcv-512.png` | 界面里的 logo、README 配图 |
| `prompt-framework.md` | **给 AI 的提示词**：让 DeepSeek / ChatGPT 按格式生成一个可用的构建脚本 |

## prompt-framework.md

这是整个项目"让 AI 写框架"那条路的核心。它规定了：

- `module.json` 每个字段的含义（`id` / `kind` / `assembly` / `entry` / `category` / `dependencies`）；
- 两种框架形态的骨架代码；
- **路径只准用相对路径**：产物写进宿主给的工作目录（`sys.argv[1]`），
  契约 dll 路径从 `sys.argv[2]` 拿，不许硬编码 `C:\...`；
- 产物格式：一个 `.pclpkg`（zip，里面是 `module.json` + dll）。

提示词里写死的这些约束不是摆设——它们和 `Pcv.Modular.Packages` 的校验、`ScriptBuilder` 的
工作目录是配套的。改提示词之前先看那两处代码。

## 改图标的注意

`.ico` 要包含 16/32/48/256 多个尺寸，否则任务栏和高分屏下会糊。用 Pillow 生成：

```python
from PIL import Image
Image.open("pcv-512.png").save("pcv.ico", sizes=[(16,16),(32,32),(48,48),(256,256)])
```
