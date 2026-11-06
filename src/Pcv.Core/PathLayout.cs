using System;
using System.IO;

namespace Pcv.Core
{
    /// <summary>
    /// 目录约定。全集中在这里，别的地方一律用它的属性拼路径——
    /// 想在别处写一个 @"PCLData" 字符串的时候，先想想是不是该加到这里来。
    /// </summary>
    public sealed class PathLayout
    {
        public PathLayout(string baseDirectory)
        {
            BaseDirectory = baseDirectory;
            DataDirectory = Path.Combine(baseDirectory, "PCLData");
            SettingsFile = Path.Combine(DataDirectory, "settings.json");
            LogsDirectory = Path.Combine(baseDirectory, "logs");
            ModulesDirectory = Path.Combine(baseDirectory, "modules");
        }

        public string BaseDirectory { get; }
        public string DataDirectory { get; }
        public string SettingsFile { get; }
        public string LogsDirectory { get; }
        public string ModulesDirectory { get; }

        /// <summary>把该有的目录都建出来。返回失败的目录名，空数组表示全好。</summary>
        public string[] Ensure()
        {
            var failed = new System.Collections.Generic.List<string>();
            foreach (var pair in new[] { ("数据", DataDirectory), ("日志", LogsDirectory) })
            {
                try
                {
                    Directory.CreateDirectory(pair.Item2);
                }
                catch
                {
                    failed.Add(pair.Item1 + "：" + pair.Item2);
                }
            }
            return failed.ToArray();
        }
    }
}
