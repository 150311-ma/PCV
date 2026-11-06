using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Newtonsoft.Json;
using Pcv.Modular.Abstractions;

namespace Pcv.Shell
{
    /// <summary>
    /// 粘贴一段脚本直接跑。
    ///
    /// 不开工程、不编译：把代码落到一个临时框架目录里，写一份 module.json，
    /// 然后走和 .pclpkg 完全一样的运行路径（进程 + 独立窗口）。
    /// 临时框架每贴一次一个目录，互不覆盖。
    /// </summary>
    public partial class ScriptWindow : Window
    {
        private readonly ModuleHostAccessor _host;
        private readonly Action<string> _buildAndInstall;

        public ScriptWindow(ModuleHostAccessor host, Action<string> buildAndInstall = null)
        {
            _host = host;
            _buildAndInstall = buildAndInstall;
            InitializeComponent();
            Code.Text = DefaultTemplate;
            Loaded += (s, e) =>
            {
                GlassHelper.TryEnableRoundedCorners(this);
                Code.Focus();
                Code.CaretIndex = Code.Text.Length;
            };
        }

        private const string DefaultTemplate =
            "import sys\n" +
            "\n" +
            "print(\"你好，我是刚贴进来的脚本\")\n" +
            "print(\"在下面输入点什么，回车发给我\")\n" +
            "\n" +
            "for line in sys.stdin:\n" +
            "    line = line.strip()\n" +
            "    if not line:\n" +
            "        continue\n" +
            "    if line == \"exit\":\n" +
            "        break\n" +
            "    print(\"你说：\" + line)\n";

        /// <summary>
        /// 构建并安装：把贴进来的东西当成"生成 .pclpkg 的脚本"，交给主窗口跑。
        /// 跑完自动装 —— 这条和拖一个 .py 进来完全同一条代码路径，不另开一套。
        /// </summary>
        private void BtnBuild_Click(object sender, RoutedEventArgs e)
        {
            var code = StripFences(Code.Text ?? "");
            if (string.IsNullOrWhiteSpace(code))
            {
                LabHint.Text = "空的，没东西可构建。";
                return;
            }

            if (_buildAndInstall == null)
            {
                LabHint.Text = "宿主没把构建入口传进来，这个窗口只能直接运行。";
                return;
            }

            Close();
            _buildAndInstall(code);
        }

        private void BtnPaste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!Clipboard.ContainsText())
                {
                    LabHint.Text = "剪贴板里没有文字。";
                    return;
                }

                Code.Text = StripFences(Clipboard.GetText());
                LabHint.Text = "已从剪贴板读入 " + Code.Text.Length + " 个字符。";
            }
            catch (Exception ex)
            {
                LabHint.Text = "读剪贴板失败：" + ex.Message;
            }
        }

        /// <summary>
        /// AI 输出常被包在 markdown 代码块里（```python … ```）。
        /// 直接跑会 SyntaxError，所以在入口处统一剥掉，而不是让用户自己删。
        /// </summary>
        private static string StripFences(string text)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n').ToList();
            if (lines.Count == 0) return "";

            // 去掉开头/结尾的 ``` 行（含 ```python、```py 之类）
            var first = lines.FindIndex(line => line.TrimStart().StartsWith("```"));
            if (first >= 0)
            {
                var last = lines.FindLastIndex(first + 1, line => line.Trim().StartsWith("```"));
                if (last > first) lines = lines.GetRange(first + 1, last - first - 1);
                else lines.RemoveRange(0, first + 1);
            }

            return string.Join("\n", lines);
        }

        private void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            var code = Code.Text ?? "";
            if (string.IsNullOrWhiteSpace(code))
            {
                LabHint.Text = "空的，没东西跑。";
                return;
            }

            try
            {
                var id = "pcv.paste." + DateTime.Now.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
                var root = Path.Combine(_host.BaseDirectory, "PCLData", "pasted", id);
                Directory.CreateDirectory(root);

                File.WriteAllText(Path.Combine(root, "main.py"), code);

                var manifest = new ModuleManifest
                {
                    Id = id,
                    Name = "贴进来的脚本",
                    Version = "0.0.1",
                    Kind = "process",
                    Runtime = "python",
                    Script = "main.py",
                    Dependencies = new System.Collections.Generic.List<string>(),
                    DirectoryPath = root,
                    Description = "从粘贴窗口直接运行的临时脚本"
                };
                File.WriteAllText(Path.Combine(root, "module.json"),
                    JsonConvert.SerializeObject(manifest, Formatting.Indented));

                _host.RunProcessFramework(manifest, root, this);
                Close();
            }
            catch (Exception ex)
            {
                LabHint.Text = "跑不起来：" + ex.Message;
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
    }
}
