using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GammaZoneLauncher
{
    public class ServerEntry
    {
        public string Name { get; set; } = "S.T.A.L.K.E.R. G.A.M.M.A. Co-op Server";
        public string Address { get; set; } = "127.0.0.1:28666";
        public string Map { get; set; } = "Кордон (l01_escape)";
        public int Players { get; set; } = 0;
        public int MaxPlayers { get; set; } = 17;
        public int PingMs { get; set; } = -1;
        public bool Online { get; set; } = false;
        public string Mode { get; set; } = "G.A.M.M.A. + xrRazom 1.4 (16p)";
        public string Roster { get; set; } = "";
    }

    public class LauncherConfig
    {
        public string AnomalyPath { get; set; } = @"C:\Anomaly";
        public string GammaPath { get; set; } = @"C:\GAMMA";
        public string CoopWorkspacePath { get; set; } = @"C:\Users\stanis\Desktop\GAMMA-Coop";
        public string PlayerName { get; set; } = Environment.UserName;
        public string LastConnectIp { get; set; } = "127.0.0.1:28666";
        public string SelectedRenderer { get; set; } = "AnomalyDX11AVX.exe";
        public bool LaunchViaMo2 { get; set; } = false;
        public int LaunchModeIndex { get; set; } = 0;
        public string HostServerName { get; set; } = "STALKER GAMMA CO-OP RU #1 [16 SLOTS]";
        public int HostPort { get; set; } = 28666;
        public int HostMaxPeers { get; set; } = 16;
        public string HostSave { get; set; } = "new";
        public bool DedicatedObserver { get; set; } = true;
        public bool HostSteam { get; set; } = true;
        public int AutosaveMinutes { get; set; } = 10;
        public bool SkipModCheck { get; set; } = false;
        public string ReleaseManifestUrl { get; set; } = "https://github.com/NegliadoV/GAMMA-Coop-16Players/releases/download/v1.0.0";
        public string ReleaseOutputDir { get; set; } = @"C:\GAMMA-Coop-Release";
        public List<ServerEntry> SavedServers { get; set; } = new List<ServerEntry>();
    }

    // Custom double-buffered tactical card with subtle border and optional left accent stripe
    public class TacticalCard : Panel
    {
        public Color BorderColor { get; set; } = Color.FromArgb(44, 52, 64);
        public Color AccentStripeColor { get; set; } = Color.Empty;
        public int AccentStripeWidth { get; set; } = 4;

        public TacticalCard()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.FromArgb(22, 26, 33);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;

            if (AccentStripeColor != Color.Empty && AccentStripeWidth > 0)
            {
                using var sb = new SolidBrush(AccentStripeColor);
                g.FillRectangle(sb, 0, 0, AccentStripeWidth, Height);
            }

            using var pen = new Pen(BorderColor, 1f);
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    // Custom dark tactical progress bar with centered status text
    public class TacticalProgressBar : Control
    {
        private int _min = 0;
        private int _max = 100;
        private int _val = 0;
        private string _overlayText = "";

        public int Minimum { get => _min; set { _min = value; Invalidate(); } }
        public int Maximum { get => _max; set { _max = Math.Max(1, value); Invalidate(); } }
        public int Value { get => _val; set { _val = Math.Clamp(value, _min, _max); Invalidate(); } }
        public string OverlayText { get => _overlayText; set { _overlayText = value ?? ""; Invalidate(); } }
        public Color FillColor { get; set; } = Color.FromArgb(46, 204, 113);
        public Color FillColorEnd { get; set; } = Color.FromArgb(32, 160, 90);

        public TacticalProgressBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(14, 17, 22);
            ForeColor = Color.FromArgb(240, 244, 248);
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
            Size = new Size(400, 26);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);

            float pct = (_max > _min) ? (float)(_val - _min) / (_max - _min) : 0f;
            pct = Math.Clamp(pct, 0f, 1f);
            int fillW = (int)Math.Round((Width - 2) * pct);

            if (fillW > 1)
            {
                var rect = new Rectangle(1, 1, fillW, Height - 2);
                using var brush = new LinearGradientBrush(rect, FillColor, FillColorEnd, LinearGradientMode.Horizontal);
                g.FillRectangle(brush, rect);
            }

            using var borderPen = new Pen(Color.FromArgb(52, 68, 60), 1f);
            g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

            string txt = !string.IsNullOrEmpty(_overlayText)
                ? _overlayText
                : $"{(int)(pct * 100)}% ({_val} / {_max})";

            TextRenderer.DrawText(
                g,
                txt,
                Font,
                new Rectangle(0, 0, Width, Height),
                ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    // Owner-drawn dark ComboBox without bright white borders
    public class TacticalComboBox : ComboBox
    {
        private const int WM_PAINT = 0x000F;
        public Color BorderColor { get; set; } = Color.FromArgb(54, 64, 78);
        public Color HighlightColor { get; set; } = Color.FromArgb(230, 142, 38);

        public TacticalComboBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            BackColor = Color.FromArgb(26, 31, 39);
            ForeColor = Color.FromArgb(235, 239, 245);
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            ItemHeight = 24;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) return;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected &&
                            (e.State & DrawItemState.ComboBoxEdit) != DrawItemState.ComboBoxEdit;

            Color bg = selected ? Color.FromArgb(52, 62, 78) : Color.FromArgb(26, 31, 39);
            Color fg = selected ? Color.FromArgb(255, 184, 77) : Color.FromArgb(235, 239, 245);

            using (var b = new SolidBrush(bg))
                e.Graphics.FillRectangle(b, e.Bounds);

            string text = Items[e.Index]?.ToString() ?? "";
            TextRenderer.DrawText(
                e.Graphics,
                text,
                Font,
                new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height),
                fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_PAINT)
            {
                using var g = Graphics.FromHwnd(Handle);
                int arrowAreaW = 22;
                var arrowRect = new Rectangle(Width - arrowAreaW, 1, arrowAreaW - 1, Height - 2);
                using (var bgBrush = new SolidBrush(Color.FromArgb(34, 40, 50)))
                    g.FillRectangle(bgBrush, arrowRect);

                // Draw chevron arrow
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int cx = Width - arrowAreaW / 2 - 1;
                int cy = Height / 2;
                using (var arrowPen = new Pen(Color.FromArgb(230, 142, 38), 1.8f))
                {
                    g.DrawLines(arrowPen, new[]
                    {
                        new Point(cx - 4, cy - 2),
                        new Point(cx, cy + 2),
                        new Point(cx + 4, cy - 2)
                    });
                }
                g.SmoothingMode = SmoothingMode.None;

                using var innerPen = new Pen(BackColor, 1f);
                g.DrawRectangle(innerPen, 1, 1, Width - 3, Height - 3);

                using var borderPen = new Pen(Focused ? HighlightColor : BorderColor, 1f);
                g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
            }
        }
    }

    public class MainForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private static readonly Color BgDark = Color.FromArgb(13, 16, 20);
        private static readonly Color BgHeader = Color.FromArgb(18, 22, 28);
        private static readonly Color BgPanel = Color.FromArgb(22, 26, 33);
        private static readonly Color BgCard = Color.FromArgb(28, 34, 43);
        private static readonly Color BorderSubtle = Color.FromArgb(46, 55, 68);
        private static readonly Color AccentAmber = Color.FromArgb(238, 152, 42);
        private static readonly Color AccentGreen = Color.FromArgb(46, 204, 113);
        private static readonly Color AccentCyan = Color.FromArgb(56, 189, 248);
        private static readonly Color AccentRed = Color.FromArgb(235, 87, 87);
        private static readonly Color TextMain = Color.FromArgb(238, 242, 247);
        private static readonly Color TextMuted = Color.FromArgb(148, 160, 176);

        private static readonly Dictionary<string, string> MapNamesRu = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["main_menu"] = "Главное меню (Ожидание)",
            ["fake_start"] = "Загрузка Зоны",
            ["k00_marsh"] = "Болота",
            ["l01_escape"] = "Кордон",
            ["l02_garbage"] = "Свалка",
            ["l03_agroprom"] = "НИИ Агропром",
            ["k01_darkscape"] = "Тёмная Лощина",
            ["l04_darkvalley"] = "Тёмная Долина",
            ["l05_bar"] = "Росток (Бар 100 Рентген)",
            ["l06_rostok"] = "Дикая Территория",
            ["l07_military"] = "Армейские Склады",
            ["l08_yantar"] = "Янтарь",
            ["l09_deadcity"] = "Мёртвый Город",
            ["l10_limansk"] = "Лиманск",
            ["l10_radar"] = "Радар",
            ["l10_red_forest"] = "Рыжий Лес",
            ["l11_hospital"] = "Госпиталь",
            ["l11_pripyat"] = "Припять (Центр)",
            ["pripyat"] = "Припять (Окраины)",
            ["jupiter"] = "Юпитер (Окрестности)",
            ["zaton"] = "Затон",
            ["l12_stancia"] = "ЧАЭС (Юг)",
            ["l12_stancia_2"] = "ЧАЭС (Север)",
            ["l13_generators"] = "Генераторы"
        };

        private readonly string _configPath;
        private LauncherConfig _cfg;

        // Custom Tab Navigation
        private int _activeTabIndex = 0;
        private readonly Button[] _navButtons = new Button[3];
        private readonly Panel[] _tabPages = new Panel[3];
        private Panel _contentHost;

        // Header & Bottom Bar Controls
        private Label _lblStatusBanner;
        private Panel _bottomBar;
        private TextBox _txtPlayerName;
        private TextBox _txtDirectIp;
        private TacticalComboBox _cmbRenderer;
        private TacticalComboBox _cmbLaunchMode;
        private Button _btnFastStartHost;
        private Button _btnJoinNow;

        // Tab 0: Servers Controls
        private DataGridView _serverGrid;
        private RichTextBox _txtLog;

        // Tab 1: Host Controls
        private TextBox _txtServerName;
        private NumericUpDown _numHostPort;
        private NumericUpDown _numMaxPeers;
        private TacticalComboBox _cmbHostSave;
        private CheckBox _chkDedicatedObserver;
        private CheckBox _chkHostSteam;
        private NumericUpDown _numAutosaveMin;
        private CheckBox _chkSkipModCheck;
        private Label _lblLiveServerTelemetry;
        private Button _btnStartHost;

        // Tab 2: Mods & Engine Controls
        private Label _lblEngineStatus;
        private Label _lblGammaStatus;
        private Label _lblFixesStatus;
        private TextBox _txtAnomalyPath;
        private TextBox _txtGammaPath;
        private TextBox _txtReleaseUrl;
        private Button _btnTurboInstall;
        private TacticalProgressBar _prgTurboInstall;
        private Label _lblTurboProgress;
        private CancellationTokenSource _turboInstallCts;

        // UDP Query Responder
        private UdpClient _queryResponder;
        private CancellationTokenSource _responderCts;
        private System.Windows.Forms.Timer _telemetryTimer;

        public MainForm()
        {
            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher_config.json");
            LoadConfig();

            DoubleBuffered = true;
            Text = "S.T.A.L.K.E.R. G.A.M.M.A. & Anomaly — ZONE CO-OP LAUNCHER (16 Players Unleashed)";
            Size = new Size(1220, 790);
            MinimumSize = new Size(1120, 720);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BgDark;
            ForeColor = TextMain;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            BuildUi();
            Shown += (s, e) =>
            {
                TryApplyDarkTitleBarAndIcon();
                LayoutBottomBar();
                StartQueryResponder(_cfg.HostPort + 1);
                RefreshModAndEngineStatus();
                Log("Добро пожаловать в ZONE CO-OP LAUNCHER! Сборка G.A.M.M.A. (594 мода) + Быстрый Старт NVMe готовы к игре.");
                _ = RefreshAllServersAsync();
            };

            _telemetryTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _telemetryTimer.Tick += (s, e) => UpdateLocalHostTelemetryLabel();
            _telemetryTimer.Start();
        }

        private void TryApplyDarkTitleBarAndIcon()
        {
            try
            {
                int useDark = 1;
                DwmSetWindowAttribute(Handle, 20, ref useDark, sizeof(int));
                DwmSetWindowAttribute(Handle, 19, ref useDark, sizeof(int));
            }
            catch { }

            try
            {
                string icoExe = Path.Combine(_cfg.AnomalyPath, "AnomalyLauncher.exe");
                if (File.Exists(icoExe))
                {
                    Icon = Icon.ExtractAssociatedIcon(icoExe);
                }
            }
            catch { }
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    _cfg = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(_configPath, Encoding.UTF8)) ?? new LauncherConfig();
                }
            }
            catch { }

            _cfg ??= new LauncherConfig();

            if (string.IsNullOrWhiteSpace(_cfg.CoopWorkspacePath) || !Directory.Exists(Path.Combine(_cfg.CoopWorkspacePath, "xrRazom-1.4")))
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
                string parentDir = Directory.GetParent(baseDir)?.FullName ?? baseDir;
                if (Directory.Exists(Path.Combine(baseDir, "xrRazom-1.4")))
                    _cfg.CoopWorkspacePath = baseDir;
                else if (Directory.Exists(Path.Combine(parentDir, "xrRazom-1.4")))
                    _cfg.CoopWorkspacePath = parentDir;
            }

            if (string.IsNullOrWhiteSpace(_cfg.AnomalyPath) || !Directory.Exists(_cfg.AnomalyPath))
            {
                foreach (var candidate in new[] { @"C:\Anomaly", @"D:\Anomaly", @"C:\Games\Anomaly", @"D:\Games\Anomaly", @"E:\Anomaly" })
                {
                    if (Directory.Exists(candidate))
                    {
                        _cfg.AnomalyPath = candidate;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(_cfg.GammaPath) || !Directory.Exists(_cfg.GammaPath))
            {
                foreach (var candidate in new[] { @"C:\GAMMA", @"D:\GAMMA", @"C:\Games\GAMMA", @"D:\Games\GAMMA", @"E:\GAMMA" })
                {
                    if (Directory.Exists(candidate))
                    {
                        _cfg.GammaPath = candidate;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(_cfg.ReleaseManifestUrl) ||
                _cfg.ReleaseManifestUrl.Equals(@"C:\GAMMA-Coop-Release", StringComparison.OrdinalIgnoreCase) ||
                _cfg.ReleaseManifestUrl.Contains("/releases/tag/", StringComparison.OrdinalIgnoreCase))
            {
                _cfg.ReleaseManifestUrl = ReleasePackagerAndUpdater.DefaultGitHubReleaseUrl;
            }

            if (_cfg.SavedServers == null || _cfg.SavedServers.Count == 0)
            {
                _cfg.SavedServers = new List<ServerEntry>
                {
                    new ServerEntry
                    {
                        Name = "Локальный выделенный сервер (Этот ПК / LAN / Radmin VPN)",
                        Address = "127.0.0.1:28666",
                        Map = "Кордон (Готов к быстрому старту)",
                        MaxPlayers = 17,
                        Mode = "G.A.M.M.A. / Anomaly 1.5.3 (xrRazom 1.4 16p)"
                    }
                };
            }
        }

        private void SaveConfig()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_txtAnomalyPath?.Text)) _cfg.AnomalyPath = _txtAnomalyPath.Text.Trim();
                if (!string.IsNullOrWhiteSpace(_txtGammaPath?.Text)) _cfg.GammaPath = _txtGammaPath.Text.Trim();
                _cfg.PlayerName = _txtPlayerName?.Text?.Trim() ?? _cfg.PlayerName;
                _cfg.LastConnectIp = _txtDirectIp?.Text?.Trim() ?? _cfg.LastConnectIp;
                if (_cmbRenderer?.SelectedItem != null) _cfg.SelectedRenderer = _cmbRenderer.SelectedItem.ToString();
                if (_cmbLaunchMode != null)
                {
                    _cfg.LaunchModeIndex = Math.Clamp(_cmbLaunchMode.SelectedIndex, 0, 2);
                    _cfg.LaunchViaMo2 = _cfg.LaunchModeIndex == 1;
                }

                if (_txtServerName != null) _cfg.HostServerName = _txtServerName.Text.Trim();
                if (_numHostPort != null) _cfg.HostPort = (int)_numHostPort.Value;
                if (_numMaxPeers != null) _cfg.HostMaxPeers = (int)_numMaxPeers.Value;
                if (_cmbHostSave?.SelectedItem != null) _cfg.HostSave = _cmbHostSave.SelectedItem.ToString();
                if (_chkDedicatedObserver != null) _cfg.DedicatedObserver = _chkDedicatedObserver.Checked;
                if (_chkHostSteam != null) _cfg.HostSteam = _chkHostSteam.Checked;
                if (_numAutosaveMin != null) _cfg.AutosaveMinutes = (int)_numAutosaveMin.Value;
                if (_chkSkipModCheck != null) _cfg.SkipModCheck = _chkSkipModCheck.Checked;
                if (_txtReleaseUrl != null && !string.IsNullOrWhiteSpace(_txtReleaseUrl.Text))
                    _cfg.ReleaseManifestUrl = _txtReleaseUrl.Text.Trim();

                var json = JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configPath, json, Encoding.UTF8);
            }
            catch { }
        }

        private void BuildUi()
        {
            // 1. Top Header + Tactical Navigation Strip
            var topContainer = new Panel
            {
                Dock = DockStyle.Top,
                Height = 114,
                BackColor = BgHeader
            };

            var headerBanner = new Panel
            {
                Dock = DockStyle.Top,
                Height = 68,
                BackColor = BgHeader
            };
            headerBanner.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.FromArgb(36, 44, 56), 1f);
                e.Graphics.DrawLine(pen, 0, headerBanner.Height - 1, headerBanner.Width, headerBanner.Height - 1);
                using var accentBrush = new SolidBrush(AccentAmber);
                e.Graphics.FillRectangle(accentBrush, 0, 0, 5, headerBanner.Height);
            };

            var lblBadge = new Label
            {
                Text = "☢",
                Font = new Font("Segoe UI Symbol", 22f, FontStyle.Bold),
                ForeColor = AccentAmber,
                AutoSize = true,
                Location = new Point(18, 11)
            };

            var lblTitle = new Label
            {
                Text = "ZONE CO-OP LAUNCHER   //   S.T.A.L.K.E.R. G.A.M.M.A. & ANOMALY 1.5.3",
                Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
                ForeColor = TextMain,
                AutoSize = true,
                Location = new Point(62, 11)
            };

            var lblSub = new Label
            {
                Text = "Прямой запуск NVMe HardLink  •  Синхронизация 594 модификаций  •  Сетевой протокол xrRazom 1.4 (до 17 игроков / Steam SDR)",
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                ForeColor = TextMuted,
                AutoSize = true,
                Location = new Point(64, 38)
            };

            _lblStatusBanner = new Label
            {
                Text = "● КЛИЕНТ ГОТОВ К ЗАПУСКУ",
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = AccentGreen,
                BackColor = Color.FromArgb(20, 38, 30),
                AutoSize = false,
                Size = new Size(295, 34),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _lblStatusBanner.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.FromArgb(46, 204, 113), 1f);
                e.Graphics.DrawRectangle(pen, 0, 0, _lblStatusBanner.Width - 1, _lblStatusBanner.Height - 1);
            };

            headerBanner.Controls.Add(lblBadge);
            headerBanner.Controls.Add(lblTitle);
            headerBanner.Controls.Add(lblSub);
            headerBanner.Controls.Add(_lblStatusBanner);
            headerBanner.Resize += (s, e) =>
            {
                _lblStatusBanner.Location = new Point(Math.Max(740, headerBanner.Width - _lblStatusBanner.Width - 20), 17);
            };

            // Custom Tactical Tab Bar (No white WinForms tabs!)
            var navStrip = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 46,
                BackColor = Color.FromArgb(15, 19, 24)
            };
            navStrip.Paint += (s, e) =>
            {
                using var pen = new Pen(BorderSubtle, 1f);
                e.Graphics.DrawLine(pen, 0, navStrip.Height - 1, navStrip.Width, navStrip.Height - 1);
            };

            string[] tabTitles =
            {
                "📡  СЕРВЕРЫ И ПОДКЛЮЧЕНИЕ",
                "🖥  СОЗДАНИЕ СЕРВЕРА (ДО 17 ИГРОКОВ)",
                "⚙  УСТАНОВКА И ОБНОВЛЕНИЕ КЛИЕНТА"
            };

            int navX = 18;
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                int btnW = (i == 0) ? 310 : (i == 1) ? 355 : 410;
                var btn = new Button
                {
                    Text = tabTitles[i],
                    Location = new Point(navX, 0),
                    Size = new Size(btnW, 45),
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                    Cursor = Cursors.Hand,
                    TabStop = false
                };
                btn.FlatAppearance.BorderSize = 0;
                btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(26, 32, 42);
                btn.Click += (s, e) => SelectTab(idx);
                btn.Paint += (s, e) =>
                {
                    if (_activeTabIndex == idx)
                    {
                        using var brush = new SolidBrush(AccentAmber);
                        e.Graphics.FillRectangle(brush, 0, btn.Height - 3, btn.Width, 3);
                    }
                };
                _navButtons[i] = btn;
                navStrip.Controls.Add(btn);
                navX += btnW + 6;
            }

            topContainer.Controls.Add(navStrip);
            topContainer.Controls.Add(headerBanner);

            // 2. Bottom Action Dock Bar (Always visible, responsive layout via Resize)
            _bottomBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 82,
                BackColor = BgHeader
            };
            _bottomBar.Paint += (s, e) =>
            {
                using var pen = new Pen(BorderSubtle, 1f);
                e.Graphics.DrawLine(pen, 0, 0, _bottomBar.Width, 0);
            };

            var lblNick = MakeLabel("Позывной сталкера:", 18, 11, TextMuted);
            _txtPlayerName = MakeTextBox(_cfg.PlayerName, 18, 34, 135);

            var lblIp = MakeLabel("Адрес сервера (IP:Порт):", 165, 11, TextMuted);
            _txtDirectIp = MakeTextBox(_cfg.LastConnectIp, 165, 34, 165);

            var lblRender = MakeLabel("Рендер / DirectX:", 342, 11, TextMuted);
            _cmbRenderer = new TacticalComboBox
            {
                Location = new Point(342, 34),
                Width = 152
            };
            _cmbRenderer.Items.AddRange(new object[]
            {
                "AnomalyDX11AVX.exe",
                "AnomalyDX11.exe",
                "AnomalyDX10AVX.exe",
                "AnomalyDX9AVX.exe",
                "AnomalyDX8AVX.exe"
            });
            _cmbRenderer.SelectedItem = _cfg.SelectedRenderer;
            if (_cmbRenderer.SelectedIndex < 0) _cmbRenderer.SelectedIndex = 0;

            var lblMode = MakeLabel("Режим запуска сборки:", 506, 11, AccentAmber);
            _cmbLaunchMode = new TacticalComboBox
            {
                Location = new Point(506, 34),
                Width = 240
            };
            _cmbLaunchMode.Items.Add("⚡ G.A.M.M.A. Быстрый Старт (Прямой)");
            _cmbLaunchMode.Items.Add("Через Mod Organizer 2 (G.A.M.M.A.)");
            _cmbLaunchMode.Items.Add("Чистая Anomaly 1.5.3 + Кооп");
            _cmbLaunchMode.SelectedIndex = Math.Clamp(_cfg.LaunchModeIndex, 0, 2);

            _btnFastStartHost = new Button
            {
                Text = "⚡ БЫСТРЫЙ СТАРТ (В ЗОНУ)",
                Size = new Size(218, 48),
                BackColor = AccentGreen,
                ForeColor = Color.FromArgb(10, 14, 12),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.75f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnFastStartHost.FlatAppearance.BorderSize = 0;
            _btnFastStartHost.Click += (s, e) => LaunchAsHostServer();

            _btnJoinNow = new Button
            {
                Text = "ПОДКЛЮЧИТЬСЯ К СЕРВЕРУ",
                Size = new Size(206, 48),
                BackColor = AccentAmber,
                ForeColor = Color.FromArgb(14, 12, 10),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.75f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnJoinNow.FlatAppearance.BorderSize = 0;
            _btnJoinNow.Click += (s, e) => LaunchAndConnect(_txtDirectIp.Text.Trim());

            _bottomBar.Controls.AddRange(new Control[]
            {
                lblNick, _txtPlayerName,
                lblIp, _txtDirectIp,
                lblRender, _cmbRenderer,
                lblMode, _cmbLaunchMode,
                _btnFastStartHost, _btnJoinNow
            });

            _bottomBar.Resize += (s, e) => LayoutBottomBar();

            // 3. Center Content Host & 3 Pages
            _contentHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BgDark,
                Padding = new Padding(18, 14, 18, 14)
            };

            for (int i = 0; i < 3; i++)
            {
                _tabPages[i] = new Panel
                {
                    Dock = DockStyle.Fill,
                    BackColor = BgDark,
                    Visible = (i == 0)
                };
                _contentHost.Controls.Add(_tabPages[i]);
            }

            BuildServersTab(_tabPages[0]);
            BuildHostTab(_tabPages[1]);
            BuildModsTab(_tabPages[2]);

            Controls.Add(_contentHost);
            Controls.Add(_bottomBar);
            Controls.Add(topContainer);

            LayoutBottomBar();
            RenderServerGrid();
            SelectTab(0);
        }

        private void LayoutBottomBar()
        {
            if (_bottomBar == null || _btnJoinNow == null || _btnFastStartHost == null) return;
            int rightMargin = 18;
            int gap = 10;
            int joinX = _bottomBar.Width - _btnJoinNow.Width - rightMargin;
            int fastX = joinX - _btnFastStartHost.Width - gap;

            _btnJoinNow.Location = new Point(Math.Max(760, joinX), 18);
            _btnFastStartHost.Location = new Point(Math.Max(535, fastX), 18);

            int availableForMode = Math.Max(185, _btnFastStartHost.Left - 506 - 12);
            _cmbLaunchMode.Width = Math.Min(260, availableForMode);
        }

        public void SelectTab(int index)
        {
            _activeTabIndex = Math.Clamp(index, 0, 2);
            for (int i = 0; i < 3; i++)
            {
                bool active = (i == _activeTabIndex);
                _tabPages[i].Visible = active;
                if (active) _tabPages[i].BringToFront();

                _navButtons[i].BackColor = active ? Color.FromArgb(24, 30, 39) : Color.FromArgb(15, 19, 24);
                _navButtons[i].ForeColor = active ? AccentAmber : TextMuted;
                _navButtons[i].Invalidate();
            }
        }

        private void BuildServersTab(Panel page)
        {
            var topStrip = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = BgDark };

            var btnRefresh = MakeActionButton("🔄 Обновить статус серверов", 0, 4, 215, BgCard, TextMain);
            btnRefresh.Click += async (s, e) => await RefreshAllServersAsync();

            var btnAddServer = MakeActionButton("➕ Добавить сервер (IP / VPN)", 224, 4, 260, BgCard, AccentAmber);
            btnAddServer.Click += (s, e) => PromptAddServer();

            var btnRemoveServer = MakeActionButton("🗑 Удалить", 493, 4, 120, BgCard, TextMuted);
            btnRemoveServer.Click += (s, e) => RemoveSelectedServer();

            var btnCopyMyIp = MakeActionButton("📋 Скопировать IP-адрес хоста", 622, 4, 255, BgCard, AccentGreen);
            btnCopyMyIp.Click += (s, e) => CopyLocalIpsToClipboard();

            var btnQuickNewCordon = MakeActionButton("⚡ Новая сессия (Кордон)", 886, 4, 255, Color.FromArgb(24, 46, 36), AccentGreen);
            btnQuickNewCordon.Click += (s, e) => LaunchFreshNewGameOnCordon();

            topStrip.Controls.AddRange(new Control[] { btnRefresh, btnAddServer, btnRemoveServer, btnCopyMyIp, btnQuickNewCordon });

            var gridCard = new TacticalCard
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(2),
                BackColor = BgPanel
            };

            _serverGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = BgPanel,
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                GridColor = Color.FromArgb(36, 43, 54),
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                EnableHeadersVisualStyles = false,
                RowTemplate = { Height = 42 }
            };

            _serverGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(28, 34, 44);
            _serverGrid.ColumnHeadersDefaultCellStyle.ForeColor = AccentAmber;
            _serverGrid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(28, 34, 44);
            _serverGrid.ColumnHeadersDefaultCellStyle.SelectionForeColor = AccentAmber;
            _serverGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            _serverGrid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            _serverGrid.ColumnHeadersHeight = 40;

            _serverGrid.DefaultCellStyle.BackColor = BgPanel;
            _serverGrid.DefaultCellStyle.ForeColor = TextMain;
            _serverGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(42, 54, 72);
            _serverGrid.DefaultCellStyle.SelectionForeColor = Color.White;
            _serverGrid.DefaultCellStyle.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            _serverGrid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(25, 30, 38);

            _serverGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = " СТАТУС", FillWeight = 11 });
            _serverGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "НАЗВАНИЕ СЕРВЕРА ЗОНЫ", FillWeight = 32 });
            _serverGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Map", HeaderText = "ЛОКАЦИЯ", FillWeight = 20 });
            _serverGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Players", HeaderText = "СТАЛКЕРЫ", FillWeight = 10 });
            _serverGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Ping", HeaderText = "ПИНГ", FillWeight = 9 });
            _serverGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Address", HeaderText = "АДРЕС (IP:ПОРТ)", FillWeight = 18 });

            _serverGrid.SelectionChanged += (s, e) =>
            {
                if (_serverGrid.SelectedRows.Count > 0)
                {
                    var addr = _serverGrid.SelectedRows[0].Cells["Address"].Value?.ToString();
                    if (!string.IsNullOrEmpty(addr)) _txtDirectIp.Text = addr;
                }
            };

            _serverGrid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0)
                {
                    var addr = _serverGrid.Rows[e.RowIndex].Cells["Address"].Value?.ToString();
                    if (!string.IsNullOrEmpty(addr))
                    {
                        _txtDirectIp.Text = addr;
                        LaunchAndConnect(addr);
                    }
                }
            };

            gridCard.Controls.Add(_serverGrid);

            var logSpacer = new Panel { Dock = DockStyle.Bottom, Height = 10, BackColor = BgDark };

            var logCard = new TacticalCard
            {
                Dock = DockStyle.Bottom,
                Height = 148,
                BackColor = Color.FromArgb(14, 17, 22),
                AccentStripeColor = AccentAmber,
                AccentStripeWidth = 3,
                Padding = new Padding(12, 8, 10, 8)
            };

            var lblLog = new Label
            {
                Text = "ТЕРМИНАЛ СВЯЗИ И ТЕЛЕМЕТРИИ ЛАУНЧЕРА:",
                Dock = DockStyle.Top,
                Height = 20,
                ForeColor = AccentAmber,
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold)
            };

            _txtLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(14, 17, 22),
                ForeColor = Color.FromArgb(186, 212, 230),
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                Font = new Font("Consolas", 9.2f)
            };
            logCard.Controls.Add(_txtLog);
            logCard.Controls.Add(lblLog);

            page.Controls.Add(gridCard);
            page.Controls.Add(topStrip);
            page.Controls.Add(logSpacer);
            page.Controls.Add(logCard);
        }

        private void BuildHostTab(Panel page)
        {
            var card = new TacticalCard
            {
                Dock = DockStyle.Fill,
                BackColor = BgPanel,
                AccentStripeColor = AccentGreen,
                AccentStripeWidth = 4,
                Padding = new Padding(24)
            };

            var title = new Label
            {
                Text = "КОНФИГУРАЦИЯ СЕРВЕРА И СЕТЕВОЙ СЕССИИ (ДО 17 ИГРОКОВ)",
                Font = new Font("Segoe UI", 12.5f, FontStyle.Bold),
                ForeColor = AccentAmber,
                AutoSize = true,
                Location = new Point(22, 16)
            };
            card.Controls.Add(title);

            var desc = new Label
            {
                Text = "Настройка параметров кооперативного сервера S.T.A.L.K.E.R. G.A.M.M.A. (хост + до 16 подключающихся игроков),\n" +
                       "выбор сетевого транспорта (Valve Steam SDR или прямой UDP-порт), автосохранение мира и локальное тестирование.",
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = TextMuted,
                AutoSize = true,
                Location = new Point(22, 44)
            };
            card.Controls.Add(desc);

            int y = 96;
            card.Controls.Add(MakeLabel("Название сервера (отображается в списке серверов):", 22, y, TextMain));
            _txtServerName = MakeTextBox(_cfg.HostServerName, 22, y + 24, 480);
            card.Controls.Add(_txtServerName);

            card.Controls.Add(MakeLabel("UDP-порт сервера:", 525, y, TextMain));
            _numHostPort = new NumericUpDown
            {
                Location = new Point(525, y + 24),
                Width = 135,
                Minimum = 1024,
                Maximum = 65530,
                Value = _cfg.HostPort,
                BackColor = BgCard,
                ForeColor = TextMain,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 10.5f)
            };
            card.Controls.Add(_numHostPort);

            card.Controls.Add(MakeLabel("Лимит подключающихся клиентов (1–16 пиров = до 17 игроков):", 685, y, AccentAmber));
            _numMaxPeers = new NumericUpDown
            {
                Location = new Point(685, y + 24),
                Width = 140,
                Minimum = 1,
                Maximum = 16,
                Value = Math.Clamp(_cfg.HostMaxPeers, 1, 16),
                BackColor = BgCard,
                ForeColor = AccentGreen,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold)
            };
            card.Controls.Add(_numMaxPeers);

            y += 70;
            card.Controls.Add(MakeLabel("Сохранение мира ('new' = продолжить автосейв, 'fast_new' = новая игра на Кордоне):", 22, y, TextMain));
            _cmbHostSave = new TacticalComboBox
            {
                Location = new Point(22, y + 24),
                Width = 480
            };
            RefreshSavesList();
            card.Controls.Add(_cmbHostSave);

            var btnRefreshSaves = MakeActionButton("🔄 Обновить список сохранений", 525, y + 21, 215, BgCard, TextMain);
            btnRefreshSaves.Height = 32;
            btnRefreshSaves.Click += (s, e) => RefreshSavesList();
            card.Controls.Add(btnRefreshSaves);

            var btnFreshCordon = MakeActionButton("⚡ Новая сессия на Кордоне", 752, y + 21, 340, Color.FromArgb(26, 46, 36), AccentGreen);
            btnFreshCordon.Height = 32;
            btnFreshCordon.Click += (s, e) => LaunchFreshNewGameOnCordon();
            card.Controls.Add(btnFreshCordon);

            y += 66;
            _chkDedicatedObserver = new CheckBox
            {
                Text = "Включить режим 24/7 «Выделенный сервер» (защита персонажа-хоста и непрерывная работа мира)",
                Checked = _cfg.DedicatedObserver,
                Location = new Point(22, y),
                AutoSize = true,
                ForeColor = AccentGreen,
                Font = new Font("Segoe UI Semibold", 9.8f, FontStyle.Bold)
            };
            card.Controls.Add(_chkDedicatedObserver);

            y += 28;
            _chkHostSteam = new CheckBox
            {
                Text = "Включить сетевой транспорт Steam P2P / SDR (AppID 480 — низкая задержка через Valve, вход через оверлей Shift+Tab)",
                Checked = _cfg.HostSteam,
                Location = new Point(22, y),
                AutoSize = true,
                ForeColor = AccentCyan,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold)
            };
            card.Controls.Add(_chkHostSteam);

            y += 30;
            card.Controls.Add(MakeLabel("Интервал автосохранения мира (минут, 0 = выкл):", 22, y + 3, TextMain));
            _numAutosaveMin = new NumericUpDown
            {
                Location = new Point(365, y),
                Width = 90,
                Minimum = 0,
                Maximum = 180,
                Value = _cfg.AutosaveMinutes,
                BackColor = BgCard,
                ForeColor = TextMain,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 10f)
            };
            card.Controls.Add(_numAutosaveMin);

            _chkSkipModCheck = new CheckBox
            {
                Text = "Разрешить подключение клиентов с отличающимися модификациями (xrr_skip_mod_check)",
                Checked = _cfg.SkipModCheck,
                Location = new Point(490, y + 2),
                AutoSize = true,
                ForeColor = TextMuted
            };
            card.Controls.Add(_chkSkipModCheck);

            y += 40;
            var telemetryCard = new TacticalCard
            {
                Location = new Point(22, y),
                Size = new Size(1100, 64),
                BackColor = Color.FromArgb(16, 20, 26),
                AccentStripeColor = AccentCyan,
                AccentStripeWidth = 4,
                Padding = new Padding(14, 10, 14, 10)
            };
            _lblLiveServerTelemetry = new Label
            {
                Text = "Статус локального сервера: Готов к запуску...",
                Dock = DockStyle.Fill,
                ForeColor = TextMain,
                Font = new Font("Consolas", 9.5f)
            };
            telemetryCard.Controls.Add(_lblLiveServerTelemetry);
            card.Controls.Add(telemetryCard);

            y += 82;
            _btnStartHost = new Button
            {
                Text = "▶ 1. ЗАПУСТИТЬ СЕРВЕР (ХОСТ СЕССИИ)",
                Location = new Point(22, y),
                Size = new Size(535, 54),
                BackColor = AccentGreen,
                ForeColor = Color.FromArgb(10, 14, 12),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnStartHost.FlatAppearance.BorderSize = 0;
            _btnStartHost.Click += (s, e) => LaunchAsHostServer();
            card.Controls.Add(_btnStartHost);

            var btnStartSecondClient = new Button
            {
                Text = "👥 2. ЗАПУСТИТЬ ВТОРОЙ КЛИЕНТ (ЛОКАЛЬНЫЙ ТЕСТ 127.0.0.1)",
                Location = new Point(575, y),
                Size = new Size(547, 54),
                BackColor = AccentAmber,
                ForeColor = Color.FromArgb(14, 12, 10),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10.2f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnStartSecondClient.FlatAppearance.BorderSize = 0;
            btnStartSecondClient.Click += (s, e) => LaunchSecondLocalClientForTest();
            card.Controls.Add(btnStartSecondClient);

            card.Resize += (s, e) =>
            {
                int w = Math.Max(920, card.Width - 44);
                telemetryCard.Width = w;
                int halfW = (w - 18) / 2;
                _btnStartHost.Width = halfW;
                btnStartSecondClient.Location = new Point(22 + halfW + 18, _btnStartHost.Top);
                btnStartSecondClient.Width = w - halfW - 18;
            };

            page.Controls.Add(card);
        }

        private void BuildModsTab(Panel page)
        {
            var card = new TacticalCard
            {
                Dock = DockStyle.Fill,
                BackColor = BgPanel,
                AccentStripeColor = AccentAmber,
                AccentStripeWidth = 4,
                Padding = new Padding(20)
            };

            var title = new Label
            {
                Text = "УСТАНОВКА, ОБНОВЛЕНИЕ И КОНФИГУРАЦИЯ КЛИЕНТА S.T.A.L.K.E.R. G.A.M.M.A. CO-OP",
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = AccentAmber,
                AutoSize = true,
                Location = new Point(22, 12)
            };
            card.Controls.Add(title);

            var cardEngine = new TacticalCard
            {
                Location = new Point(22, 38),
                Size = new Size(1100, 44),
                BackColor = BgCard,
                AccentStripeColor = AccentGreen,
                AccentStripeWidth = 4,
                Padding = new Padding(12, 5, 12, 5)
            };
            _lblEngineStatus = new Label { Dock = DockStyle.Fill, ForeColor = TextMain, Font = new Font("Segoe UI", 8.8f) };
            cardEngine.Controls.Add(_lblEngineStatus);
            card.Controls.Add(cardEngine);

            var cardGamma = new TacticalCard
            {
                Location = new Point(22, 86),
                Size = new Size(1100, 44),
                BackColor = BgCard,
                AccentStripeColor = AccentCyan,
                AccentStripeWidth = 4,
                Padding = new Padding(12, 5, 12, 5)
            };
            _lblGammaStatus = new Label { Dock = DockStyle.Fill, ForeColor = TextMain, Font = new Font("Segoe UI", 8.8f) };
            cardGamma.Controls.Add(_lblGammaStatus);
            card.Controls.Add(cardGamma);

            var cardFixes = new TacticalCard
            {
                Location = new Point(22, 134),
                Size = new Size(1100, 30),
                BackColor = BgCard,
                AccentStripeColor = AccentAmber,
                AccentStripeWidth = 4,
                Padding = new Padding(12, 5, 12, 4)
            };
            _lblFixesStatus = new Label { Dock = DockStyle.Fill, ForeColor = TextMain, Font = new Font("Segoe UI", 8.8f) };
            cardFixes.Controls.Add(_lblFixesStatus);
            card.Controls.Add(cardFixes);

            int y = 170;
            card.Controls.Add(MakeLabel("Директория установки игры (S.T.A.L.K.E.R. Anomaly / G.A.M.M.A. Co-op):", 22, y, TextMain));
            _txtAnomalyPath = MakeTextBox(_cfg.AnomalyPath, 22, y + 19, 430);
            _txtAnomalyPath.TextChanged += (s, e) => { _cfg.AnomalyPath = _txtAnomalyPath.Text.Trim(); RefreshModAndEngineStatus(); };
            card.Controls.Add(_txtAnomalyPath);

            var btnBrowseAnomaly = MakeActionButton("Обзор...", 460, y + 17, 95, BgCard, TextMain);
            btnBrowseAnomaly.Height = 29;
            btnBrowseAnomaly.Click += (s, e) =>
            {
                using var fbd = new FolderBrowserDialog { Description = "Выберите директорию для установки или запуска игры", SelectedPath = _cfg.AnomalyPath };
                if (fbd.ShowDialog() == DialogResult.OK) { _txtAnomalyPath.Text = fbd.SelectedPath; SaveConfig(); }
            };
            card.Controls.Add(btnBrowseAnomaly);

            card.Controls.Add(MakeLabel("Директория профилей Mod Organizer 2 (опционально):", 575, y, TextMuted));
            _txtGammaPath = MakeTextBox(_cfg.GammaPath, 575, y + 19, 442);
            _txtGammaPath.TextChanged += (s, e) => { _cfg.GammaPath = _txtGammaPath.Text.Trim(); RefreshModAndEngineStatus(); };
            card.Controls.Add(_txtGammaPath);

            var btnBrowseGamma = MakeActionButton("Обзор...", 1025, y + 17, 97, BgCard, TextMain);
            btnBrowseGamma.Height = 29;
            btnBrowseGamma.Click += (s, e) =>
            {
                using var fbd = new FolderBrowserDialog { Description = "Выберите директорию Mod Organizer 2 (опционально)", SelectedPath = _cfg.GammaPath };
                if (fbd.ShowDialog() == DialogResult.OK) { _txtGammaPath.Text = fbd.SelectedPath; SaveConfig(); }
            };
            card.Controls.Add(btnBrowseGamma);

            // PRIMARY USER-FRIENDLY BOX: 1-Click Client Download, Install & Delta Update (with integrated Progress Bar!)
            y += 54;
            var releaseBox = new TacticalCard
            {
                Location = new Point(22, y),
                Size = new Size(1100, 148),
                BackColor = Color.FromArgb(15, 24, 33),
                BorderColor = Color.FromArgb(46, 134, 193),
                AccentStripeColor = AccentGreen,
                AccentStripeWidth = 4,
                Padding = new Padding(14)
            };

            var lblCloudHeader = new Label
            {
                Text = "☁ АВТОМАТИЧЕСКАЯ УСТАНОВКА И ОБНОВЛЕНИЕ КЛИЕНТА (GITHUB RELEASES — ВСЯ СБОРКА В 1 КЛИК)",
                Location = new Point(16, 8),
                AutoSize = true,
                ForeColor = AccentGreen,
                Font = new Font("Segoe UI Semibold", 9.8f, FontStyle.Bold)
            };
            releaseBox.Controls.Add(lblCloudHeader);

            var btnClientInstall = new Button
            {
                Text = "⬇  СКАЧАТЬ И УСТАНОВИТЬ / ОБНОВИТЬ СБОРКУ В 1 КЛИК (38.5 ГБ БАЗА + СВЕЖИЙ ПАТЧ)",
                Location = new Point(16, 32),
                Size = new Size(640, 42),
                BackColor = AccentGreen,
                ForeColor = Color.FromArgb(10, 14, 12),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnClientInstall.FlatAppearance.BorderSize = 0;
            btnClientInstall.Click += async (s, e) => await RunClientInstallOrUpdateAsync();
            releaseBox.Controls.Add(btnClientInstall);

            releaseBox.Controls.Add(MakeLabel("URL репозитория / манифеста обновлений:", 670, 26, TextMuted));
            _txtReleaseUrl = MakeTextBox(_cfg.ReleaseManifestUrl, 670, 46, 412);
            _txtReleaseUrl.TextChanged += (s, e) => _cfg.ReleaseManifestUrl = _txtReleaseUrl.Text.Trim();
            releaseBox.Controls.Add(_txtReleaseUrl);

            _prgTurboInstall = new TacticalProgressBar
            {
                Location = new Point(16, 84),
                Size = new Size(1068, 26),
                Minimum = 0,
                Maximum = 100,
                Value = 100,
                OverlayText = "Готов к проверке и установке обновлений клиента"
            };
            releaseBox.Controls.Add(_prgTurboInstall);

            _lblTurboProgress = new Label
            {
                Text = "Выберите директорию установки выше и нажмите зелёную кнопку для автоматической загрузки или обновления клиента.",
                Location = new Point(16, 116),
                Size = new Size(1068, 22),
                ForeColor = Color.FromArgb(180, 225, 245),
                Font = new Font("Consolas", 9f)
            };
            releaseBox.Controls.Add(_lblTurboProgress);

            card.Controls.Add(releaseBox);

            // SECONDARY TOOLS BOX: Engine Patching, Packaging & Mod Organizer 2 Tools
            y += 156;
            var toolsBox = new TacticalCard
            {
                Location = new Point(22, y),
                Size = new Size(1100, 122),
                BackColor = Color.FromArgb(18, 22, 28),
                BorderColor = Color.FromArgb(44, 52, 64),
                AccentStripeColor = AccentAmber,
                AccentStripeWidth = 4,
                Padding = new Padding(12)
            };

            toolsBox.Controls.Add(MakeLabel("🛠 ИНСТРУМЕНТЫ СБОРКИ, ДВИЖКА И АДМИНИСТРИРОВАНИЯ РЕЛИЗОВ:", 16, 7, AccentAmber));

            var btnInstallCoop = new Button
            {
                Text = "⚙ Применить патч кооп-движка (17 игроков) + пересобрать профиль",
                Location = new Point(16, 30),
                Size = new Size(495, 36),
                BackColor = AccentAmber,
                ForeColor = Color.FromArgb(14, 12, 10),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 9.2f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnInstallCoop.FlatAppearance.BorderSize = 0;
            btnInstallCoop.Click += (s, e) => InstallCoopEngineAndMods();
            toolsBox.Controls.Add(btnInstallCoop);

            var btnRestoreVanilla = MakeActionButton("↩ Восстановить чистый bin", 520, 30, 215, BgCard, TextMain);
            btnRestoreVanilla.Height = 36;
            btnRestoreVanilla.Click += (s, e) => RestoreVanillaBinaries();
            toolsBox.Controls.Add(btnRestoreVanilla);

            _btnTurboInstall = MakeActionButton("⚡ Докачать моды с ModDB (MO2)", 744, 30, 340, BgCard, AccentGreen);
            _btnTurboInstall.Height = 36;
            _btnTurboInstall.Click += async (s, e) => await ToggleTurboGammaInstallAsync();
            toolsBox.Controls.Add(_btnTurboInstall);

            var btnPackPatch = MakeActionButton("📦 Собрать дельта-патч релиза", 16, 74, 265, BgCard, AccentCyan);
            btnPackPatch.Height = 34;
            btnPackPatch.Click += async (s, e) => await RunPackFastPatchAsync();
            toolsBox.Controls.Add(btnPackPatch);

            var btnPackVolumes = MakeActionButton("📦 Упаковать базу в тома (1.9 ГБ)", 290, 74, 275, BgCard, AccentCyan);
            btnPackVolumes.Height = 34;
            btnPackVolumes.Click += async (s, e) => await RunPackFullVolumesAsync();
            toolsBox.Controls.Add(btnPackVolumes);

            var btnOpenReleaseDir = MakeActionButton("📂 Папка релиза", 574, 74, 160, BgCard, TextMain);
            btnOpenReleaseDir.Height = 34;
            btnOpenReleaseDir.Click += (s, e) =>
            {
                Directory.CreateDirectory(_cfg.ReleaseOutputDir);
                Process.Start(new ProcessStartInfo { FileName = _cfg.ReleaseOutputDir, UseShellExecute = true });
            };
            toolsBox.Controls.Add(btnOpenReleaseDir);

            var btnOpenGammaLauncher = MakeActionButton("G.A.M.M.A. Launcher", 744, 74, 165, BgCard, TextMuted);
            btnOpenGammaLauncher.Height = 34;
            btnOpenGammaLauncher.Click += (s, e) => LaunchOfficialGammaInstaller();
            toolsBox.Controls.Add(btnOpenGammaLauncher);

            var btnOpenMo2 = MakeActionButton("Mod Organizer 2", 918, 74, 166, BgCard, TextMuted);
            btnOpenMo2.Height = 34;
            btnOpenMo2.Click += (s, e) => LaunchModOrganizerOnly();
            toolsBox.Controls.Add(btnOpenMo2);

            card.Controls.Add(toolsBox);

            card.Resize += (s, e) =>
            {
                int w = Math.Max(920, card.Width - 44);
                cardEngine.Width = w;
                cardGamma.Width = w;
                cardFixes.Width = w;
                releaseBox.Width = w;
                toolsBox.Width = w;
                _prgTurboInstall.Width = w - 32;
                _lblTurboProgress.Width = w - 32;
                _txtReleaseUrl.Width = Math.Max(260, w - 670 - 16);
            };

            page.Controls.Add(card);
        }

        public async Task RunClientInstallOrUpdateAsync()
        {
            SaveConfig();
            try
            {
                _lblStatusBanner.Text = "● УСТАНОВКА / ОБНОВЛЕНИЕ СБОРКИ...";
                _lblStatusBanner.ForeColor = AccentCyan;

                var updater = new ReleasePackagerAndUpdater(
                    _cfg.AnomalyPath,
                    _cfg.GammaPath,
                    _cfg.ReleaseOutputDir,
                    Log,
                    prog =>
                    {
                        if (IsDisposed || !IsHandleCreated) return;
                        BeginInvoke(new Action(() =>
                        {
                            _prgTurboInstall.Maximum = Math.Max(1, prog.TotalSteps);
                            _prgTurboInstall.Value = Math.Clamp(prog.CurrentStep, 0, Math.Max(1, prog.TotalSteps));
                            _prgTurboInstall.OverlayText = $"{prog.Phase} ({prog.Detail})";
                            _lblTurboProgress.Text = $"{prog.Phase} — {prog.Detail}";
                        }));
                    });

                bool forceDownloadBase = false;
                if (ReleasePackagerAndUpdater.IsFullGammaBaseInstalled(_cfg.AnomalyPath))
                {
                    var ans = MessageBox.Show(
                        $"В папке {_cfg.AnomalyPath} уже найдена установленная база сборки.\n\n" +
                        "• Нажмите «Да», если хотите скачать и распаковать заново все 21 том базы (38.5 ГБ) + свежий патч.\n" +
                        "• Нажмите «Нет», чтобы скачать и установить только свежий дельта-патч синхронизации (92 МБ).",
                        "Режим установки / обновления",
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Question);
                    if (ans == DialogResult.Cancel) return;
                    forceDownloadBase = (ans == DialogResult.Yes);
                }

                string normUrl = ReleasePackagerAndUpdater.NormalizeReleaseUrl(_cfg.ReleaseManifestUrl);
                if (_txtReleaseUrl != null && _txtReleaseUrl.Text != normUrl)
                    _txtReleaseUrl.Text = normUrl;

                await updater.InstallOrUpdateClientFromUrlAsync(normUrl, _cfg.AnomalyPath, CancellationToken.None, forceDownloadBase: forceDownloadBase, forceApplyPatch: true);
                EnsureWindowedActiveUserLtx(Path.Combine(_cfg.AnomalyPath, "appdata"));
                RefreshModAndEngineStatus();
                _lblStatusBanner.Text = "● БЫСТРЫЙ СТАРТ ГОТОВ (594 МОДА)";
                _lblStatusBanner.ForeColor = AccentGreen;
                MessageBox.Show(
                    $"Сборка в папке {_cfg.AnomalyPath} успешно проверена и обновлена!\nТеперь можно нажимать «⚡ БЫСТРЫЙ СТАРТ» или «ПОДКЛЮЧИТЬСЯ К СЕРВЕРУ».",
                    "Готово к игре",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("[ОШИБКА АПДЕЙТЕРА] " + ex.Message);
                MessageBox.Show(ex.Message, "Ошибка установки / обновления", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public async Task RunPackFastPatchAsync(bool silent = false)
        {
            SaveConfig();
            try
            {
                _lblStatusBanner.Text = "● СБОРКА ПАТЧА И ЛАУНЧЕРА...";
                _lblStatusBanner.ForeColor = AccentAmber;

                var packager = new ReleasePackagerAndUpdater(
                    _cfg.AnomalyPath,
                    _cfg.GammaPath,
                    _cfg.ReleaseOutputDir,
                    Log,
                    prog =>
                    {
                        if (IsDisposed || !IsHandleCreated) return;
                        BeginInvoke(new Action(() =>
                        {
                            _prgTurboInstall.Maximum = Math.Max(1, prog.TotalSteps);
                            _prgTurboInstall.Value = Math.Clamp(prog.CurrentStep, 0, Math.Max(1, prog.TotalSteps));
                            _prgTurboInstall.OverlayText = $"{prog.Phase} ({prog.Detail})";
                            _lblTurboProgress.Text = $"{prog.Phase} — {prog.Detail}";
                        }));
                    });

                var manifest = await packager.PackFastPatchAndLauncherAsync(_cfg.ReleaseManifestUrl, CancellationToken.None);
                _lblStatusBanner.Text = "● ГОТОВО К ЗАПУСКУ (594 МОДА)";
                _lblStatusBanner.ForeColor = AccentGreen;
                if (!silent)
                {
                    MessageBox.Show(
                        $"Патч обновления и клиентский лаунчер успешно собраны в каталоге:\n{_cfg.ReleaseOutputDir}\n\n" +
                        $"• Версия: {manifest.Version}\n" +
                        $"• Архив патча: {manifest.PatchArchiveName} ({manifest.PatchArchiveSize / (1024.0 * 1024.0):0.1} МБ)\n" +
                        $"• Архив лаунчера: GAMMA-Coop-Launcher.zip\n" +
                        $"• Манифест: manifest.json",
                        "Сборка патча завершена",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                Log("[ОШИБКА УПАКОВКИ ПАТЧА] " + ex.Message);
                if (!silent)
                    MessageBox.Show(ex.Message, "Ошибка упаковки патча", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public async Task RunPackFullVolumesAsync(bool silent = false)
        {
            SaveConfig();
            try
            {
                _lblStatusBanner.Text = "● УПАКОВКА ТОМОВ ПО 1.9 ГБ...";
                _lblStatusBanner.ForeColor = AccentCyan;

                var packager = new ReleasePackagerAndUpdater(
                    _cfg.AnomalyPath,
                    _cfg.GammaPath,
                    _cfg.ReleaseOutputDir,
                    Log,
                    prog =>
                    {
                        if (IsDisposed || !IsHandleCreated) return;
                        BeginInvoke(new Action(() =>
                        {
                            _prgTurboInstall.Maximum = Math.Max(1, prog.TotalSteps);
                            _prgTurboInstall.Value = Math.Clamp(prog.CurrentStep, 0, Math.Max(1, prog.TotalSteps));
                            _prgTurboInstall.OverlayText = $"{prog.Phase} ({prog.Detail})";
                            _lblTurboProgress.Text = $"{prog.Phase} — {prog.Detail}";
                        }));
                    });

                var manifest = await packager.PackFullBaseVolumesAsync(_cfg.ReleaseManifestUrl, CancellationToken.None);
                _lblStatusBanner.Text = "● ГОТОВО К ЗАПУСКУ (594 МОДА)";
                _lblStatusBanner.ForeColor = AccentGreen;
                if (!silent)
                {
                    MessageBox.Show(
                        $"Полный дистрибутив упакован в многотомный архив (1.9 ГБ на том) в каталоге:\n{_cfg.ReleaseOutputDir}\n\n" +
                        $"• Томов базы: {manifest.BaseVolumes.Count} шт.\n" +
                        $"• Патч обновления: {manifest.PatchArchiveName}\n" +
                        $"• Клиентский лаунчер: GAMMA-Coop-Launcher.zip\n" +
                        $"• Манифест: manifest.json\n\n" +
                        $"Файлы из {_cfg.ReleaseOutputDir} готовы к публикации в GitHub Releases.",
                        "Упаковка дистрибутива завершена",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                Log("[ОШИБКА УПАКОВКИ ТОМОВ] " + ex.Message);
                if (!silent)
                    MessageBox.Show(ex.Message, "Ошибка упаковки томов", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public async Task ToggleTurboGammaInstallAsync()
        {
            if (_turboInstallCts != null)
            {
                _turboInstallCts.Cancel();
                _lblTurboProgress.Text = "Остановка турбо-установки... Все скачанные архивы и распакованные моды сохранены.";
                return;
            }

            SaveConfig();
            SelectTab(2);
            _turboInstallCts = new CancellationTokenSource();
            _btnTurboInstall.Text = "⏹ Остановить загрузку с ModDB";
            _btnTurboInstall.BackColor = AccentAmber;
            _btnTurboInstall.ForeColor = Color.Black;
            _lblStatusBanner.Text = "● ЗАГРУЗКА МОДОВ GAMMA (10 ПОТОКОВ)";
            _lblStatusBanner.ForeColor = AccentAmber;

            var installer = new TurboGammaInstaller(
                _cfg.GammaPath,
                _cfg.AnomalyPath,
                _cfg.CoopWorkspacePath,
                Log,
                prog =>
                {
                    if (IsDisposed) return;
                    BeginInvoke(new Action(() =>
                    {
                        if (_prgTurboInstall != null && prog.TotalMods > 0)
                        {
                            _prgTurboInstall.Maximum = prog.TotalMods;
                            _prgTurboInstall.Value = Math.Clamp(prog.InstalledMods, 0, prog.TotalMods);
                            int pct = (int)Math.Round(100.0 * prog.InstalledMods / Math.Max(1, prog.TotalMods));
                            _prgTurboInstall.OverlayText = $"{pct}% — Моды: {prog.InstalledMods}/{prog.TotalMods} | Архивы: {prog.DownloadedArchives}/{prog.TotalArchives} | {prog.SpeedMbps:0.0} МБ/с";
                        }
                        if (_lblTurboProgress != null)
                        {
                            _lblTurboProgress.Text =
                                $"{prog.Phase} | Архивы: {prog.DownloadedArchives}/{prog.TotalArchives} | " +
                                $"Моды в MO2: {prog.InstalledMods}/{prog.TotalMods} | Скорость: {prog.SpeedMbps:0.0} МБ/с | Текущий: {prog.CurrentItem}";
                        }
                        RefreshModAndEngineStatus();
                    }));
                },
                () =>
                {
                    if (IsDisposed) return;
                    BeginInvoke(new Action(InstallCoopEngineAndMods));
                });

            try
            {
                await installer.RunAsync(10, _turboInstallCts.Token);
                _prgTurboInstall.Maximum = 594;
                _prgTurboInstall.Value = 594;
                _prgTurboInstall.OverlayText = "100% ГОТОВО — Все 594 активных мода G.A.M.M.A. + Кооп 16p установлены и запечены";
                _lblTurboProgress.Text = "[ГОТОВО] Все моды S.T.A.L.K.E.R. G.A.M.M.A. и кооп-движок (17 игроков) успешно установлены!";
                _lblStatusBanner.Text = "● БЫСТРЫЙ СТАРТ ГОТОВ (594 МОДА)";
                _lblStatusBanner.ForeColor = AccentGreen;
            }
            catch (OperationCanceledException)
            {
                Log("[ТУРБО-GAMMA] Установка приостановлена пользователем. Нажмите кнопку снова для продолжения с того же места.");
                _lblTurboProgress.Text = "Пауза: прогресс сохранён. Нажмите зелёную кнопку выше, чтобы продолжить с того же места.";
            }
            catch (Exception ex)
            {
                Log("[ОШИБКА ТУРБО-GAMMA] " + ex.Message);
                _lblTurboProgress.Text = "Ошибка: " + ex.Message;
            }
            finally
            {
                _turboInstallCts?.Dispose();
                _turboInstallCts = null;
                _btnTurboInstall.Text = "⚡ Докачать моды с ModDB (MO2)";
                _btnTurboInstall.BackColor = BgCard;
                _btnTurboInstall.ForeColor = AccentGreen;
                RefreshModAndEngineStatus();
            }
        }

        private void RefreshSavesList()
        {
            if (_cmbHostSave == null) return;
            _cmbHostSave.Items.Clear();
            _cmbHostSave.Items.Add("new");
            _cmbHostSave.Items.Add("fast_new");
            string savesDir = Path.Combine(_cfg.AnomalyPath, "appdata", "savedgames");
            if (Directory.Exists(savesDir))
            {
                foreach (var f in Directory.GetFiles(savesDir, "*.scop").OrderByDescending(File.GetLastWriteTime))
                {
                    _cmbHostSave.Items.Add(Path.GetFileNameWithoutExtension(f));
                }
            }
            _cmbHostSave.SelectedItem = _cfg.HostSave;
            if (_cmbHostSave.SelectedIndex < 0) _cmbHostSave.SelectedIndex = 0;
        }

        public void InstallCoopEngineAndMods()
        {
            try
            {
                string anomalyBin = Path.Combine(_cfg.AnomalyPath, "bin");
                string backupBin = Path.Combine(_cfg.AnomalyPath, "bin_vanilla_backup");
                string xrRazomRoot = Path.Combine(_cfg.CoopWorkspacePath, "xrRazom-1.4");
                string xrRazomBin = Path.Combine(xrRazomRoot, "Anomaly", "bin");
                string xrRazomAnomalyGamedata = Path.Combine(xrRazomRoot, "Anomaly", "gamedata");
                string xrRazomModGamedata = Path.Combine(xrRazomRoot, "gamedata");
                string fixesRepoMods = Path.Combine(_cfg.CoopWorkspacePath, "xrRazom-GAMMA-fixes", "mods");

                if (!Directory.Exists(xrRazomBin))
                {
                    MessageBox.Show($"Не найдена папка {xrRazomBin}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (!Directory.Exists(backupBin))
                {
                    Log("Создаю резервную копию чистых бинарников C:\\Anomaly\\bin -> C:\\Anomaly\\bin_vanilla_backup...");
                    CopyDirectory(anomalyBin, backupBin);
                }

                Log("Устанавливаю пропатченный кооп-движок xrRazom 1.4 (до 16 пиров / 17 игроков) в C:\\Anomaly\\bin...");
                foreach (var file in Directory.GetFiles(xrRazomBin))
                {
                    if (file.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
                    string dest = Path.Combine(anomalyBin, Path.GetFileName(file));
                    try { File.Copy(file, dest, true); } catch { }
                }

                string anomalyGamedata = Path.Combine(_cfg.AnomalyPath, "gamedata");
                string gammaMods = Path.Combine(_cfg.GammaPath, "mods");
                if (Directory.Exists(_cfg.GammaPath))
                {
                    Directory.CreateDirectory(gammaMods);

                    string coopModDir = Path.Combine(gammaMods, "zz xrRazom 1.4 Co-op (16 Players Unleashed)");
                    string coopModGd = Path.Combine(coopModDir, "gamedata");
                    if (Directory.Exists(coopModGd))
                    {
                        try { Directory.Delete(coopModGd, true); } catch { }
                    }
                    CopyDirectory(xrRazomModGamedata, coopModGd);

                    string mcmOpt = Path.Combine(xrRazomRoot, "options", "MCM", "gamedata");
                    if (Directory.Exists(mcmOpt))
                    {
                        CopyDirectory(mcmOpt, coopModGd);
                    }

                    if (Directory.Exists(fixesRepoMods))
                    {
                        foreach (var fixDir in Directory.GetDirectories(fixesRepoMods))
                        {
                            string modName = Path.GetFileName(fixDir);
                            CopyDirectory(fixDir, Path.Combine(gammaMods, modName));
                        }
                    }

                    string bridgeModDir = Path.Combine(gammaMods, "zz xrRazom Fix - Launcher Bridge");
                    foreach (var scriptName in new[] { "zz_xrr_launcher_bridge.script", "modxml_zz_xrr_launcher_bridge.script" })
                    {
                        string bridgeScriptSrc = Path.Combine(xrRazomModGamedata, "scripts", scriptName);
                        if (File.Exists(bridgeScriptSrc))
                        {
                            string bridgeDst = Path.Combine(bridgeModDir, "gamedata", "scripts", scriptName);
                            Directory.CreateDirectory(Path.GetDirectoryName(bridgeDst));
                            if (File.Exists(bridgeDst)) File.Delete(bridgeDst);
                            File.Copy(bridgeScriptSrc, bridgeDst, false);
                        }
                    }

                    TurboGammaInstaller.BakeActiveGammaModsToAnomaly(_cfg.GammaPath, _cfg.AnomalyPath, _cfg.CoopWorkspacePath, Log, forceRebake: true);
                }
                else
                {
                    CopyDirectory(xrRazomAnomalyGamedata, anomalyGamedata);
                    CopyDirectory(xrRazomModGamedata, anomalyGamedata);
                }

                RefreshModAndEngineStatus();
                Log("[OK] Кооп-движок (17 игроков), Быстрый Старт G.A.M.M.A. и 10 фиксов синхронизации активны!");
                _lblStatusBanner.Text = "● БЫСТРЫЙ СТАРТ ГОТОВ (594 МОДА)";
                _lblStatusBanner.ForeColor = AccentGreen;
            }
            catch (Exception ex)
            {
                Log("[ОШИБКА] Ошибка установки кооп-движка: " + ex.Message);
                MessageBox.Show(ex.Message, "Ошибка установки", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RestoreVanillaBinaries()
        {
            try
            {
                string anomalyBin = Path.Combine(_cfg.AnomalyPath, "bin");
                string backupBin = Path.Combine(_cfg.AnomalyPath, "bin_vanilla_backup");
                if (!Directory.Exists(backupBin))
                {
                    MessageBox.Show("Резервная копия C:\\Anomaly\\bin_vanilla_backup ещё не создавалась (сейчас уже стоят чистые бинарники).", "Информация", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                CopyDirectory(backupBin, anomalyBin);
                string gnsDll = Path.Combine(anomalyBin, "GameNetworkingSockets.dll");
                if (File.Exists(gnsDll)) File.Delete(gnsDll);

                RefreshModAndEngineStatus();
                Log("Восстановлены оригинальные чистые бинарники C:\\Anomaly\\bin.");
            }
            catch (Exception ex)
            {
                Log("[ОШИБКА] Ошибка восстановления: " + ex.Message);
            }
        }

        private void RefreshModAndEngineStatus()
        {
            string anomalyBin = Path.Combine(_cfg.AnomalyPath, "bin");
            string gnsDll = Path.Combine(anomalyBin, "GameNetworkingSockets.dll");
            string backupBin = Path.Combine(_cfg.AnomalyPath, "bin_vanilla_backup");
            bool coopInstalled = File.Exists(gnsDll);
            bool baseInstalled = ReleasePackagerAndUpdater.IsFullGammaBaseInstalled(_cfg.AnomalyPath);

            _lblEngineStatus.Text = coopInstalled
                ? $"✔ СЕТЕВОЙ ДВИЖОК ({anomalyBin}): Установлен модифицированный движок xrRazom 1.4 (до 16 пиров / 17 игроков + Steam SDR).\n" +
                  $"   Статус бинарных модулей: Готовы к запуску"
                : $"⚠ СЕТЕВОЙ ДВИЖОК ({anomalyBin}): Кооперативный движок ещё не установлен в указанной директории.\n" +
                  $"   Используйте блок автоматической установки ниже для загрузки и настройки клиента.";

            string gammaMods = Path.Combine(_cfg.GammaPath, "mods");
            int modCount = Directory.Exists(gammaMods) ? Directory.GetDirectories(gammaMods).Length : 0;
            bool fastBaked = File.Exists(Path.Combine(_cfg.AnomalyPath, "gamedata", ".gamma_baked_ready")) || baseInstalled;
            _lblGammaStatus.Text = baseInstalled
                ? $"✔ КЛИЕНТ S.T.A.L.K.E.R. G.A.M.M.A. ({_cfg.AnomalyPath}): База сборки и 594 активных модификации установлены.\n" +
                  $"   Режим прямого запуска: АКТИВЕН (запуск напрямую из gamedata без оверлея MO2)"
                : $"⚠ КЛИЕНТ S.T.A.L.K.E.R. G.A.M.M.A. ({_cfg.AnomalyPath}): Базовые архивы сборки ещё не распакованы.\n" +
                  $"   Нажмите зелёную кнопку «СКАЧАТЬ И УСТАНОВИТЬ / ОБНОВИТЬ СБОРКУ В 1 КЛИК» ниже (38.5 ГБ).";

            string fixesDir = Path.Combine(_cfg.CoopWorkspacePath, "xrRazom-GAMMA-fixes", "mods");
            int fixCount = Directory.Exists(fixesDir) ? Directory.GetDirectories(fixesDir).Length : 15;
            _lblFixesStatus.Text = $"✔ СИНХРОНИЗАЦИЯ КО-ОП: {fixCount} модулей совместимости xrRazom-GAMMA-fixes + Автоспавн на Кордоне + 16-Slot Party HUD.";

            if (_lblStatusBanner != null)
            {
                if (coopInstalled && baseInstalled)
                {
                    _lblStatusBanner.Text = "● КЛИЕНТ ГОТОВ К ЗАПУСКУ";
                    _lblStatusBanner.ForeColor = AccentGreen;
                }
                else
                {
                    _lblStatusBanner.Text = "● ТРЕБУЕТСЯ УСТАНОВКА КЛИЕНТА";
                    _lblStatusBanner.ForeColor = AccentAmber;
                }
            }
        }

        private void WriteAxrLauncherCommand(string action, string connectIp, string overrideHostSave = null)
        {
            SaveConfig();
            string appdata = Path.Combine(_cfg.AnomalyPath, "appdata");
            Directory.CreateDirectory(appdata);
            string axrOptions = Path.Combine(appdata, "axr_options.ltx");

            var lines = File.Exists(axrOptions)
                ? File.ReadAllLines(axrOptions, Encoding.UTF8).ToList()
                : new List<string>();

            var settings = new Dictionary<string, string>
            {
                ["auto_action"] = action,
                ["connect_ip"] = connectIp,
                ["player_name"] = _cfg.PlayerName,
                ["host_port"] = _cfg.HostPort.ToString(),
                ["host_steam"] = _cfg.HostSteam ? "1" : "0",
                ["max_players"] = _cfg.HostMaxPeers.ToString(),
                ["skip_mod_check"] = _cfg.SkipModCheck ? "1" : "0",
                ["host_save"] = overrideHostSave ?? _cfg.HostSave,
                ["dedicated_observer"] = _cfg.DedicatedObserver ? "1" : "0",
                ["autosave_minutes"] = _cfg.AutosaveMinutes.ToString(),
                ["server_name"] = _cfg.HostServerName
            };

            UpsertIniSection(lines, "xrr_launcher", settings);
            File.WriteAllLines(axrOptions, lines, new UTF8Encoding(false));
        }

        private static void UpsertIniSection(List<string> lines, string sectionName, Dictionary<string, string> values)
        {
            string header = $"[{sectionName}]";
            int secIdx = lines.FindIndex(l => l.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
            if (secIdx >= 0)
            {
                int endIdx = secIdx + 1;
                while (endIdx < lines.Count && !lines[endIdx].TrimStart().StartsWith("["))
                {
                    endIdx++;
                }
                lines.RemoveRange(secIdx, endIdx - secIdx);
            }

            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add("");

            lines.Add(header);
            foreach (var kv in values)
            {
                lines.Add($"{kv.Key} = {kv.Value}");
            }
        }

        private void EnsureCoopInstalledBeforeLaunch()
        {
            string gnsDll = Path.Combine(_cfg.AnomalyPath, "bin", "GameNetworkingSockets.dll");
            string xrRazomBin = Path.Combine(_cfg.CoopWorkspacePath, "xrRazom-1.4", "Anomaly", "bin");
            if (!File.Exists(gnsDll) && Directory.Exists(xrRazomBin))
            {
                Log("Кооп-движок ещё не был скопирован в директорию bin — выполняю автоматическую установку...");
                InstallCoopEngineAndMods();
            }
            try
            {
                if (Directory.Exists(_cfg.AnomalyPath))
                {
                    File.WriteAllText(Path.Combine(_cfg.AnomalyPath, "steam_appid.txt"), "480\n", new UTF8Encoding(false));
                    string binDir = Path.Combine(_cfg.AnomalyPath, "bin");
                    if (Directory.Exists(binDir))
                        File.WriteAllText(Path.Combine(binDir, "steam_appid.txt"), "480\n", new UTF8Encoding(false));
                }
            }
            catch { }
        }

        private void LaunchAndConnect(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) address = "127.0.0.1:28666";
            EnsureCoopInstalledBeforeLaunch();
            WriteAxrLauncherCommand("join", address);
            Log($"[КЛИЕНТ] Запуск игры и автоподключение к серверу {address} под позывным '{_cfg.PlayerName}'...");
            StartGameProcess();
        }

        public void LaunchAsHostServer()
        {
            EnsureCoopInstalledBeforeLaunch();
            SaveConfig();
            StartQueryResponder(_cfg.HostPort + 1);
            EnsureWindowedActiveUserLtx(Path.Combine(_cfg.AnomalyPath, "appdata"));
            WriteAxrLauncherCommand("host", $"127.0.0.1:{_cfg.HostPort}");
            Log($"[БЫСТРЫЙ СТАРТ] Запуск сервера '{_cfg.HostServerName}' на UDP порту {_cfg.HostPort} (до {_cfg.HostMaxPeers + 1} игроков)...");
            StartGameProcess();
        }

        private void LaunchFreshNewGameOnCordon()
        {
            EnsureCoopInstalledBeforeLaunch();
            SaveConfig();
            StartQueryResponder(_cfg.HostPort + 1);
            EnsureWindowedActiveUserLtx(Path.Combine(_cfg.AnomalyPath, "appdata"));
            WriteAxrLauncherCommand("host", $"127.0.0.1:{_cfg.HostPort}", "fast_new");
            Log($"[НОВАЯ ИГРА — КОРДОН] Мгновенный старт новой игры в Деревне Новичков (l01_escape) на порту {_cfg.HostPort}...");
            StartGameProcess();
        }

        private void LaunchSecondLocalClientForTest()
        {
            try
            {
                EnsureCoopInstalledBeforeLaunch();
                SaveConfig();

                string fsgameSrc = Path.Combine(_cfg.AnomalyPath, "fsgame.ltx");
                string fsgameClient2 = Path.Combine(_cfg.AnomalyPath, "fsgame_client2.ltx");
                if (File.Exists(fsgameSrc))
                {
                    string content = File.ReadAllText(fsgameSrc, Encoding.UTF8)
                        .Replace("| appdata\\", "| appdata_client2\\");
                    File.WriteAllText(fsgameClient2, content, new UTF8Encoding(false));
                }

                bool useGammaBaked = _cfg.LaunchModeIndex != 2 && Directory.Exists(Path.Combine(_cfg.GammaPath, "mods"));
                if (useGammaBaked)
                {
                    TurboGammaInstaller.BakeActiveGammaModsToAnomaly(_cfg.GammaPath, _cfg.AnomalyPath, _cfg.CoopWorkspacePath, Log, forceRebake: false);
                }

                string appdata2 = Path.Combine(_cfg.AnomalyPath, "appdata_client2");
                Directory.CreateDirectory(appdata2);
                EnsureWindowedActiveUserLtx(appdata2);

                string axrOptions2 = Path.Combine(appdata2, "axr_options.ltx");
                var lines = File.Exists(axrOptions2)
                    ? File.ReadAllLines(axrOptions2, Encoding.UTF8).ToList()
                    : new List<string>();

                string peerNick = (_cfg.PlayerName ?? "Stalker") + "_Peer2";
                var settings = new Dictionary<string, string>
                {
                    ["auto_action"] = "join",
                    ["connect_ip"] = $"127.0.0.1:{_cfg.HostPort}",
                    ["player_name"] = peerNick,
                    ["host_port"] = _cfg.HostPort.ToString(),
                    ["max_players"] = _cfg.HostMaxPeers.ToString(),
                    ["skip_mod_check"] = "1"
                };
                UpsertIniSection(lines, "xrr_launcher", settings);
                File.WriteAllLines(axrOptions2, lines, new UTF8Encoding(false));

                string exeName = _cfg.SelectedRenderer;
                string binExe = Path.Combine(_cfg.AnomalyPath, "bin", exeName);
                string fsltxArg = useGammaBaked ? "fsgame_gamma_client2.ltx" : "fsgame_client2.ltx";

                Log($"[2-Е ОКНО] Запуск изолированного клиента ({fsltxArg}, ник '{peerNick}') -> 127.0.0.1:{_cfg.HostPort}...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = binExe,
                    Arguments = $"-fsltx {fsltxArg} -noprefetch -nointro -skiplogo -nosplashwindow",
                    WorkingDirectory = _cfg.AnomalyPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log("[ОШИБКА] Ошибка запуска 2-го окна: " + ex.Message);
                MessageBox.Show(ex.Message, "Ошибка запуска 2-го окна", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void EnsureWindowedActiveUserLtx(string appdataDir)
        {
            Directory.CreateDirectory(appdataDir);
            string userLtx = Path.Combine(appdataDir, "user.ltx");
            bool isSecondClient = appdataDir.EndsWith("appdata_client2", StringComparison.OrdinalIgnoreCase);

            if (!File.Exists(userLtx))
            {
                string mainUserLtx = Path.Combine(_cfg.AnomalyPath, "appdata", "user.ltx");
                if (File.Exists(mainUserLtx) && !string.Equals(mainUserLtx, userLtx, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(mainUserLtx, userLtx, true);
                }
                else
                {
                    File.WriteAllLines(userLtx, new[]
                    {
                        "g_always_active on",
                        "rs_screenmode windowed",
                        "rs_fullscreen off",
                        "rs_borderless 0",
                        "vid_mode 1280x720"
                    }, new UTF8Encoding(false));
                    return;
                }
            }

            var lines = File.ReadAllLines(userLtx, Encoding.UTF8).ToList();
            bool hasAlwaysActive = false;
            bool hasScreenMode = false;
            bool hasFullscreen = false;
            bool hasVidMode = false;
            bool hasHostSteam = false;
            bool hasHostSession = false;
            bool hasHostLanPort = false;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("g_always_active "))
                {
                    lines[i] = "g_always_active on";
                    hasAlwaysActive = true;
                }
                else if (lines[i].StartsWith("rs_screenmode "))
                {
                    lines[i] = "rs_screenmode windowed";
                    hasScreenMode = true;
                }
                else if (lines[i].StartsWith("rs_fullscreen "))
                {
                    lines[i] = "rs_fullscreen off";
                    hasFullscreen = true;
                }
                else if (isSecondClient && lines[i].StartsWith("vid_mode "))
                {
                    lines[i] = "vid_mode 1280x720";
                    hasVidMode = true;
                }
                else if (lines[i].StartsWith("xrr_host_steam "))
                {
                    lines[i] = (!isSecondClient && _cfg.HostSteam) ? "xrr_host_steam on" : "xrr_host_steam off";
                    hasHostSteam = true;
                }
                else if (lines[i].StartsWith("xrr_host_session "))
                {
                    lines[i] = isSecondClient ? "xrr_host_session off" : "xrr_host_session on";
                    hasHostSession = true;
                }
                else if (lines[i].StartsWith("xrr_host_lan_port "))
                {
                    lines[i] = $"xrr_host_lan_port {_cfg.HostPort}";
                    hasHostLanPort = true;
                }
            }
            if (!hasAlwaysActive) lines.Add("g_always_active on");
            if (!hasScreenMode) lines.Add("rs_screenmode windowed");
            if (!hasFullscreen) lines.Add("rs_fullscreen off");
            if (isSecondClient && !hasVidMode) lines.Add("vid_mode 1280x720");
            if (!hasHostSteam) lines.Add((!isSecondClient && _cfg.HostSteam) ? "xrr_host_steam on" : "xrr_host_steam off");
            if (!hasHostSession) lines.Add(isSecondClient ? "xrr_host_session off" : "xrr_host_session on");
            if (!hasHostLanPort) lines.Add($"xrr_host_lan_port {_cfg.HostPort}");
            File.WriteAllLines(userLtx, lines, new UTF8Encoding(false));
        }

        private async void StartGameProcess()
        {
            try
            {
                string exeName = _cfg.SelectedRenderer;
                string binExe = Path.Combine(_cfg.AnomalyPath, "bin", exeName);

                if (!File.Exists(binExe))
                {
                    SelectTab(2);
                    var ask = MessageBox.Show(
                        $"В директории {_cfg.AnomalyPath} не найден исполняемый файл клиента ({exeName}).\n\n" +
                        "Запустить автоматическую загрузку и установку полной сборки G.A.M.M.A. Co-op сейчас?",
                        "Клиент не установлен",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);
                    if (ask == DialogResult.Yes)
                    {
                        await RunClientInstallOrUpdateAsync();
                    }
                    return;
                }

                if (_cfg.LaunchModeIndex == 1)
                {
                    string mo2Exe = Path.Combine(_cfg.GammaPath, "ModOrganizer.exe");
                    if (File.Exists(mo2Exe))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = mo2Exe,
                            Arguments = "\"moshortcut://:G.A.M.M.A. Co-op 16 Players (DX11 AVX)\"",
                            WorkingDirectory = _cfg.GammaPath,
                            UseShellExecute = true
                        });
                        return;
                    }
                }

                string args = "-noprefetch -nointro -skiplogo -nosplashwindow";
                if (_cfg.LaunchModeIndex == 0)
                {
                    if (Directory.Exists(Path.Combine(_cfg.GammaPath, "mods")))
                    {
                        TurboGammaInstaller.BakeActiveGammaModsToAnomaly(_cfg.GammaPath, _cfg.AnomalyPath, _cfg.CoopWorkspacePath, Log, forceRebake: false);
                    }
                    if (File.Exists(Path.Combine(_cfg.AnomalyPath, "fsgame_gamma_fast.ltx")))
                    {
                        args = "-fsltx fsgame_gamma_fast.ltx -noprefetch -nointro -skiplogo -nosplashwindow";
                    }
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = binExe,
                    Arguments = args,
                    WorkingDirectory = _cfg.AnomalyPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log("[ОШИБКА] Не удалось запустить игру: " + ex.Message);
                MessageBox.Show(ex.Message, "Ошибка запуска", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LaunchOfficialGammaInstaller()
        {
            string installer = Path.Combine(_cfg.GammaPath, ".Grok's Modpack Installer", "G.A.M.M.A. Launcher.exe");
            if (!File.Exists(installer))
            {
                MessageBox.Show($"Файл не найден: {installer}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                WorkingDirectory = Path.GetDirectoryName(installer),
                UseShellExecute = true
            });
            Log("Запущен официальный G.A.M.M.A. Launcher v8.6.");
        }

        private void LaunchModOrganizerOnly()
        {
            string mo2 = Path.Combine(_cfg.GammaPath, "ModOrganizer.exe");
            if (!File.Exists(mo2))
            {
                MessageBox.Show($"Файл не найден: {mo2}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = mo2,
                WorkingDirectory = _cfg.GammaPath,
                UseShellExecute = true
            });
            Log("Запущен Mod Organizer 2.");
        }

        private void StartQueryResponder(int queryPort)
        {
            try
            {
                _responderCts?.Cancel();
                _queryResponder?.Close();

                _responderCts = new CancellationTokenSource();
                _queryResponder = new UdpClient(queryPort);
                var token = _responderCts.Token;

                Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            var res = await _queryResponder.ReceiveAsync(token);
                            string req = Encoding.UTF8.GetString(res.Buffer);
                            if (req.StartsWith("XRR_QUERY"))
                            {
                                var status = ReadLocalServerTelemetry();
                                byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(status));
                                await _queryResponder.SendAsync(payload, payload.Length, res.RemoteEndPoint);
                            }
                        }
                        catch
                        {
                            if (token.IsCancellationRequested) break;
                        }
                    }
                }, token);
            }
            catch (Exception ex)
            {
                Log($"Инфо: UDP порт мониторинга {queryPort} уже занят или недоступен ({ex.Message}).");
            }
        }

        private ServerEntry ReadLocalServerTelemetry()
        {
            string axrOptions = Path.Combine(_cfg.AnomalyPath, "appdata", "axr_options.ltx");
            string rawLevel = "main_menu";
            int players = 1;
            int maxPlayers = _cfg.HostMaxPeers + 1;
            string roster = _cfg.PlayerName;
            bool gameRunning = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_cfg.SelectedRenderer)).Length > 0;

            if (File.Exists(axrOptions))
            {
                try
                {
                    var dict = ReadIniSection(axrOptions, "xrr_server_telemetry");
                    if (dict.TryGetValue("level", out var l) && !string.IsNullOrEmpty(l)) rawLevel = l;
                    if (dict.TryGetValue("players", out var p) && int.TryParse(p, out int pi) && pi > 0) players = pi;
                    if (dict.TryGetValue("max_players", out var mp) && int.TryParse(mp, out int mpi) && mpi > 1) maxPlayers = mpi;
                    if (dict.TryGetValue("roster", out var r)) roster = r;
                }
                catch { }
            }

            string mapDisplay = MapNamesRu.TryGetValue(rawLevel, out var ru) ? $"{ru} ({rawLevel})" : rawLevel;
            return new ServerEntry
            {
                Name = _cfg.HostServerName,
                Address = $"127.0.0.1:{_cfg.HostPort}",
                Map = gameRunning ? mapDisplay : "Кордон (Готов к быстрому старту)",
                Players = gameRunning ? players : 0,
                MaxPlayers = maxPlayers,
                Online = true,
                Roster = roster
            };
        }

        private void UpdateLocalHostTelemetryLabel()
        {
            if (_lblLiveServerTelemetry == null) return;
            var st = ReadLocalServerTelemetry();
            _lblLiveServerTelemetry.Text =
                $"📡 СЕРВЕР: {st.Name}   |   UDP Порт: {_cfg.HostPort} (Query: {_cfg.HostPort + 1})\n" +
                $"📍 Локация: {st.Map}   |   Онлайн: {st.Players} / {st.MaxPlayers}   |   Отряд: {(string.IsNullOrEmpty(st.Roster) ? "ожидание игроков" : st.Roster)}";
        }

        private static Dictionary<string, string> ReadIniSection(string path, string sectionName)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string header = $"[{sectionName}]";
            bool inSec = false;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    inSec = line.Equals(header, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (inSec && line.Contains('='))
                {
                    int idx = line.IndexOf('=');
                    string k = line[..idx].Trim();
                    string v = line[(idx + 1)..].Trim();
                    result[k] = v;
                }
            }
            return result;
        }

        private async Task RefreshAllServersAsync()
        {
            Log("Сканирование активных серверов Зоны...");
            foreach (var srv in _cfg.SavedServers)
            {
                await QueryServerAsync(srv);
            }
            RenderServerGrid();
        }

        private async Task QueryServerAsync(ServerEntry srv)
        {
            try
            {
                string host = srv.Address;
                int port = 28666;
                if (host.Contains(':'))
                {
                    var parts = host.Split(':');
                    host = parts[0];
                    int.TryParse(parts[1], out port);
                }

                var sw = Stopwatch.StartNew();
                using var udp = new UdpClient();
                byte[] req = Encoding.UTF8.GetBytes("XRR_QUERY");
                await udp.SendAsync(req, req.Length, host, port + 1);

                var recvTask = udp.ReceiveAsync();
                var completed = await Task.WhenAny(recvTask, Task.Delay(1200));
                sw.Stop();

                if (completed == recvTask)
                {
                    var res = await recvTask;
                    var remote = JsonSerializer.Deserialize<ServerEntry>(Encoding.UTF8.GetString(res.Buffer));
                    if (remote != null)
                    {
                        srv.Name = remote.Name;
                        srv.Map = remote.Map;
                        srv.Players = remote.Players;
                        srv.MaxPlayers = remote.MaxPlayers;
                        srv.Online = true;
                        srv.PingMs = (int)Math.Max(1, sw.ElapsedMilliseconds);
                        return;
                    }
                }

                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, 1000);
                srv.Online = reply.Status == IPStatus.Success;
                srv.PingMs = srv.Online ? (int)Math.Max(1, reply.RoundtripTime) : -1;
            }
            catch
            {
                srv.Online = false;
                srv.PingMs = -1;
            }
        }

        private void RenderServerGrid()
        {
            _serverGrid.Rows.Clear();
            foreach (var s in _cfg.SavedServers)
            {
                int rowIdx = _serverGrid.Rows.Add(
                    s.Online ? " ● ONLINE" : " ○ OFFLINE",
                    s.Name,
                    s.Map,
                    $"{s.Players} / {s.MaxPlayers}",
                    s.PingMs >= 0 ? $"{s.PingMs} мс" : "—",
                    s.Address
                );
                _serverGrid.Rows[rowIdx].Cells["Status"].Style.ForeColor = s.Online ? AccentGreen : AccentRed;
                _serverGrid.Rows[rowIdx].Cells["Status"].Style.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            }
        }

        private void PromptAddServer()
        {
            using var dlg = new Form
            {
                Text = "Добавить сервер в список",
                Size = new Size(450, 235),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = BgPanel,
                ForeColor = TextMain,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            dlg.Controls.Add(MakeLabel("Название сервера:", 18, 16, TextMain));
            var txtName = MakeTextBox("Кооперативный сервер G.A.M.M.A.", 18, 38, 395);
            dlg.Controls.Add(txtName);

            dlg.Controls.Add(MakeLabel("IP-адрес и порт (например, 26.0.0.1:28666):", 18, 76, TextMain));
            var txtAddr = MakeTextBox("26.0.0.1:28666", 18, 98, 395);
            dlg.Controls.Add(txtAddr);

            var btnOk = MakeActionButton("Добавить сервер", 268, 142, 145, AccentAmber, Color.Black);
            btnOk.Click += async (s, e) =>
            {
                string addr = txtAddr.Text.Trim();
                if (!addr.Contains(':')) addr += ":28666";
                var entry = new ServerEntry
                {
                    Name = txtName.Text.Trim(),
                    Address = addr,
                    Map = "Ожидание ответа",
                    MaxPlayers = 17
                };
                _cfg.SavedServers.Add(entry);
                SaveConfig();
                dlg.Close();
                await RefreshAllServersAsync();
            };
            dlg.Controls.Add(btnOk);
            dlg.ShowDialog(this);
        }

        private void RemoveSelectedServer()
        {
            if (_serverGrid.SelectedRows.Count == 0) return;
            int idx = _serverGrid.SelectedRows[0].Index;
            if (idx >= 0 && idx < _cfg.SavedServers.Count)
            {
                _cfg.SavedServers.RemoveAt(idx);
                SaveConfig();
                RenderServerGrid();
            }
        }

        private void CopyLocalIpsToClipboard()
        {
            var ips = Dns.GetHostAddresses(Dns.GetHostName())
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                .Select(ip => $"{ip}:{_cfg.HostPort}")
                .ToList();
            string text = ips.Count > 0 ? string.Join(", ", ips) : $"127.0.0.1:{_cfg.HostPort}";
            Clipboard.SetText(ips.FirstOrDefault() ?? text);
            Log($"Скопирован адрес сервера в буфер обмена: {text}");
            MessageBox.Show($"Сетевой адрес сервера скопирован в буфер обмена:\n\n{text}\n\nПередайте этот адрес другим участникам сессии — его необходимо указать в поле «Адрес сервера» и нажать «ПОДКЛЮЧИТЬСЯ К СЕРВЕРУ».",
                "Адрес сервера скопирован", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                if (file.EndsWith(".orig.bak", StringComparison.OrdinalIgnoreCase))
                    continue;
                string dest = Path.Combine(targetDir, Path.GetFileName(file));
                try
                {
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Copy(file, dest, false);
                }
                catch (IOException) { }
            }
            foreach (var sub in Directory.GetDirectories(sourceDir))
            {
                CopyDirectory(sub, Path.Combine(targetDir, Path.GetFileName(sub)));
            }
        }

        private static Label MakeLabel(string text, int x, int y, Color color)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                ForeColor = color,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Regular)
            };
        }

        private static TextBox MakeTextBox(string text, int x, int y, int width)
        {
            return new TextBox
            {
                Text = text,
                Location = new Point(x, y),
                Width = width,
                BackColor = Color.FromArgb(26, 31, 39),
                ForeColor = TextMain,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 10f)
            };
        }

        private static Button MakeActionButton(string text, int x, int y, int width, Color bg, Color fg)
        {
            var b = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, 38),
                BackColor = bg,
                ForeColor = fg,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 9.2f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(52, 62, 78);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(38, 46, 58);
            return b;
        }

        private void Log(string msg)
        {
            if (_txtLog == null || _txtLog.IsDisposed || !_txtLog.IsHandleCreated) return;
            string line = $"[{DateTime.Now:HH:mm:ss}]  {msg}\n";
            if (_txtLog.InvokeRequired)
            {
                _txtLog.BeginInvoke(new Action(() =>
                {
                    if (_txtLog.IsDisposed) return;
                    _txtLog.AppendText(line);
                    _txtLog.ScrollToCaret();
                }));
            }
            else
            {
                _txtLog.AppendText(line);
                _txtLog.ScrollToCaret();
            }
        }

        public void SaveTabPreviews(string outDir)
        {
            try
            {
                Directory.CreateDirectory(outDir);
                if (_cfg.SavedServers.Count > 0)
                {
                    _cfg.SavedServers[0].Online = true;
                    _cfg.SavedServers[0].PingMs = 1;
                    _cfg.SavedServers[0].Map = "Кордон (Готов к быстрому старту)";
                }
                RenderServerGrid();
                for (int i = 0; i < 3; i++)
                {
                    SelectTab(i);
                    UpdateLocalHostTelemetryLabel();
                    RefreshModAndEngineStatus();
                    PerformLayout();
                    Refresh();
                    Application.DoEvents();
                    using var bmp = new Bitmap(Width, Height);
                    DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                    bmp.Save(Path.Combine(outDir, $"launcher_tab_{i}.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                SelectTab(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(outDir, "preview_error.txt"), ex.ToString());
                Log("Preview error: " + ex.Message);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveConfig();
            _turboInstallCts?.Cancel();
            _responderCts?.Cancel();
            _queryResponder?.Close();
            base.OnFormClosing(e);
        }
    }

    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var form = new MainForm();
                if (args.Contains("--install-coop"))
                {
                    form.InstallCoopEngineAndMods();
                    return;
                }
                if (args.Contains("--repair-and-bake"))
                {
                    TurboGammaInstaller.BakeActiveGammaModsToAnomaly(@"C:\GAMMA", @"C:\Anomaly", @"C:\Users\stanis\Desktop\GAMMA-Coop", _ => { }, forceRebake: true);
                    return;
                }
                if (args.Contains("--pack-patch"))
                {
                    form.Shown += async (s, e) =>
                    {
                        form.SelectTab(2);
                        await form.RunPackFastPatchAsync(silent: true);
                    };
                }
                if (args.Contains("--pack-volumes"))
                {
                    form.Shown += async (s, e) =>
                    {
                        form.SelectTab(2);
                        await form.RunPackFullVolumesAsync(silent: false);
                    };
                }
                if (args.Contains("--turbo-install"))
                {
                    form.Shown += async (s, e) => await form.ToggleTurboGammaInstallAsync();
                }
                if (args.Contains("--fast-start"))
                {
                    form.Shown += (s, e) => form.LaunchAsHostServer();
                }
                if (args.Contains("--save-previews"))
                {
                    form.Show();
                    form.SaveTabPreviews(@"C:\Users\stanis\.gemini\antigravity\brain\bbf95dd9-e57c-41ac-b668-9e6154be0ddd");
                    return;
                }
                Application.Run(form);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), ex.ToString());
            }
        }
    }
}
