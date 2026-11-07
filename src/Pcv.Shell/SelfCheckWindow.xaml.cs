using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Host;

namespace Pcv.Shell
{
    /// <summary>
    /// 用户视角的一次性体检：现在装的东西到底健不健康，哪一步出的问题。
    ///
    /// 和 CLI 的 --inject-fault 不是一回事：那边是「加载器会不会识别坏模块」
    /// （开发者验证校验器），这边是「我这套安装有没有坏」（用户排查问题）。
    /// 模块图用的是启动时那次加载的快照，不重新加载一遍——重跑加载器会真的
    /// 把模块再启动一次，副作用比它带来的新鲜度大得多。
    /// </summary>
    public partial class SelfCheckWindow : Window
    {
        private readonly ObservableCollection<CheckItem> _items = new ObservableCollection<CheckItem>();
        private readonly ModuleLoadReport _report;
        private readonly string _modulesDirectory;
        private readonly string _baseDirectory;
        private readonly string _version;

        public SelfCheckWindow(ModuleLoadReport report, string modulesDirectory,
                               string baseDirectory, string version)
        {
            _report = report;
            _modulesDirectory = modulesDirectory;
            _baseDirectory = baseDirectory;
            _version = version;
            InitializeComponent();
            ListChecks.ItemsSource = _items;
            Loaded += (s, e) => GlassHelper.TryEnableRoundedCorners(this);
            RunChecks();
        }

        private void RunChecks()
        {
            _items.Clear();
            try
            {
                var directories = Directory.Exists(_modulesDirectory)
                    ? Directory.GetDirectories(_modulesDirectory)
                        .Where(d => !Path.GetFileName(d).StartsWith("."))
                        .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray()
                    : new string[0];

                CheckModuleGraph(directories);
                var manifests = CheckManifests(directories);
                CheckEntryFiles(manifests);
                CheckDependencies(manifests);
                CheckDirectoryIds(manifests);
                CheckPending();
                CheckLogs();
                CheckWritable();
                CheckHostFiles();
            }
            catch (Exception ex)
            {
                Add("自检本身", "失败", "检查过程出错了：" + ex.Message);
            }

            var passed = _items.Count(i => i.State == "通过");
            var warned = _items.Count(i => i.State == "注意");
            var failed = _items.Count(i => i.State == "失败");
            LabSummary.Text = $"{passed} 项通过 / {warned} 项注意 / {failed} 项失败";
            LabConclusion.Text = failed > 0
                ? "有东西坏了。先看红的那几项，再点「复制报告」把结果贴进 Issue。"
                : warned > 0
                    ? "基本正常，有几项值得留意——不一定是故障。黄色项会写清楚原因。"
                    : "看起来都正常。如果还是装不上，点「复制报告」把结果贴进 Issue。";
        }

        // ── 各项检查 ────────────────────────────────────────────────────────

        private void CheckModuleGraph(string[] directories)
        {
            var entries = _report?.Entries ?? new List<ModuleEntry>();
            var started = entries.Count(e => e.State == ModuleState.Started);
            var skipped = entries.Count(e => e.State == ModuleState.Skipped);
            var failed = entries.Count(e => e.State == ModuleState.Failed);
            var badIds = string.Join("、", entries.Where(e => e.State == ModuleState.Failed)
                .Select(e => e.Id).Take(4));
            var detail = $"启动时加载 {entries.Count} 个模块：{started} 运行中 / {skipped} 跳过 / {failed} 失败"
                         + (failed > 0 ? "（" + badIds + "）" : "")
                         + $"；目录里现有 {directories.Length} 个包。";
            Add("模块图", failed > 0 ? "失败" : skipped > 0 ? "注意" : "通过", detail);
        }

