using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using LabelMaker.Config;
using LabelMaker.Devices;
using LabelMaker.Services;

namespace LabelMaker
{
    /// <summary>
    /// 打标 HTTP 网关：常驻托盘，把打标机的 TCP 协议包成本机 HTTP 接口
    /// </summary>
    internal static class Program
    {
        private const string AppTitle = "LabelMaker 打标网关";
        private const string AuthorMail = "i-net@qq.com";

        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string LogDir = Path.Combine(BaseDir, "logs");
        private static readonly object LogLock = new object();
        private static readonly object PrintLock = new object();
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static NotifyIcon _tray;
        private static LaserMarkDevice _device;
        private static LocalPrintServer _server;
        private static bool _printing;

        [STAThread]
        private static void Main()
        {
            Directory.CreateDirectory(LogDir);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Log("未处理异常：" + e.Exception.Message);

            // 首次运行没有配置文件时，先在 exe 同目录落一份默认配置，方便直接改
            if (!File.Exists(Path.Combine(BaseDir, "config.json")))
            {
                AppSettings.Current.Save();
                Log("已生成默认配置 config.json");
            }

            ReloadDevice();
            StartServer();

            var menu = new ContextMenuStrip();
            menu.Items.Add("设置", null, (s, e) => ShowSettings());
            menu.Items.Add("打开日志目录", null, (s, e) => OpenFolder(LogDir));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("关于", null, (s, e) => ShowAbout());
            menu.Items.Add("退出", null, (s, e) => Application.Exit());

            _tray = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = AppTitle,
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (s, e) => ShowSettings();

            Log("程序已启动，右键托盘图标可打开设置");
            Application.Run();

            Cleanup();
        }

        // ============================ 设备与 HTTP 服务 ============================

        private static void ReloadDevice()
        {
            try { _device?.Dispose(); } catch { }
            _device = new LaserMarkDevice();
            _device.OnLog += Log;
        }

