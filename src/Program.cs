using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ClashFloatStatus
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new StatusForm());
        }
    }

    internal sealed class StatusForm : Form
    {
        private readonly AppSettings settings = AppSettings.Load();
        private readonly Timer refreshTimer = new Timer();
        private readonly Timer keepAliveTimer = new Timer();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolTip toolTip = new ToolTip();
        private readonly ToolStripMenuItem lockItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem startupItem = new ToolStripMenuItem();
        private readonly Font mainFont = new Font("SimHei", 11.5f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font delayFont = new Font("SimHei", 11.5f, FontStyle.Regular, GraphicsUnit.Point);
        private const int ElementGap = 10;
        private NativeMethods.WinEventDelegate foregroundChanged;
        private IntPtr foregroundHook = IntPtr.Zero;

        private bool proxyEnabled;
        private bool locked;
        private bool dragging;
        private bool refreshing;
        private bool hiddenForFullscreen;
        private Point dragOffset;
        private string region = "读取中";
        private string delayText = "--ms";
        private Color delayColor = Color.FromArgb(80, 80, 80);
        private Rectangle switchRect = new Rectangle(4, 7, 40, 20);

        public StatusForm()
        {
            locked = settings.Locked;
            proxyEnabled = ProxyControl.IsEnabled();

            FormBorderStyle = FormBorderStyle.None;
            Text = "ClashFloatStatus";
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = Color.Black;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(176, 34);
            MinimumSize = new Size(130, 34);

            BuildMenu();
            ContextMenuStrip = menu;

            MouseDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseUp += OnMouseUp;
            MouseClick += OnMouseClick;

            refreshTimer.Interval = 10000;
            refreshTimer.Tick += async (s, e) => await RefreshStatusAsync(false);

            keepAliveTimer.Interval = 150;
            keepAliveTimer.Tick += (s, e) => UpdateVisibilityForForeground();

            Load += async (s, e) =>
            {
                RestorePosition();
                ClashForWindowsSettings.SetSystemProxyEnabled(proxyEnabled);
                InstallForegroundHook();
                refreshTimer.Start();
                keepAliveTimer.Start();
                UpdateVisibilityForForeground();
                await RefreshStatusAsync(true);
            };

            FormClosed += (s, e) => UninstallForegroundHook();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DrawContent(e.Graphics);
        }

        private void DrawContent(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            switchRect = new Rectangle(4, (Height - 20) / 2, 40, 20);
            DrawSwitch(g, switchRect, proxyEnabled);

            float centerY = switchRect.Top + switchRect.Height / 2f;
            float regionX = switchRect.Right + ElementGap;
            RectangleF regionBounds = MeasureTextPath(g, region, mainFont);
            DrawTextPath(g, region, mainFont, regionX, centerY);

            float delayX = regionX + regionBounds.Width + ElementGap;
            DrawTextPath(g, delayText, delayFont, delayX, centerY);
        }

        private static RectangleF MeasureTextPath(Graphics g, string text, Font font)
        {
            if (string.IsNullOrEmpty(text)) return RectangleF.Empty;

            using (StringFormat format = new StringFormat(StringFormatFlags.NoWrap))
            using (GraphicsPath path = new GraphicsPath())
            {
                format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                float emSize = font.SizeInPoints * g.DpiY / 72f;
                path.AddString(text, font.FontFamily, (int)font.Style, emSize, new PointF(0, 0), format);
                return path.GetBounds();
            }
        }

        private static void DrawTextPath(Graphics g, string text, Font font, float inkLeft, float centerY)
        {
            if (string.IsNullOrEmpty(text)) return;

            using (StringFormat format = new StringFormat(StringFormatFlags.NoWrap))
            using (GraphicsPath path = new GraphicsPath())
            using (SolidBrush brush = new SolidBrush(Color.Black))
            {
                format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                float emSize = font.SizeInPoints * g.DpiY / 72f;
                path.AddString(text, font.FontFamily, (int)font.Style, emSize, new PointF(0, 0), format);

                RectangleF bounds = path.GetBounds();
                using (Matrix matrix = new Matrix())
                {
                    matrix.Translate(inkLeft - bounds.Left, centerY - (bounds.Top + bounds.Height / 2f));
                    path.Transform(matrix);
                }

                g.FillPath(brush, path);
            }
        }

        private void RenderLayeredWindow()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0) return;

            using (Bitmap bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                DrawContent(g);
                NativeMethods.ApplyLayeredBitmap(Handle, bitmap, Location);
            }
        }

        private void InstallForegroundHook()
        {
            if (foregroundHook != IntPtr.Zero) return;

            foregroundChanged = (hook, eventType, hwnd, idObject, idChild, threadId, eventTime) =>
            {
                if (!IsHandleCreated || IsDisposed) return;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        UpdateVisibilityForForeground();
                    }));
                }
                catch
                {
                }
            };

            foregroundHook = NativeMethods.SetForegroundHook(foregroundChanged);
        }

        private void UpdateVisibilityForForeground()
        {
            if (!IsHandleCreated || IsDisposed) return;

            bool fullscreen = NativeMethods.IsForegroundFullscreen(Handle);
            if (fullscreen)
            {
                hiddenForFullscreen = true;
                if (Visible) Hide();
                return;
            }

            if (hiddenForFullscreen && !Visible)
            {
                hiddenForFullscreen = false;
                Show();
                RenderLayeredWindow();
            }

            NativeMethods.KeepTopMost(Handle);
        }

        private void UninstallForegroundHook()
        {
            if (foregroundHook == IntPtr.Zero) return;
            NativeMethods.UnhookWinEvent(foregroundHook);
            foregroundHook = IntPtr.Zero;
        }

        private static void DrawSwitch(Graphics g, Rectangle rect, bool enabled)
        {
            const int scale = 4;
            using (Bitmap bitmap = new Bitmap(rect.Width * scale, rect.Height * scale))
            using (Graphics high = Graphics.FromImage(bitmap))
            {
                high.SmoothingMode = SmoothingMode.AntiAlias;
                high.InterpolationMode = InterpolationMode.HighQualityBicubic;
                high.PixelOffsetMode = PixelOffsetMode.HighQuality;
                high.Clear(Color.Transparent);
                DrawSwitchRaw(high, new Rectangle(0, 0, bitmap.Width - 1, bitmap.Height - 1), enabled, scale);

                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(bitmap, rect);
            }
        }

        private static void DrawSwitchRaw(Graphics g, Rectangle rect, bool enabled, int scale)
        {
            Color fill = enabled ? Color.FromArgb(24, 119, 242) : Color.FromArgb(150, 154, 160);
            using (GraphicsPath path = RoundedRect(rect, rect.Height / 2))
            using (SolidBrush brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
            }

            int pad = 2 * scale;
            int knobSize = rect.Height - pad * 2;
            int knobX = enabled ? rect.Right - knobSize - pad : rect.Left + pad;
            Rectangle knob = new Rectangle(knobX, rect.Top + pad, knobSize, knobSize);
            using (SolidBrush brush = new SolidBrush(Color.White))
            {
                g.FillEllipse(brush, knob);
            }
            using (Pen pen = new Pen(Color.FromArgb(24, 0, 0, 0), scale))
            {
                g.DrawEllipse(pen, knob);
            }
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void BuildMenu()
        {
            lockItem.Text = locked ? "解锁位置" : "锁定位置";
            lockItem.Click += (s, e) =>
            {
                locked = !locked;
                settings.Locked = locked;
                settings.Save();
                lockItem.Text = locked ? "解锁位置" : "锁定位置";
            };

            ToolStripMenuItem refreshItem = new ToolStripMenuItem("立即刷新");
            refreshItem.Click += async (s, e) => await RefreshStatusAsync(true);

            startupItem.Text = "开机启动";
            startupItem.Checked = StartupControl.IsEnabled();
            startupItem.CheckOnClick = false;
            startupItem.Click += (s, e) =>
            {
                bool next = !StartupControl.IsEnabled();
                StartupControl.SetEnabled(next);
                startupItem.Checked = next;
            };

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (s, e) =>
            {
                settings.X = Left;
                settings.Y = Top;
                settings.Save();
                Application.Exit();
            };

            menu.Items.Add(lockItem);
            menu.Items.Add(refreshItem);
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);
        }

        private async Task RefreshStatusAsync(bool force)
        {
            if (refreshing && !force)
            {
                return;
            }

            refreshing = true;
            try
            {
                proxyEnabled = ProxyControl.IsEnabled();
                ClashSnapshot snapshot = await Task.Run(() => ClashClient.ReadSnapshot());
                region = snapshot.Region;
                delayText = snapshot.DelayMs.HasValue ? snapshot.DelayMs.Value + "ms" : "--ms";
                delayColor = GetDelayColor(snapshot.DelayMs, snapshot.HasError);
                ToolTipText(snapshot);
                FitToText();
                RenderLayeredWindow();
            }
            catch
            {
                region = "Clash";
                delayText = "--ms";
                delayColor = Color.FromArgb(130, 130, 130);
                FitToText();
                RenderLayeredWindow();
            }
            finally
            {
                refreshing = false;
            }
        }

        private void ToolTipText(ClashSnapshot snapshot)
        {
            string text = proxyEnabled ? "系统代理：开启" : "系统代理：关闭";
            if (!string.IsNullOrWhiteSpace(snapshot.NodeName))
            {
                text += Environment.NewLine + "当前节点：" + snapshot.NodeName;
            }
            if (!string.IsNullOrWhiteSpace(snapshot.Error))
            {
                text += Environment.NewLine + snapshot.Error;
            }
            toolTip.SetToolTip(this, text);
        }

        private static Color GetDelayColor(int? delay, bool error)
        {
            if (error || !delay.HasValue) return Color.FromArgb(130, 130, 130);
            if (delay.Value <= 180) return Color.FromArgb(20, 140, 75);
            if (delay.Value <= 650) return Color.FromArgb(204, 130, 0);
            return Color.FromArgb(205, 55, 55);
        }

        private void FitToText()
        {
            float regionWidth;
            float delayWidth;
            using (Bitmap bitmap = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                regionWidth = MeasureTextPath(g, region, mainFont).Width;
                delayWidth = MeasureTextPath(g, delayText, delayFont).Width;
            }

            int nextWidth = Math.Max(130, Math.Min(280, (int)Math.Ceiling(switchRect.Left + switchRect.Width + ElementGap + regionWidth + ElementGap + delayWidth + 8)));
            if (Width != nextWidth)
            {
                int oldRight = Right;
                Width = nextWidth;
                if (!settings.HasPosition)
                {
                    RestorePosition();
                }
                else if (Right > Screen.FromControl(this).Bounds.Right)
                {
                    Left = oldRight - Width;
                }
            }
        }

        private void RestorePosition()
        {
            if (settings.HasPosition)
            {
                Location = new Point(settings.X, settings.Y);
                return;
            }

            Rectangle tb = NativeMethods.GetTaskbarBounds();
            if (tb.Width >= tb.Height)
            {
                int x = tb.Right - Width - 260;
                int y = tb.Top + (tb.Height - Height) / 2;
                Location = new Point(Math.Max(tb.Left, x), Math.Max(tb.Top, y));
            }
            else
            {
                int x = tb.Left + (tb.Width - Width) / 2;
                int y = tb.Bottom - Height - 120;
                Location = new Point(Math.Max(tb.Left, x), Math.Max(tb.Top, y));
            }
        }

        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !locked && !switchRect.Contains(e.Location))
            {
                dragging = true;
                dragOffset = e.Location;
                Cursor = Cursors.SizeAll;
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!dragging) return;
            Point screen = PointToScreen(e.Location);
            Location = new Point(screen.X - dragOffset.X, screen.Y - dragOffset.Y);
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            Cursor = Cursors.Default;
            SnapToTaskbar();
            settings.X = Left;
            settings.Y = Top;
            settings.Save();
            RenderLayeredWindow();
        }

        private async void OnMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && switchRect.Contains(e.Location))
            {
                bool next = !ProxyControl.IsEnabled();
                ProxyControl.SetEnabled(next);
                proxyEnabled = next;
                RenderLayeredWindow();
                await RefreshStatusAsync(true);
            }
        }

        private void SnapToTaskbar()
        {
            Rectangle tb = NativeMethods.GetTaskbarBounds();
            if (tb.Width >= tb.Height)
            {
                Top = tb.Top + (tb.Height - Height) / 2;
                Left = Math.Max(tb.Left, Math.Min(Left, tb.Right - Width));
            }
            else
            {
                Left = tb.Left + (tb.Width - Width) / 2;
                Top = Math.Max(tb.Top, Math.Min(Top, tb.Bottom - Height));
            }
        }
    }

    internal sealed class ClashSnapshot
    {
        public string Region = "未知";
        public string NodeName = "";
        public int? DelayMs;
        public string Error = "";
        public bool HasError { get { return !string.IsNullOrWhiteSpace(Error); } }
    }

    internal static class ClashClient
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static ClashSnapshot ReadSnapshot()
        {
            ClashSnapshot snapshot = new ClashSnapshot();
            ClashConfig config = ClashConfig.Find();
            if (config == null || string.IsNullOrWhiteSpace(config.Controller))
            {
                snapshot.Region = "未运行";
                snapshot.Error = "没有找到 Clash 控制接口";
                return snapshot;
            }

            string baseUrl = "http://" + config.Controller.Trim().Trim('\'', '"');
            Dictionary<string, object> proxies = GetJson(baseUrl + "/proxies", config.Secret);
            Dictionary<string, object> proxiesDict = AsDict(proxies, "proxies");
            string nodeName = FindSelectedNode(proxiesDict);
            snapshot.NodeName = nodeName;
            snapshot.Region = RegionParser.Parse(nodeName);

            if (!string.IsNullOrWhiteSpace(nodeName))
            {
                try
                {
                    string encoded = Uri.EscapeDataString(nodeName);
                    string url = baseUrl + "/proxies/" + encoded + "/delay?timeout=5000&url=https%3A%2F%2Fwww.gstatic.com%2Fgenerate_204";
                    Dictionary<string, object> delay = GetJson(url, config.Secret);
                    if (delay.ContainsKey("delay"))
                    {
                        snapshot.DelayMs = Convert.ToInt32(delay["delay"]);
                    }
                }
                catch (Exception ex)
                {
                    snapshot.Error = "延迟测试失败：" + ex.Message;
                }
            }
            else
            {
                snapshot.Error = "没有找到当前节点";
            }

            return snapshot;
        }

        private static string FindSelectedNode(Dictionary<string, object> proxies)
        {
            if (proxies == null) return "";
            string[] preferred = { "🔰 选择节点", "🚀 节点选择", "节点选择", "Proxy", "代理", "GLOBAL" };
            foreach (string key in preferred)
            {
                string now = ResolveNow(proxies, key, 0);
                if (!string.IsNullOrWhiteSpace(now) && !IsBuiltIn(now)) return now;
            }

            foreach (KeyValuePair<string, object> pair in proxies)
            {
                Dictionary<string, object> item = pair.Value as Dictionary<string, object>;
                if (item == null || !item.ContainsKey("now")) continue;
                string now = ResolveNow(proxies, pair.Key, 0);
                if (!string.IsNullOrWhiteSpace(now) && !IsBuiltIn(now)) return now;
            }

            return "";
        }

        private static string ResolveNow(Dictionary<string, object> proxies, string key, int depth)
        {
            if (depth > 8) return key;
            if (!proxies.ContainsKey(key)) return "";
            Dictionary<string, object> item = proxies[key] as Dictionary<string, object>;
            if (item == null || !item.ContainsKey("now")) return "";
            string now = Convert.ToString(item["now"]);
            if (string.IsNullOrWhiteSpace(now) || IsBuiltIn(now)) return now;

            Dictionary<string, object> next = proxies.ContainsKey(now) ? proxies[now] as Dictionary<string, object> : null;
            if (next != null && next.ContainsKey("now"))
            {
                string resolved = ResolveNow(proxies, now, depth + 1);
                if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
            }

            return now;
        }

        private static bool IsBuiltIn(string name)
        {
            return name == "DIRECT" || name == "REJECT" || name == "GLOBAL";
        }

        private static Dictionary<string, object> GetJson(string url, string secret)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 6500;
            request.ReadWriteTimeout = 6500;
            if (!string.IsNullOrWhiteSpace(secret))
            {
                request.Headers["Authorization"] = "Bearer " + secret;
            }

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream))
            {
                string json = reader.ReadToEnd();
                return Serializer.DeserializeObject(json) as Dictionary<string, object>;
            }
        }

        private static Dictionary<string, object> AsDict(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key)) return null;
            return dict[key] as Dictionary<string, object>;
        }
    }

    internal sealed class ClashConfig
    {
        public string Controller = "";
        public string Secret = "";
        public int MixedPort = 7890;

        public static ClashConfig Find()
        {
            foreach (string path in CandidateConfigFiles())
            {
                if (!File.Exists(path)) continue;
                ClashConfig config = Parse(path);
                if (!string.IsNullOrWhiteSpace(config.Controller)) return config;
            }
            return null;
        }

        public static int FindMixedPort()
        {
            ClashConfig config = Find();
            return config == null ? 7890 : config.MixedPort;
        }

        private static ClashConfig Parse(string path)
        {
            ClashConfig config = new ClashConfig();
            string text = File.ReadAllText(path);
            config.Controller = MatchValue(text, "external-controller");
            config.Secret = MatchValue(text, "secret");
            string mixed = MatchValue(text, "mixed-port");
            int port;
            if (int.TryParse(mixed, out port)) config.MixedPort = port;
            return config;
        }

        private static string MatchValue(string text, string key)
        {
            Match match = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(key) + @"\s*:\s*(.+?)\s*$");
            if (!match.Success) return "";
            return match.Groups[1].Value.Trim().Trim('\'', '"');
        }

        private static IEnumerable<string> CandidateConfigFiles()
        {
            foreach (string path in FromRunningClash())
            {
                yield return path;
            }

            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(user, ".config", "clash", "config.yaml");

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            yield return Path.Combine(appData, "clash", "config.yaml");
        }

        private static IEnumerable<string> FromRunningClash()
        {
            Process[] processes = Process.GetProcessesByName("clash-win64");
            foreach (Process process in processes)
            {
                string exe = "";
                try { exe = process.MainModule.FileName; }
                catch { }
                if (string.IsNullOrWhiteSpace(exe)) continue;

                DirectoryInfo dir = new FileInfo(exe).Directory;
                for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, "data", "config.yaml");
                    if (File.Exists(candidate)) yield return candidate;
                }
            }
        }
    }

    internal static class RegionParser
    {
        private static readonly string[] Regions =
        {
            "香港", "澳门", "台湾", "日本", "美国", "新加坡", "韩国", "英国", "德国", "法国",
            "加拿大", "澳大利亚", "俄罗斯", "泰国", "越南", "印度", "土耳其", "荷兰",
            "菲律宾", "马来西亚", "印尼", "巴西", "阿根廷", "墨西哥", "西班牙", "意大利"
        };

        public static string Parse(string nodeName)
        {
            if (string.IsNullOrWhiteSpace(nodeName)) return "未知";
            foreach (string region in Regions)
            {
                if (nodeName.IndexOf(region, StringComparison.OrdinalIgnoreCase) >= 0) return region;
            }

            if (nodeName.Contains("🇭🇰")) return "香港";
            if (nodeName.Contains("🇯🇵")) return "日本";
            if (nodeName.Contains("🇺🇸") || nodeName.Contains("🇺🇲")) return "美国";
            if (nodeName.Contains("🇸🇬")) return "新加坡";
            if (nodeName.Contains("🇹🇼")) return "台湾";
            if (nodeName.Contains("🇰🇷")) return "韩国";

            Match match = Regex.Match(nodeName, @"[A-Za-z\u4e00-\u9fa5]{2,6}");
            return match.Success ? match.Value : "未知";
        }
    }

    internal static class ProxyControl
    {
        private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(InternetSettings, false))
            {
                object value = key == null ? null : key.GetValue("ProxyEnable");
                return value != null && Convert.ToInt32(value) == 1;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(InternetSettings, true))
            {
                if (key == null) return;
                key.SetValue("ProxyEnable", enabled ? 1 : 0, RegistryValueKind.DWord);
                if (enabled)
                {
                    int port = ClashConfig.FindMixedPort();
                    key.SetValue("ProxyServer", "127.0.0.1:" + port, RegistryValueKind.String);
                }
            }
            ClashForWindowsSettings.SetSystemProxyEnabled(enabled);
            NativeMethods.RefreshInternetSettings();
        }
    }

    internal static class ClashForWindowsSettings
    {
        public static void SetSystemProxyEnabled(bool enabled)
        {
            foreach (string path in CandidateFiles())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    string text = File.ReadAllText(path);
                    string replacement = "connProxy: " + (enabled ? "1" : "0");
                    if (Regex.IsMatch(text, @"(?m)^connProxy:\s*\d+\s*$"))
                    {
                        text = Regex.Replace(text, @"(?m)^connProxy:\s*\d+\s*$", replacement);
                    }
                    else
                    {
                        text = replacement + Environment.NewLine + text;
                    }
                    File.WriteAllText(path, text);
                }
                catch
                {
                }
            }
        }

        private static IEnumerable<string> CandidateFiles()
        {
            foreach (string path in FromRunningClash())
            {
                yield return path;
            }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            yield return Path.Combine(appData, "clash_win", "cfw-settings.yaml");
        }

        private static IEnumerable<string> FromRunningClash()
        {
            Process[] processes = Process.GetProcessesByName("clash-win64");
            foreach (Process process in processes)
            {
                string exe = "";
                try { exe = process.MainModule.FileName; }
                catch { }
                if (string.IsNullOrWhiteSpace(exe)) continue;

                DirectoryInfo dir = new FileInfo(exe).Directory;
                for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, "data", "cfw-settings.yaml");
                    if (File.Exists(candidate)) yield return candidate;
                }
            }
        }
    }

    internal static class StartupControl
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ClashFloatStatus";

        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
            {
                object value = key == null ? null : key.GetValue(ValueName);
                return value != null && Convert.ToString(value).IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key == null) return;
                if (enabled)
                {
                    key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }
    }

    internal sealed class AppSettings
    {
        private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClashFloatStatus");
        private static readonly string FilePath = Path.Combine(Dir, "settings.ini");

        public bool Locked = true;
        public int X = int.MinValue;
        public int Y = int.MinValue;
        public bool HasPosition { get { return X != int.MinValue && Y != int.MinValue; } }

        public static AppSettings Load()
        {
            AppSettings settings = new AppSettings();
            if (!File.Exists(FilePath)) return settings;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                string[] parts = line.Split(new[] { '=' }, 2);
                if (parts.Length != 2) continue;
                if (parts[0] == "Locked") settings.Locked = parts[1] == "1";
                if (parts[0] == "X") int.TryParse(parts[1], out settings.X);
                if (parts[0] == "Y") int.TryParse(parts[1], out settings.Y);
            }
            return settings;
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllLines(FilePath, new[]
            {
                "Locked=" + (Locked ? "1" : "0"),
                "X=" + X,
                "Y=" + Y
            });
        }
    }

    internal static class NativeMethods
    {
        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;
        private const int ABM_GETTASKBARPOS = 5;
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public delegate void WinEventDelegate(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTAPI
        {
            public int x;
            public int y;

            public POINTAPI(int x, int y)
            {
                this.x = x;
                this.y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZEAPI
        {
            public int cx;
            public int cy;

            public SIZEAPI(int cx, int cy)
            {
                this.cx = cx;
                this.cy = cy;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("shell32.dll")]
        private static extern IntPtr SHAppBarMessage(int dwMessage, ref APPBARDATA pData);

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINTAPI pptDst, ref SIZEAPI psize, IntPtr hdcSrc, ref POINTAPI pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        public static void KeepTopMost(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        public static IntPtr SetForegroundHook(WinEventDelegate callback)
        {
            return SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, callback, 0, 0, WINEVENT_OUTOFCONTEXT);
        }

        public static bool IsForegroundFullscreen(IntPtr ownHandle)
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || hwnd == ownHandle || !IsWindowVisible(hwnd)) return false;

            uint processId;
            GetWindowThreadProcessId(hwnd, out processId);
            if (processId == (uint)Process.GetCurrentProcess().Id) return false;

            RECT rect;
            if (!GetWindowRect(hwnd, out rect)) return false;

            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;

            MONITORINFO info = new MONITORINFO();
            info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(monitor, ref info)) return false;

            int windowWidth = rect.right - rect.left;
            int windowHeight = rect.bottom - rect.top;
            int monitorWidth = info.rcMonitor.right - info.rcMonitor.left;
            int monitorHeight = info.rcMonitor.bottom - info.rcMonitor.top;

            const int tolerance = 2;
            bool coversWidth = rect.left <= info.rcMonitor.left + tolerance && rect.right >= info.rcMonitor.right - tolerance && windowWidth >= monitorWidth - tolerance;
            bool coversHeight = rect.top <= info.rcMonitor.top + tolerance && rect.bottom >= info.rcMonitor.bottom - tolerance && windowHeight >= monitorHeight - tolerance;
            return coversWidth && coversHeight;
        }

        public static void ApplyLayeredBitmap(IntPtr handle, Bitmap bitmap, Point location)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            IntPtr oldBitmap = SelectObject(memDc, hBitmap);

            try
            {
                POINTAPI topPos = new POINTAPI(location.X, location.Y);
                SIZEAPI size = new SIZEAPI(bitmap.Width, bitmap.Height);
                POINTAPI srcPos = new POINTAPI(0, 0);
                BLENDFUNCTION blend = new BLENDFUNCTION
                {
                    BlendOp = AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = AC_SRC_ALPHA
                };

                UpdateLayeredWindow(handle, screenDc, ref topPos, ref size, memDc, ref srcPos, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                SelectObject(memDc, oldBitmap);
                DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public static void RefreshInternetSettings()
        {
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }

        public static Rectangle GetTaskbarBounds()
        {
            APPBARDATA data = new APPBARDATA();
            data.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            IntPtr result = SHAppBarMessage(ABM_GETTASKBARPOS, ref data);
            if (result != IntPtr.Zero)
            {
                return Rectangle.FromLTRB(data.rc.left, data.rc.top, data.rc.right, data.rc.bottom);
            }

            Rectangle screen = Screen.PrimaryScreen.Bounds;
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            if (work.Bottom < screen.Bottom) return new Rectangle(screen.Left, work.Bottom, screen.Width, screen.Bottom - work.Bottom);
            if (work.Top > screen.Top) return new Rectangle(screen.Left, screen.Top, screen.Width, work.Top - screen.Top);
            if (work.Left > screen.Left) return new Rectangle(screen.Left, screen.Top, work.Left - screen.Left, screen.Height);
            return new Rectangle(work.Right, screen.Top, screen.Right - work.Right, screen.Height);
        }
    }
}
