import json, os, pathlib, shutil, subprocess, sys, tempfile, zipfile

PCV_DIR   = r"C:\PCV"
MODULE_ID = "pcv.mclaunch"
NAME      = "Minecraft 下载启动框架"
VERSION   = "3.1.0"
ASM       = "Pcv.McLaunch"
ROOT_NS   = "Pcv.McLaunch"
DEPS      = ["pcv.core"]
OUT_DIR   = pathlib.Path.cwd()

CONTRACT = os.path.join(PCV_DIR, "Pcv.Modular.Abstractions.dll")
if not os.path.isfile(CONTRACT):
    print("找不到契约程序集：" + CONTRACT + "\n请把 PCV_DIR 改成 PCV 的安装目录。")
    sys.exit(3)

work = pathlib.Path(tempfile.mkdtemp(prefix="pcv-"))
try:
    csproj = f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <AssemblyName>{ASM}</AssemblyName>
    <RootNamespace>{ROOT_NS}</RootNamespace>
    <LangVersion>latest</LangVersion>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Pcv.Modular.Abstractions">
      <HintPath>{CONTRACT}</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>"""

    module_json = {
        "id": MODULE_ID,
        "name": NAME,
        "version": VERSION,
        "assembly": ASM + ".dll",
        "entry": ROOT_NS + ".Entry",
        "dependencies": DEPS,
        "loadPriority": 100,
        "author": "PCV",
        "description": "下载指定版本的 Minecraft 客户端并拉起 Java 进程的框架",
        "tags": ["工具", "Minecraft"],
    }

    code = r'''using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using Pcv.Modular.Abstractions;
using Pcv.Modular.Contracts;

namespace Pcv.McLaunch
{
    public sealed class Entry : IPclModule, IFrameworkSurface
    {
        public string Title => "Minecraft 下载启动";

        // 规矩要求"必须有界面"：这里给自己画一个，别让宿主标成"没有提供界面"
        public object CreateView(IModuleContext context)
        {
            var panel = new System.Windows.Controls.StackPanel();
            var options = LoadOptions();

            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "版本：" + options.Version + "　安装目录：" + options.InstallDir,
                FontSize = 12,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x4A, 0x55, 0x63))
            });

            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "点下面的按钮：下载版本清单 → 客户端 jar → 拉起 Java。下载进度写在 logs 里。",
                FontSize = 12,
                Margin = new System.Windows.Thickness(0, 10, 0, 0),
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x7C, 0x87, 0x98))
            });

            var status = new System.Windows.Controls.TextBlock
            {
                Text = "",
                FontSize = 12,
                Margin = new System.Windows.Thickness(0, 12, 0, 0),
                TextWrapping = System.Windows.TextWrapping.Wrap
            };

            var start = new System.Windows.Controls.Button
            {
                Content = "开始下载并启动",
                Width = 160,
                Height = 36,
                Margin = new System.Windows.Thickness(0, 16, 0, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            start.Click += (s, e) =>
            {
                status.Text = "正在跑，细节看 logs\\modules.log…";
                var thread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        OnStart();
                        status.Dispatcher.Invoke(() => status.Text = "流程已经走完，看日志确认结果。");
                    }
                    catch (Exception ex)
                    {
                        status.Dispatcher.Invoke(() => status.Text = "失败：" + ex.Message);
                    }
                });
                thread.IsBackground = true;
                thread.Start();
            };

            panel.Children.Add(start);
            panel.Children.Add(status);
            return panel;
        }

        private IModuleContext _context;
        private ISettingsService _settings;
        private Process _process;

        public void OnLoad(IModuleContext context)
        {
            _context = context;
            _settings = null; context.Services.TryGet(out _settings);
            context.Log.Info("PCV Minecraft 下载启动框架已装载");
        }

        public void OnStart()
        {
            try
            {
                var opts = LoadOptions();
                Directory.CreateDirectory(opts.InstallDir);
                string vjPath = EnsureVersionJson(opts);
                string jarPath = EnsureClientJar(opts, vjPath);
                Launch(opts, jarPath);
            }
            catch (Exception ex)
            {
                _context.Log.Error("Minecraft 启动失败: " + ex.Message);
            }
        }

        public void OnStop()
        {
            var p = _process;
            _process = null;
            if (p == null) return;
            try
            {
                if (!p.HasExited) p.Kill();
            }
            catch { }
            try { p.Dispose(); } catch { }
        }

        private sealed class Options
        {
            public string InstallDir;
            public string Version;
            public string JavaPath;
        }

        private Options LoadOptions()
        {
            return new Options
            {
                InstallDir = GetSetting("mc.installDir", Path.Combine(Path.GetTempPath(), "pcv-minecraft")),
                Version    = GetSetting("mc.version", "1.20.1"),
                JavaPath   = GetSetting("mc.javaPath", "java"),
            };
        }

        private string GetSetting(string key, string fallback)
        {
            if (_settings == null) return fallback;
            try
            {
                var v = _settings.GetValue(key, "");
                return string.IsNullOrEmpty(v) ? fallback : v;
            }
            catch { return fallback; }
        }

        private string EnsureVersionJson(Options opts)
        {
            string dir = Path.Combine(opts.InstallDir, "versions", opts.Version);
            Directory.CreateDirectory(dir);
            string vjPath = Path.Combine(dir, opts.Version + ".json");
            if (File.Exists(vjPath)) return vjPath;

            string manifest = HttpGetText("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
            string vUrl = FindVersionUrl(manifest, opts.Version);
            if (vUrl == null) throw new InvalidOperationException("版本清单里找不到 " + opts.Version);
            string vj = HttpGetText(vUrl);
            File.WriteAllText(vjPath, vj, Encoding.UTF8);
            return vjPath;
        }

        private string EnsureClientJar(Options opts, string vjPath)
        {
            string vj = File.ReadAllText(vjPath, Encoding.UTF8);
            string jarUrl = FindClientJarUrl(vj);
            if (jarUrl == null) throw new InvalidOperationException("版本 json 里没有 client.jar 地址");
            string jarPath = Path.Combine(Path.GetDirectoryName(vjPath), opts.Version + ".jar");
            if (!File.Exists(jarPath)) HttpDownloadFile(jarUrl, jarPath);
            return jarPath;
        }

        private static string FindVersionUrl(string manifest, string version)
        {
            string needle = "\"id\":\"" + version + "\"";
            int i = manifest.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;
            int u = manifest.IndexOf("\"url\":", i, StringComparison.Ordinal);
            if (u < 0) return null;
            int s = manifest.IndexOf('"', u + 6);
            int e = manifest.IndexOf('"', s + 1);
            if (s < 0 || e < 0) return null;
            return manifest.Substring(s + 1, e - s - 1);
        }

        private static string FindClientJarUrl(string vj)
        {
            int i = vj.IndexOf("\"client\":{", StringComparison.Ordinal);
            if (i < 0) return null;
            int u = vj.IndexOf("\"url\":\"", i, StringComparison.Ordinal);
            if (u < 0) return null;
            int s = u + 7;
            int e = vj.IndexOf('"', s);
            if (e < 0) return null;
            return vj.Substring(s, e - s);
        }

        private static string HttpGetText(string url)
        {
            using (var wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers[HttpRequestHeader.UserAgent] = "PCV-McLaunch/1.0";
                return wc.DownloadString(url);
            }
        }

        private static void HttpDownloadFile(string url, string path)
        {
            string tmp = path + ".part";
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "PCV-McLaunch/1.0";
                    wc.DownloadFile(url, tmp);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            finally
            {
                if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { } }
            }
        }

        private void Launch(Options opts, string jarPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName               = opts.JavaPath,
                Arguments              = "-jar \"" + jarPath + "\"",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                WorkingDirectory       = opts.InstallDir,
            };
            _process = Process.Start(psi);
            _context.Log.Info("已下载客户端 " + opts.Version + "，进程 PID=" + _process.Id);
        }
    }
}
'''

    (work / (ASM + ".csproj")).write_text(csproj, encoding="utf-8")
    (work / "Entry.cs").write_text(code, encoding="utf-8")
    (work / "module.json").write_text(json.dumps(module_json, ensure_ascii=False, indent=2), encoding="utf-8")

    build = subprocess.run(["dotnet", "build", "-c", "Release", "-v:q"], cwd=str(work),
                           capture_output=True, text=True)
    if build.returncode != 0:
        print("编译失败：\n" + build.stdout + build.stderr)
        sys.exit(4)

    dll = work / "bin" / "Release" / (ASM + ".dll")
    if not dll.is_file():
        print("编译完了却找不到 dll：" + str(dll))
        sys.exit(5)

    package = OUT_DIR / (MODULE_ID + "-" + VERSION + ".pclpkg")
    with zipfile.ZipFile(package, "w", zipfile.ZIP_DEFLATED) as zip_file:
        zip_file.write(work / "module.json", "module.json")
        zip_file.write(dll, ASM + ".dll")

    print(str(package.resolve()))
finally:
    shutil.rmtree(work, ignore_errors=True)