        private static void StartServer()
        {
            try { _server?.Dispose(); } catch { }
            _server = null;

            var cfg = AppSettings.Current;
            var server = new LocalPrintServer();
            server.OnLog += Log;
            server.PrintHandler = HandlePrintJob;

            string error;
            if (server.Start(cfg.WebPort, cfg.WebAllowLan, out error))
            {
                _server = server;
                if (_tray != null) _tray.Text = AppTitle + " · " + cfg.WebPort;
            }
            else
            {
                Log("HTTP 服务启动失败：" + error);
                MessageBox.Show(
                    "HTTP 服务启动失败：" + error + Environment.NewLine +
                    "常见原因：端口被占用，或以非管理员身份启动时无法监听局域网地址。",
                    AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// HTTP 打印请求的实际处理：连设备 -> 打标 -> 返回结果。
        /// 打标机同一时间只能跑一个任务，这里用标记位挡住并发请求，返回「正忙」。
        /// </summary>
        private static async Task<PrintResult> HandlePrintJob(PrintJob job)
        {
            lock (PrintLock)
            {
                if (_printing) return new PrintResult { Success = false, Busy = true, Message = "打标机正忙，请等待上一条任务完成" };
                _printing = true;
            }

            try
            {
                var cfg = AppSettings.Current;
                var template = string.IsNullOrWhiteSpace(job.TemplatePath) ? cfg.TemplatePath : job.TemplatePath;

                if (!await _device.ConnectAsync(cfg.LaserIp, cfg.LaserPort, cfg.LaserPassword))
                    return new PrintResult { Success = false, Message = "连接或登录打标机失败，请检查设备 IP、端口与密码" };

                var ok = await _device.MarkAsync(job.Items, template, cfg.MarkTimeoutMs);
                return new PrintResult
                {
                    Success = ok,
                    Message = ok ? "打标完成" : "打标失败或等待完成超时"
                };
            }
            catch (Exception ex)
            {
                Log("打标异常：" + ex.Message);
                return new PrintResult { Success = false, Message = "打标异常：" + ex.Message };
            }
            finally
            {
                lock (PrintLock) { _printing = false; }
            }
        }

        // ============================ 界面 ============================

        private static void ShowSettings()
        {
            using (var form = new SettingsForm())
            {
                if (form.ShowDialog() != DialogResult.OK) return;
            }
            Log("配置已保存，正在按新配置重启服务");
            ReloadDevice();
            StartServer();
        }

        private static void ShowAbout()
        {
            MessageBox.Show(
                AppTitle + Environment.NewLine +
                "把八思量打标软件的 TCP 协议封装成本机 HTTP 接口，供 MES、网页或脚本直接调用。" + Environment.NewLine + Environment.NewLine +
                "版本：V1.0" + Environment.NewLine + Environment.NewLine +
                "本软件免费使用，使用前请自行确认参数与打标结果，因使用本工具带来的相关风险由使用者自行承担。",
                "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void OpenFolder(string path)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\""); }
            catch (Exception ex) { Log("打开目录失败：" + ex.Message); }
        }

        private static void Cleanup()
        {
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            try { _server?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
        }

        // ============================ 日志 ============================

        private static void Log(string msg)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg;
            lock (LogLock)
            {
                try
                {
                    var file = Path.Combine(LogDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                    File.AppendAllText(file, line + Environment.NewLine, Utf8NoBom);
                }
                catch { /* 日志写不进去不影响主流程 */ }
            }
        }

        // ============================ 设置窗体 ============================

        private sealed class SettingsForm : Form
        {
            private readonly TextBox _ip = new TextBox();
            private readonly NumericUpDown _devicePort = new NumericUpDown();
            private readonly TextBox _password = new TextBox();
            private readonly TextBox _template = new TextBox();
            private readonly NumericUpDown _webPort = new NumericUpDown();
            private readonly CheckBox _allowLan = new CheckBox();

            public SettingsForm()
            {
                var cfg = AppSettings.Current;

                Text = "设置";
                Font = new Font("Microsoft YaHei UI", 9f);
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterScreen;
                ClientSize = new Size(560, 272);
                MinimizeBox = false;
                MaximizeBox = false;
                ShowInTaskbar = true;

                _ip.Text = cfg.LaserIp;
                _devicePort.Minimum = 1;
                _devicePort.Maximum = 65535;
                _devicePort.Value = Math.Max(1, Math.Min(65535, cfg.LaserPort));
                _password.Text = cfg.LaserPassword;
                _template.Text = cfg.TemplatePath;
                _webPort.Minimum = 1;
                _webPort.Maximum = 65535;
                _webPort.Value = Math.Max(1, Math.Min(65535, cfg.WebPort));
                _allowLan.Text = "允许局域网访问（不勾选时只监听 127.0.0.1）";
                _allowLan.AutoSize = true;
                _allowLan.Checked = cfg.WebAllowLan;

                var table = new TableLayoutPanel
                {
                    Location = new Point(12, 12),
                    Size = new Size(536, 204),
                    ColumnCount = 2
                };
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                AddRow(table, "打标机 IP", _ip);
                AddRow(table, "打标机端口", _devicePort);
                AddRow(table, "登录密码", _password);
                AddRow(table, "模板路径", _template);
                AddRow(table, "HTTP 端口", _webPort);
                AddRow(table, "局域网访问", _allowLan);

                var save = new Button { Text = "保存并重启服务", DialogResult = DialogResult.OK, AutoSize = true };
                var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
                save.Click += (s, e) => SaveSettings();

                var buttons = new FlowLayoutPanel
                {
                    Location = new Point(0, 224),
                    Size = new Size(560, 44),
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(10)
                };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(save);

                Controls.Add(table);
                Controls.Add(buttons);
                AcceptButton = save;
                CancelButton = cancel;
            }

            private static void AddRow(TableLayoutPanel table, string label, Control control)
            {
                var row = table.RowCount;
                table.RowCount = row + 1;
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

                var lb = new Label
                {
                    Text = label,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Dock = DockStyle.Fill
                };
                control.Dock = DockStyle.Fill;

                table.Controls.Add(lb, 0, row);
                table.Controls.Add(control, 1, row);
            }

            private void SaveSettings()
            {
                var cfg = AppSettings.Current;
                cfg.LaserIp = _ip.Text.Trim();
                cfg.LaserPort = (int)_devicePort.Value;
                cfg.LaserPassword = _password.Text;
                cfg.TemplatePath = _template.Text.Trim();
                cfg.WebPort = (int)_webPort.Value;
                cfg.WebAllowLan = _allowLan.Checked;
                cfg.Save();
            }
        }
    }
}
