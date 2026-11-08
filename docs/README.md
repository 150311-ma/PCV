# docs/ — 文档与物料

```
docs/
  screenshots/   界面截图（8 张，全部由程序离屏渲染）
  promo/         宣传片、B 站投稿包
```

## screenshots/

八张，都是**程序自己离屏渲染**出来的，不是截图工具抓的：

```bat
PCV.exe --render docs/screenshots/home.png --page home --size 1280x880
PCV.exe --render docs/screenshots/market.png --page market --size 1280x880
PCV.exe --dark --render docs/screenshots/dark.png --page home --size 1280x880
PCV.exe --selfcheck --render docs/screenshots/selfcheck.png
```

界面页是 1280×880；`selfcheck.png` 是窗口原始尺寸 780×620。
市场页走的是 `--render` 里的同步加载路径（异步还没回来就截图会拍到空列表），
所以 `market.png` 里能看到真实线上条目。

好处是尺寸、主题、内容都可控；界面改了重新跑一遍就行。
`--dark` 启动即深色；`--switch-dark` 是窗口加载完再换一次主题，
用来验证 `DynamicResource` 真的会跟着变（写死 `StaticResource` 会当场露馅）。

## promo/

| 文件 | 说明 |
| --- | --- |
| `pcv-promo.mp4` | 静音版宣传片，32 秒，7 帧 + 淡入淡出 |
| `pcv-bili.mp4` | B 站版：56 秒，有旁白和配乐，1080p30 / H.264 / AAC |
| `pcv-cover.jpg` | B 站封面，1146×717，带一张新 UI 真截图 |
| `bili-upload.txt` | 投稿信息：标题备选、分区、简介、标签、旁白原文 |

生成链：`tools/make_promo.py` 先出 `frame-*.png` 和静音片，
`tools/make_narrated.py` 出有声 B 站版，`tools/make_bili.py` 出静音备份片和封面。
后两支只依赖帧，互不覆盖输出文件。

三支脚本共用 `tools/promo_style.py`：色值从 `src/Pcv.Shell/Styles/TrmTheme.xaml`
解析、版本号从 `installer/PCV.Setup/Installer.cs` 读、帧名单由
`tools/make_promo.py` 的 `frame_names()` 统一生成。界面换主题时宣传片不会留在旧配色，
镜头增减时旁白也不会悄悄少一段。

素材全部来自真实界面；旁白用微软 TTS，配乐是 numpy 现场合成的（版权干净）。

## 写文档的规矩

- **一份事实只写一遍**：许可文本只在 `LICENSE`，来源说明只在 `NOTICE.md`，
  能做什么只在 `OPEN-SOURCE.md`。别处引用，不要抄。
- 过期的截图直接删，不要留着"以防万一"——留着的一定会被当成现状。
