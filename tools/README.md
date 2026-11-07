# tools/ — 构建与发布脚本

都是 Python 3，只依赖标准库（`make_bili.py` / `make_narrated.py` 额外用了 Pillow 和 numpy）。
**都在仓库根目录执行**，脚本内部自己算路径。

| 脚本 | 干什么 | 典型用法 |
| --- | --- | --- |
| `pack_module.py` | 把模块目录打成 `.pclpkg` | `python tools/pack_module.py src/Pcv.Stat/bin/Release dist/Pcv.Stat-2.10.0.pclpkg` |
| `build_installer.py` | 构建整个方案 → 摆干净目录 → 打 payload → 编安装器 | `python tools/build_installer.py` |
| `make_promo.py` | 用真实截图合成宣传片（静音） | `python tools/make_promo.py` |
| `make_narrated.py` | 加旁白（edge-tts）和原创配乐，出有声版 | `python tools/make_narrated.py` |
| `make_bili.py` | 导出 B 站投稿版（30fps + 静音音轨 + 封面） | `python tools/make_bili.py` |
| `check_repo.py` | 仓库体检：版本一致性、文档引用、清单、单一真相源、源码卫生 | `python tools/check_repo.py` |
| `check_repo.py --inject-fault` | 注入 4 类已知故障，确认体检器自己会报红 | `python tools/check_repo.py --inject-fault` |

## 需要注意的几点

- **`build_installer.py` 的 `PACKAGES` 名单是硬编码的**，文件名里带版本号。
  改版本号时这里必须同步改，否则会被"跳过"，装出来的包少东西且**不报错**。
  `check_repo.py` 会把这里和 `Installer.Version`、各 `module.json`、README 模块表、`dist/` 产物一起比，
  漏改哪一处都会报红；`.github/workflows/repo-check.yml` 在每个 PR 上跑它。
- `check_repo.py` 只用标准库、不碰 Windows API，所以能在 Linux CI 上跑。
  `--inject-fault` 会在临时副本上改坏仓库再体检，**不通过反向验证的体检不许算通过**。
- 暂存目录在 `%LOCALAPPDATA%\PCV-Setup-Build`，不放仓库里——曾经放进来过一次，
  被同步脚本当成源码提交了。
- 宣传片三件套有先后依赖：`make_promo.py` 先产出 `promo/frame-*.png`，
  另外两个脚本读这些帧。
- 配音用的是微软 Edge 的在线 TTS，**需要联网**；配乐是 numpy 现场合成的，不联网、无版权风险。

## 不在这里的脚本

推仓库、部署到桌面那份（`deploy_desktop.py`）、构建 PCV 本体这些放在工作区上一层
（`PCL-MultiModule/tools/`），因为它们依赖工作区结构，不属于开源仓库。

**文档里不要引用它们**：仓库 clone 下来是没有这些文件的，写了就是一条跑不通的命令。
README / CONTRIBUTING / PR 模板里的构建自检命令都指向仓库内真实存在的脚本。
