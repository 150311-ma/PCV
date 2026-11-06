using System;
using System.IO;
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

        public ScriptWindow(ModuleHostAccessor host)
        {
            _host = host;
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
