<!--
  提交前把这份清单过一遍。勾不上的项，要么补做，要么在描述里说明为什么不做。
  这不是走形式——下面每一条都对应一次真实踩过的坑。

  ★ 硬要求：本项目每次更新都必须先有 Issue。
  pr-guard 是 main 的必需状态检查，不满足就合并不了，没有豁免标签。
-->

## 这个 PR 做了什么

<!-- 一两句话。如果是修 bug，写清楚"之前什么现象、为什么" -->

## 关联 issue

Closes #

<!-- 必填：把 # 后面的编号补上（Closes #12 这样）。没有 Issue 就先去开一个：
     https://github.com/150311-ma/PCV/issues/new/choose
     pr-guard 会真的去查这个编号是不是本仓库的 Issue —— 填 PR 号、编号、不写，都会报红。 -->

---

## 提交前自检

### 流程（pr-guard 会查）

- [ ] 已经先开好 Issue，正文写了 `Closes #N`
- [ ] PR 标题是 `<类型>: <一句话>`，且不超过 60 字
- [ ] 类型是 fix / feat / docs / refactor / chore / test / build 之一

### 构建与运行

- [ ] `dotnet build Pcv.sln -c Release` 零错误
- [ ] `python tools/deploy_desktop.py` 跑过，模块自检显示「模块图健康」
- [ ] 界面有改动的话，用 `PCV.exe --render out.png --page <页>` 看过渲染结果
- [ ] 没有再出现「构建输出里的旧模块目录」这种残留（改模块 Id 时最容易忘）

### 版本号（只在升版本时勾）

升版本号要改 **四** 个地方，漏一个不会报错、只会让装出来的东西少一块：

- [ ] 各 `module.json` 的 `version`
- [ ] `tools/build_installer.py` 里 `PACKAGES` 的文件名
- [ ] `installer/PCV.Setup` 的 `Installer.Version` 与 csproj `<Version>`
- [ ] 示例构建脚本里写死的 `VERSION`

### 文档

- [ ] README / ARCHITECTURE / CHANGELOG 里受影响的部分改了
- [ ] 同一个事实没有在第二处重复写（许可、来源、能力说明都只有一处真相源）
- [ ] 删掉了过期截图或过期段落，而不是"留着以防万一"

### 规矩

- [ ] 没有引入新的第三方依赖（引入了要在描述里说明理由）
- [ ] 契约工程（`Pcv.Modular.Abstractions`）仍然不引用 WPF
- [ ] 壳没有新增对模块工程的「真实引用」（只能是 `ReferenceOutputAssembly="false"`）
- [ ] 新模块的 `id` 全小写点分，`assembly` 与 `entry` 和代码完全一致

### 安全

- [ ] 没有把绝对路径（`C:\Users\...`）写进代码或脚本
- [ ] 涉及解包/路径拼接的地方，仍然防住了 `..` 越界
- [ ] 没有提交密钥、令牌、个人目录路径

---

## 怎么验证的

<!-- 别写"应该没问题"。写你实际跑了什么、看到什么。截图或日志都行 -->

```
（命令与输出）
```

## 需要 reviewer 注意的地方

<!-- 有争议的取舍、可能有副作用的地方、你拿不准的设计 -->
