# 参与贡献

先谢谢。这个项目是一个人维护的，所以流程尽量做减法——**但下面四条是硬的**，
而且有机器闸门盯着，不是靠自觉。

## 四条硬规矩

1. **每次更新：先 Issue → 再 PR → 两个闸门全绿 → 合并。**
   不直接往 `main` 推。没有「就改个错别字，不用开 Issue」这种例外，闸门也没有豁免标签。
2. **一份事实只写一遍。** 许可文本在 `LICENSE`，来源在 `NOTICE.md`，能做什么在 `OPEN-SOURCE.md`，
   版本历史在 `CHANGELOG.md`。别处引用，不要复制粘贴。
3. **不留过期的东西。** 过期的截图、注释掉的代码、"以后可能用得上"的文件，删掉。
   留着的一定会被当成现状。
4. **提交标题 ≤ 60 字。** GitHub 文件列表右侧那一列显示的就是最近一次提交的标题；
   太长会被截断，一次全量提交还会让整列变成同一句话。

## 每次更新的完整流程

```
1. 开 Issue          写清「现在什么现象 / 想要什么结果」
2. 建分支            git checkout -b fix/xxx
3. 改 + 自测         dotnet build Pcv.sln -c Release
4. 开 PR             正文写一行 Closes #<Issue 编号>，标题 <类型>: <一句话>
5. 等两个闸门       pr-guard 与 repo-check 全绿才能合并
6. squash 合并       Issue 会被 Closes #N 自动关掉，源分支随之删除
```

`main` 上有**两个必需状态检查**，分工不同，少一个都合并不了：

| 检查 | 管什么 | 不合格会怎样 |
| --- | --- | --- |
| `pr-guard` | 流程：正文有 `Closes #N` 且编号是真实 Issue、标题有类型前缀、标题 1–60 字 | 报红 |
| `repo-check` | 内容：`tools/check_repo.py` 查版本一致性 / 文档引用 / 清单 / 单一真相源 / 源码卫生，并用 `--inject-fault` 自检 | 报红 |

`repo-check` 只用标准库，Linux CI 上也能跑——它不编译 .NET，查的是那些
「不会让构建失败、但会让下一个人照着过期说明做事」的东西。

开发机上有一条龙脚本，会自动建 Issue、把 `Closes #N` 补进 PR 正文、等检查、再合并；
也可以按上面的步骤手工做。脚本依赖本机的 GitHub 凭据，不随仓库分发，所以不在 `tools/` 里。

## 提 issue

三种模板，按情况选：

| 模板 | 什么时候用 |
| --- | --- |
| 问题反馈 | 装不上、打不开、功能不对 |
| 包相关 | `.pclpkg` 装不上 / 加载失败 / 自己打的包不对 |
| 功能建议 | 想让 PCV 多做点什么 |

**有用的信息**：PCV 版本、复现步骤、`logs/modules.log`（原样贴，别截图转述）。

## 提 PR

```bash
git clone https://github.com/150311-ma/PCV.git
cd PCV
git checkout -b fix/install-while-running

# 改完之后
dotnet build Pcv.sln -c Release
python tools/check_repo.py          # 仓库体检：版本、文档引用、清单
python tools/check_repo.py --inject-fault

git commit -am "fix: 运行中覆盖安装改为排队"
git push origin fix/install-while-running
```

开 PR 时正文**必须**有一行 `Closes #12`（12 换成你开的 Issue 编号），
模板里的检查单也要过一遍。**勾不上的项要么补做，要么在描述里说明为什么不做。**

### 提交信息

`<类型>: <一句话>`，第一行 ≤ 60 字，细节放正文。
类型用 `fix` / `feat` / `docs` / `refactor` / `chore` / `test` / `build`。
因为 `CHANGELOG.md` 是从这里抄的，也因为 GitHub 文件列表会直接显示它。

## 代码约定

| 位置 | 约定 |
| --- | --- |
| 命名 | 程序集 `Pcv.*`；模块 Id 全小写点分（`pcv.mc.launcher`） |
| 契约 | `Pcv.Modular.Abstractions` **不许引用 WPF**，`IFrameworkSurface` 返回 `object` 就是这个原因 |
| 壳 | 对模块工程只能有 `ReferenceOutputAssembly="false"` 的引用，不能真依赖 |
| 模块间 | 不互相引用程序集，通过 `context.Services` 取接口 |
| 注释 | 解释"为什么"，不解释"是什么"。踩过坑的地方必须写清坑在哪 |
| 字符串 | 面向用户的文案用中文；标识符、日志键名用英文 |

## 加一个模块

1. 复制 `src/Pcv.Demo.Hello` 改名；
2. 写 `module.json`：`id`、`assembly`、`entry`、`category`、`dependencies`；
3. 打包：`python tools/pack_module.py <输出目录> dist/<id>-<版本>.pclpkg`；
4. 拖进 PCV 或命令行装，确认列表里状态是「运行中」而不是「失败」。

写不出来就让 AI 写：把 [`assets/prompt-framework.md`](assets/prompt-framework.md) 整段丢给模型。

## 升版本号

四处要一起改，漏一个不会报错，只会让装出来的东西少一块：

1. 各 `module.json` 的 `version`
2. `tools/build_installer.py` 的 `PACKAGES` 文件名
3. `installer/PCV.Setup` 的 `Installer.Version` 与 csproj `<Version>`
4. 示例构建脚本里写死的 `VERSION`

## 关于"借用 AI 写代码"

可以，而且这个项目本身就有不少是这么来的。但请遵守一条：
**AI 给的 API 调用默认是猜的**，编译不过、运行报错是常态。提交前自己跑一遍，
别把"看起来对"当"验证过"。

## 许可

提交 PR 即表示你同意你的贡献以 [MIT](LICENSE) 授权，并且你确实有权利这么做。
