# tools/ — 构建与发布脚本

都是 Python 3，只依赖标准库（宣传片三支脚本额外用了 Pillow，`make_narrated.py` 还要 numpy 和 edge-tts）。
**都在仓库根目录执行**，脚本内部自己算路径。

| 脚本 | 干什么 | 典型用法 |
| --- | --- | --- |
| `pack_module.py` | 把模块目录打成 `.pclpkg` | `python tools/pack_module.py src/Pcv.Stat/bin/Release dist/Pcv.Stat-3.0.0.pclpkg` |
| `build_installer.py` | 构建整个方案 → 摆干净目录 → 打 payload → 编安装器 | `python tools/build_installer.py` |
| `promo_style.py` | 宣传片共用样式：色值解析自 `TrmTheme.xaml`、版本读自 `Installer.cs` | 被三支宣传片脚本 import，不单独跑 |
| `make_promo.py` | 用真实截图合成帧与宣传片（静音） | `python tools/make_promo.py` |
| `make_narrated.py` | 加旁白（edge-tts）和原创配乐，出有声 B 站版 | `python tools/make_narrated.py` |
| `make_bili.py` | 导出静音备份片（30fps + 静音 AAC 轨）和 1146×717 封面 | `python tools/make_bili.py` |
| `build_pcv_api.py` | 从 `pcv-api/catalog.json` 生成三份线上清单 | `python tools/build_pcv_api.py` |
| `build_pcv_api.py --check` | 只校验产物与真相源一致 | `python tools/build_pcv_api.py --check` |
| `build_pcv_api.py --inject-fault` | 注入 3 类故障，确认校验器自己会报红 | `python tools/build_pcv_api.py --inject-fault` |
| `build_market.py` | 从 `market/catalog.json` + `dist/` 生成市场清单（size / sha256 现场算） | `python tools/build_market.py` |
| `build_market.py --blog` | 同步清单与包到博客三处，连带同步 `_routes.json` / `_worker.js` | `python tools/build_market.py --blog <博客根目录>` |
| `check_repo.py` | 仓库体检：版本一致性、文档引用、清单、市场清单、源码卫生 | `python tools/check_repo.py` |
| `check_repo.py --inject-fault` | 注入 6 类已知故障，确认体检器自己会报红 | `python tools/check_repo.py --inject-fault` |
| `check_trm_theme.py` | TRM 设计令牌体检：浅/深两套字典 key 对齐、界面 XAML 与 C# 无裸色、令牌引用有定义、颜色只用 DynamicResource | `python tools/check_trm_theme.py` |
| `check_trm_theme.py --inject-fault` | 注入 5 类已知故障，确认主题体检器自己会报红 | `python tools/check_trm_theme.py --inject-fault` |

## 需要注意的几点

- **`build_installer.py` 的 `PACKAGES` 名单是硬编码的**，文件名里带版本号。
  改版本号时这里必须同步改，否则会被"跳过"，装出来的包少东西且**不报错**。
  `check_repo.py` 会把这里和 `Installer.Version`、各 `module.json`、README 模块表、`dist/` 产物一起比，
  漏改哪一处都会报红；`.github/workflows/repo-check.yml` 在每个 PR 上跑它。
- `check_repo.py` 只用标准库、不碰 Windows API，所以能在 Linux CI 上跑。
  `--inject-fault` 会在临时副本上改坏仓库再体检，**不通过反向验证的体检不许算通过**。
  临时副本必须带上 `dist/*.pclpkg`：市场检查要对它们算 sha256，排除掉会变成
  「dist 里没这个包」的假阳性。
- **构建前先跑 `build_market.py`**：壳工程把 `market/index.json` 内嵌成断网兜底清单，
  文件不在就编译失败。改了 `dist/` 里的包也必须重跑，否则清单里的 sha256 会过期，
  客户端会拒绝安装。`check_repo.py` 会把这一项也闸住。
- 暂存目录在 `%LOCALAPPDATA%\PCV-Setup-Build`，不放仓库里——曾经放进来过一次，
  被同步脚本当成源码提交了。
- 宣传片有先后依赖：`make_promo.py` 先产出 `promo/frame-*.png`，
  `make_narrated.py` 与 `make_bili.py` 都只读这些帧，输出文件互不覆盖
  （以前两支都写 `PCV-B站版.mp4`，谁后跑谁盖掉谁）。
  帧名单只由 `make_promo.frame_names()` 生成一份，增减镜头时旁白条数会对不上并直接报错。
- 配音用的是微软 Edge 的在线 TTS，**需要联网**；配乐是 numpy 现场合成的，不联网、无版权风险。

## 不在这里的脚本

推仓库、部署到桌面那份（`deploy_desktop.py`）、构建 PCV 本体这些放在工作区上一层
（`PCL-MultiModule/tools/`），因为它们依赖工作区结构，不属于开源仓库。

**文档里不要引用它们**：仓库 clone 下来是没有这些文件的，写了就是一条跑不通的命令。
README / CONTRIBUTING / PR 模板里的构建自检命令都指向仓库内真实存在的脚本。