        private Dictionary<string, ModuleManifest> CheckManifests(string[] directories)
        {
            var manifests = new Dictionary<string, ModuleManifest>(StringComparer.OrdinalIgnoreCase);
            var broken = new List<string>();
            foreach (var directory in directories)
            {
                var file = Path.Combine(directory, "module.json");
                if (!File.Exists(file))
                {
                    broken.Add(Path.GetFileName(directory) + " 没有 module.json");
                    continue;
                }
                try
                {
                    var manifest = JsonConvert.DeserializeObject<ModuleManifest>(File.ReadAllText(file));
                    if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id))
                    {
                        broken.Add(Path.GetFileName(directory) + " 的清单没有 id");
                        continue;
                    }
                    manifests[Path.GetFileName(directory)] = manifest;
                }
                catch (Exception ex)
                {
                    broken.Add(Path.GetFileName(directory) + " 的清单读不出来：" + ex.Message);
                }
            }
            Add("模块清单", broken.Count > 0 ? "失败" : "通过",
                broken.Count > 0 ? string.Join("；", broken.Take(4)) : $"{manifests.Count} 份 module.json 都能解析。");
            return manifests;
        }

        private void CheckEntryFiles(Dictionary<string, ModuleManifest> manifests)
        {
            var missing = new List<string>();
            foreach (var pair in manifests)
            {
                var manifest = pair.Value;
                var isProcess = string.Equals(manifest.Kind, "process", StringComparison.OrdinalIgnoreCase);
                var needed = isProcess ? manifest.Script : manifest.Assembly;
                if (string.IsNullOrWhiteSpace(needed) ||
                    !File.Exists(Path.Combine(_modulesDirectory, pair.Key, needed)))
                    missing.Add(pair.Key + (isProcess ? " 的脚本 " : " 的程序集 ") + (needed ?? "(空)"));
            }
            Add("入口文件", missing.Count > 0 ? "失败" : "通过",
                missing.Count > 0 ? "找不到：" + string.Join("；", missing.Take(4))
                                  : $"{manifests.Count} 个包的入口文件都在。");
        }

        private void CheckDependencies(Dictionary<string, ModuleManifest> manifests)
        {
            var installed = new HashSet<string>(manifests.Values.Select(m => m.Id),
                                               StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            foreach (var pair in manifests)
            {
                foreach (var dependency in pair.Value.Dependencies ?? new List<string>())
                {
                    if (!installed.Contains(dependency))
                        missing.Add($"{pair.Key} → {dependency}");
                }
            }
            Add("依赖关系", missing.Count > 0 ? "失败" : "通过",
                missing.Count > 0 ? "缺前置框架：" + string.Join("；", missing.Take(5))
                                  : "每个包要的前置框架都装着。");
        }

        private void CheckDirectoryIds(Dictionary<string, ModuleManifest> manifests)
        {
            var mismatches = manifests
                .Where(p => !string.Equals(p.Key, p.Value.Id, StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{p.Key} ≠ {p.Value.Id}").Take(4).ToList();
            Add("目录与 Id", mismatches.Count > 0 ? "注意" : "通过",
                mismatches.Count > 0 ? "目录名和清单 id 对不上：" + string.Join("；", mismatches)
                                     : "目录名和清单里的 id 一致。");
        }

        private void CheckPending()
        {
            var pending = Path.Combine(_modulesDirectory, ".pending");
            var count = Directory.Exists(pending) ? Directory.GetDirectories(pending).Length : 0;
            Add("待生效的更新", count > 0 ? "注意" : "通过",
                count > 0 ? $"{count} 个包排队中，下次启动 PCV 时才会换上（程序开着时旧的 dll 被锁住）。"
                          : "没有排队等待的安装。");
        }

        private void CheckLogs()
        {
            var file = Path.Combine(_baseDirectory, "logs", "modules.log");
            if (!File.Exists(file))
            {
                Add("运行日志", "通过", "还没有日志文件（新装或没跑过）。");
                return;
            }
            try
            {
                var lines = File.ReadAllLines(file);
                var tail = lines.Skip(Math.Max(0, lines.Length - 300)).ToArray();
                var errors = tail.Where(line =>
                    line.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("失败", StringComparison.Ordinal) >= 0
                    || line.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                var sample = errors.Length > 0
                    ? "；最近一条：" + Trim(errors.Last())
                    : "";
                Add("运行日志", errors.Length > 0 ? "注意" : "通过",
                    $"最近 {tail.Length} 行里有 {errors.Length} 条报错记录" + sample
                    + "（历史记录不代表现在还坏着）。");
            }
            catch (Exception ex)
            {
                Add("运行日志", "注意", "日志读不了：" + ex.Message);
            }
        }

        private void CheckWritable()
        {
            var probe = Path.Combine(_modulesDirectory, ".write-probe-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            try
            {
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                Add("模块目录可写", "通过", "能装新包，也能覆盖已有包。");
            }
            catch (Exception ex)
            {
                Add("模块目录可写", "失败", "写不进去，装包会失败：" + ex.Message);
            }
        }

        private void CheckHostFiles()
        {
            var contract = Path.Combine(_baseDirectory, "Pcv.Modular.Abstractions.dll");
            Add("宿主文件", File.Exists(contract) ? "通过" : "失败",
                File.Exists(contract) ? "契约程序集在，模块能被加载。" : "缺 Pcv.Modular.Abstractions.dll，模块加载不了。");
        }

        // ── 小工具 ──────────────────────────────────────────────────────────

        private static string Trim(string value)
        {
            var text = (value ?? "").Trim();
            return text.Length <= 110 ? text : text.Substring(0, 110) + "…";
        }

        private void Add(string name, string state, string detail)
        {
            _items.Add(new CheckItem(name, state, detail));
        }

        private void BtnRerun_Click(object sender, RoutedEventArgs e)
        {
            LabHint.Text = "装不上东西的时候点这里，报告能直接贴进 Issue。";
            RunChecks();
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            var text = new StringBuilder();
            text.AppendLine("PCV 自检报告");
            text.AppendLine("版本：" + _version);
            text.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine("汇总：" + LabSummary.Text);
            text.AppendLine();
            foreach (var item in _items)
                text.AppendLine($"[{item.State}] {item.Name} — {item.Detail}");
            LabHint.Text = ClipboardWriter.TrySetText(text.ToString())
                ? "报告已复制，粘到 Issue 里就行。"
                : "剪贴板被别的程序占着，没写进去；再点一次试试。";
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !(e.OriginalSource is Button)) DragMove();
        }
    }

    /// <summary>一行的检查结果。颜色由状态决定，界面不自己判断。</summary>
    public sealed class CheckItem
    {
        public CheckItem(string name, string state, string detail)
        {
            Name = name;
            State = state;
            Detail = detail;
            switch (state)
            {
                case "通过":
                    StateBrush = Solid("#16A34A");
                    StatePill = Solid("#E8F7EE");
                    StateInk = Solid("#15803D");
                    break;
                case "注意":
                    StateBrush = Solid("#D97706");
                    StatePill = Solid("#FFF4E5");
                    StateInk = Solid("#9A5B00");
                    break;
                default:
                    StateBrush = Solid("#E5484D");
                    StatePill = Solid("#FDECEC");
                    StateInk = Solid("#C0363A");
                    break;
            }
        }

        public string Name { get; private set; }
        public string State { get; private set; }
        public string Detail { get; private set; }
        public Brush StateBrush { get; private set; }
        public Brush StatePill { get; private set; }
        public Brush StateInk { get; private set; }

        private static Brush Solid(string hex)
            => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }
}
