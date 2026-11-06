# 参与贡献

先谢谢。这个项目是一个人维护的，所以流程尽量做减法——**但有三条是硬的**。

## 三条硬规矩

1. **不直接往 `main` 推。** 所有改动走 PR，合并前至少自己把清单过一遍。
2. **一份事实只写一遍。** 许可文本在 `LICENSE`，来源在 `NOTICE.md`，能做什么在 `OPEN-SOURCE.md`，
   版本历史在 `CHANGELOG.md`。别处引用，不要复制粘贴。
3. **不留过期的东西。** 过期的截图、注释掉的代码、"以后可能用得上"的文件，删掉。
   留着的一定会被当成现状。

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
python tools/deploy_desktop.py     # 顺手跑模块自检

git commit -am "fix: 运行中覆盖安装改为排队，下次启动生效"
git push origin fix/install-while-running
```

然后开 PR，正文会自动带上检查单。**勾不上的项要么补做，要么在描述里说明为什么不做。**

### 提交信息

`<类型>: <一句话>`

类型用 `fix` / `feat` / `docs` / `refactor` / `chore`。第一行别超过 60 个字，
细节放正文——因为 `CHANGELOG.md` 是从这里抄的。

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
