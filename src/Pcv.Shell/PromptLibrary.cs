using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Pcv.Shell
{
    /// <summary>
    /// 制作说明（提示词）的唯一来源：嵌在程序集里，不依赖外面的文件。
    /// 壳和命令行验证都从这里取，免得哪天两处内容不一致。
    /// </summary>
    internal static class PromptLibrary
    {
        private const string Fallback =
            "为 PAINCHARNWAV（PCV）写一个功能框架包。只输出一个 Python 脚本，" +
            "脚本要生成 module.json 与程序集并打成 .pclpkg。";

        private static string _cached;

        public static string Load()
        {
            if (_cached != null) return _cached;

            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("prompt-framework.md", StringComparison.OrdinalIgnoreCase));

            if (name != null)
            {
                using (var stream = assembly.GetManifestResourceStream(name))
                using (var reader = new StreamReader(stream))
                    _cached = reader.ReadToEnd();
            }

            if (string.IsNullOrWhiteSpace(_cached)) _cached = Fallback;
            return _cached;
        }
    }
}
