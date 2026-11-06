using System;
using System.Collections.Generic;
using System.Linq;

namespace Pcv.Modular.Abstractions
{
    /// <summary>
    /// module.json 的映射。加载器只认这份清单，不扫描程序集里的“魔法约定”。
    /// </summary>
    public sealed class ModuleManifest
    {
        /// <summary>全局唯一标识，建议小写点分：pcl.core / pcl.theme。</summary>
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; } = "1.0.0";
        /// <summary>程序集文件名，相对于模块目录。进程型框架不用这个字段。</summary>
        public string Assembly { get; set; }

        /// <summary>
        /// 框架形态：
        ///   "assembly"（默认）—— .NET 程序集，进程内加载；
        ///   "process"          —— 独立的可执行进程，任何语言都行。
        /// </summary>
        public string Kind { get; set; } = "assembly";

        /// <summary>进程型框架用什么跑：python / node / exec（直接执行 entry 指向的文件）。</summary>
        public string Runtime { get; set; }

        /// <summary>进程型框架的入口文件（相对模块目录），比如 main.py。</summary>
        public string Script { get; set; }

        /// <summary>启动参数模板，空格分隔；支持 {dir} 占位。</summary>
        public string Arguments { get; set; }

        /// <summary>宿主启动时自动拉起（配合"启动后隐藏主窗口"用）。</summary>
        public bool AutoStart { get; set; }

        /// <summary>
        /// 分类，只有两种：
        ///   "framework"（默认）—— 框架，提供能力；
        ///   "software"        —— 软件，调用框架干活，自己不含框架。
        /// </summary>
        public string Category { get; set; } = "framework";

        /// <summary>
        /// 非空表示"这是某个软件的基础框架"，值是那个软件的名字，比如 "PCV"。
        /// 基础框架不提供"打开使用"，它是被用的一方。
        /// </summary>
        public string BaseFor { get; set; }

        public bool IsSoftware => string.Equals(Category, "software", StringComparison.OrdinalIgnoreCase);

        /// <summary>界面上的分组名：内置两种给中文名，自定义的照抄。</summary>
        public string CategoryLabel
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Category)) return "框架";
                if (string.Equals(Category, "framework", StringComparison.OrdinalIgnoreCase)) return "框架";
                if (IsSoftware) return "软件";
                return Category.Trim();
            }
        }

        /// <summary>是不是内置的那两种（决定要不要走"软件必须依赖框架"这类规则）。</summary>
        public bool IsBuiltInCategory =>
            string.Equals(Category, "framework", StringComparison.OrdinalIgnoreCase) || IsSoftware;
        public bool IsBaseFramework => !string.IsNullOrWhiteSpace(BaseFor);
        /// <summary>入口类型的全名；留空则自动取程序集中第一个 IPclModule 实现。</summary>
        public string Entry { get; set; }
        /// <summary>依赖的模块 Id。</summary>
        public List<string> Dependencies { get; set; } = new List<string>();
        /// <summary>同层级内的加载顺序，小的先加载。</summary>
        public int LoadPriority { get; set; }
        public string Author { get; set; }
        public string Description { get; set; }
        /// <summary>可选标签，仅供界面分组，不参与加载逻辑。</summary>
        public List<string> Tags { get; set; } = new List<string>();
        /// <summary>要求的最低宿主版本，语义化版本比较。</summary>
        public string MinHostVersion { get; set; }

        /// <summary>清单文件所在目录，由加载器在发现阶段填入。</summary>
        public string DirectoryPath { get; set; }

        public string DisplayName => string.IsNullOrEmpty(Name) ? Id : Name;
        public string DependencyText => Dependencies == null || Dependencies.Count == 0 ? "-" : string.Join(", ", Dependencies);

        public override string ToString() => $"{Id}@{Version}";

        /// <summary>校验清单自身的完整性，返回错误列表（空表示通过）。</summary>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(Id)) errors.Add("缺少 id");
            else if (Id.Count(c => c == '.') == 0) errors.Add($"id 建议使用点分命名：{Id}");
            if (Kind == "process")
            {
                if (string.IsNullOrWhiteSpace(Script)) errors.Add("进程型框架缺少 script（入口文件）");
                if (string.IsNullOrWhiteSpace(Runtime)) errors.Add("进程型框架缺少 runtime（python / node / exec）");
            }
            else if (string.IsNullOrWhiteSpace(Assembly))
            {
                errors.Add("缺少 assembly");
            }
            if (Dependencies != null && Dependencies.Contains(Id)) errors.Add("模块不能依赖自身");
            if (Dependencies != null && Dependencies.Distinct().Count() != Dependencies.Count)
                errors.Add("dependencies 中存在重复项");
            if (!string.IsNullOrWhiteSpace(MinHostVersion) && !IsVersionAtLeast(HostInfoVersion, MinHostVersion))
                errors.Add($"需要宿主版本 >= {MinHostVersion}，当前 {HostInfoVersion}");

            // category 允许自定义：framework / software 是内置的两种，
            // 其他值一律当作"这个包自己声明的类别"，界面按它分组。
            // 只挡两类明显不合理的值：空串和过长（会被当分组标题显示）。
            if (!string.IsNullOrEmpty(Category))
            {
                var trimmed = Category.Trim();
                if (trimmed.Length == 0) errors.Add("category 不能只由空格组成");
                if (trimmed.Length > 24) errors.Add("category 最长 24 个字符，现在是 " + trimmed.Length);
            }

            if (IsSoftware)
            {
                // 软件的三个硬性条件里，能静态查的就这两条
                if (Dependencies == null || Dependencies.Count == 0)
                    errors.Add("软件必须依赖至少一个框架（它自己不含框架，能力要向框架要）");
                if (IsBaseFramework)
                    errors.Add("软件不能声明自己是基础框架（baseFor 只属于框架）");
            }

            return errors;
        }

        /// <summary>由加载器注入，用于 MinHostVersion 校验。</summary>
        public static string HostInfoVersion { get; set; } = "1.0.0";

        private static bool IsVersionAtLeast(string current, string required)
        {
            if (!System.Version.TryParse(Normalize(current), out var c)) return true;
            if (!System.Version.TryParse(Normalize(required), out var r)) return true;
            return c >= r;
        }

        private static string Normalize(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return "0.0.0";
            var parts = v.Split('-')[0].Split('.');
            while (parts.Length < 2) parts = parts.Concat(new[] { "0" }).ToArray();
            return string.Join(".", parts.Take(4));
        }
    }
}
