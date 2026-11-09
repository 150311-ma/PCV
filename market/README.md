# market/ — 市场清单

**服务端是博客**（Cloudflare Pages），不是这里。这里放的是**清单的真相源**和生成产物。

| 文件 | 谁写 | 内容 |
| --- | --- | --- |
| `catalog.json` | 人工维护 | 每个包的展示信息（名字、简介、图标、颜色、分类、包名） |
| `index.json` | `tools/build_market.py` 生成 | 加上 `size` / `sha256` / `version` / `updated` 后的正式清单 |

`index.json` 会被三处使用，**字节完全相同**：

1. `Pcv.Shell.csproj` 内嵌成 `PCV.market-index.json` —— 断网时的兜底清单；
2. 博客静态目录 `/market/index.json` —— PCV 客户端从线上拉的正式清单；
3. `tools/check_repo.py` 拿它和 `dist/` 对照，防止清单悄悄过期。

## 上架一个新包

1. 把打好的 `.pclpkg` 放进 `dist/`，文件名带版本号（如 `pcv.foo-3.1.1.pclpkg`）。
2. 在 `market/catalog.json` 的 `items` 里加一条，`file` 只写**纯文件名**。
3. 跑 `python tools/build_market.py` 生成清单并自校验。
4. 跑 `python tools/build_market.py --blog <博客项目根目录>` 把清单与包同步到
   `blog/public/market/`、`blog/dist/market/`、`server/web/market/`，顺便同步
   `_routes.json` 和 `_worker.js` 的市场直出分支。
5. 发布博客站点，再用 `python tools/check_repo.py` 确认仓库体检仍全绿。

`size` 和 `sha256` **不要手抄**：重新打包后它们会变，抄一次就错一次，客户端会
因为校验和不一致拒绝安装。`build_market.py` 现场从 `dist/` 算出来填进去。

## 服务端为什么要绕开 Worker

博客的 `_worker.js` 是进阶模式，接管全部请求，而且会把 `.json` 当文章索引做改写
（`stripPostsJson`）、把 `.json` 列进访问密码门禁。市场清单被改写一次，客户端就
解析不出条目；被门禁拦一次，桌面程序（不带 cookie）就永远拿不到。

所以两层保险：

- `_routes.json` 的 `exclude` 加上 `/market/*`，请求由 Pages 静态层直接发出；
- `_worker.js` 的 `fetch()` 入口仍有一条 `/market/` 直出分支作为兜底，
  不经过门禁、不经过改写，缓存只给 60 秒。

`build_market.py --blog` 会保证这三份 `_routes.json` / `_worker.js` 不漂移。

## 校验器本身也要被校验

```bat
python tools/build_market.py --inject-fault
```

会注入「校验和被改」「大小被改」「`file` 拼成外部 URL」三类故障，确认校验器会报红。
另外 `tools/check_repo.py --inject-fault` 里也有一条市场清单的注入场景。
