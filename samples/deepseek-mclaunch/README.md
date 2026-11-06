# DeepSeek 输出的构建脚本（修正版）

这个目录里的 `build-mclaunch.py` 来自用户实测：DeepSeek 按提示词生成的脚本。

原版直接跑会失败，原因是 AI 猜了不存在的 API：

```
error CS1061: "IServiceRegistry"未包含"GetService"的定义
error CS1061: "ISettingsService"未包含"Get"的定义
```

这里只改了两行（宿主真实提供的是 `TryGet` / `GetValue`）：

```diff
- _settings = context.Services.GetService(typeof(ISettingsService)) as ISettingsService;
+ _settings = null; context.Services.TryGet(out _settings);
- var v = _settings.Get(key);
+ var v = _settings.GetValue(key, "");
```

改完一次编译通过，产出 `pcv.mclaunch-1.0.0.pclpkg`。

留着这份是为了两件事：
1. 它验证了"拖脚本 → 自动构建 → 自动装"这条链是真的通的；
2. 它是提示词里那节"契约里真实存在的成员"的反面教材 —— 下一份 AI 输出不该再犯。
