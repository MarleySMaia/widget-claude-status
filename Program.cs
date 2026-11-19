using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace UsageWidget;

record Row(string Label, double Used, DateTimeOffset? Reset);
record Section(string Title, List<Row> Rows);
record ProcInfo(string Name, double Cpu, long Mem);
record SysSnap(double Cpu, ulong MemUsed, ulong MemTotal, List<ProcInfo> Procs);

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "UsageWidget.SingleInstance", out bool created);
        if (!created) return;
        // Widget pequeno não precisa de GPU; evita carregar drivers D3D (economiza bastante memória).
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Run(new MainWindow());
    }
}

// ---------- Configurações (salvas em %APPDATA%\UsageWidget\settings.json) ----------
class Settings
{
    public bool Compact { get; set; }
    public bool Claude { get; set; } = true;
    public bool Antigravity { get; set; } = true;
    public bool Sys { get; set; } = true;
    public bool Processes { get; set; } = true;
    public bool SortByMemory { get; set; }
    public int TopN { get; set; } = 5;

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UsageWidget", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

// ---------- Monitor do sistema (CPU, RAM, processos) ----------
class SysMon
{
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)]
    class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);

    long _idle, _kernel, _user;
    Dictionary<int, TimeSpan> _prev = new();
    DateTime _prevAt;

    public SysSnap Sample(bool withProcs)
    {
        GetSystemTimes(out var i, out var k, out var u);
        double cpu = 0;
        long total = (k - _kernel) + (u - _user); // kernel já inclui idle
        if (_kernel != 0 && total > 0) cpu = Math.Clamp((total - (i - _idle)) * 100.0 / total, 0, 100);
        _idle = i; _kernel = k; _user = u;

        var m = new MEMORYSTATUSEX();
        GlobalMemoryStatusEx(m);

        var list = new List<ProcInfo>();
        if (withProcs)
        {
            var now = DateTime.UtcNow;
            double elapsed = (now - _prevAt).TotalMilliseconds * Environment.ProcessorCount;
            bool hasPrev = _prev.Count > 0;
            var cur = new Dictionary<int, TimeSpan>();
            var agg = new Dictionary<string, (double cpu, long mem)>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.Id == 0) continue; // Idle
                        string name = p.ProcessName;
                        long mem = p.WorkingSet64;
                        double c = 0;
                        try
                        {
                            var t = p.TotalProcessorTime;
                            cur[p.Id] = t;
                            if (hasPrev && elapsed > 0 && _prev.TryGetValue(p.Id, out var pt))
                                c = Math.Max(0, (t - pt).TotalMilliseconds * 100 / elapsed);
                        }
                        catch { } // processos protegidos: só memória
                        agg.TryGetValue(name, out var a);
                        agg[name] = (a.cpu + c, a.mem + mem);
                    }
                    catch { }
                }
            }
            _prev = cur; _prevAt = now;
            list = agg.Select(kv => new ProcInfo(kv.Key, kv.Value.cpu, kv.Value.mem)).ToList();
        }
        return new SysSnap(cpu, m.ullTotalPhys - m.ullAvailPhys, m.ullTotalPhys, list);
    }
}

class MainWindow : Window
{
    // ---------- Win32 ----------
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const int WM_HOTKEY = 0x0312, HOTKEY_ID = 0xA11;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);

    // ---------- Estado ----------
    static readonly string PosFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UsageWidget", "pos.txt");
    readonly Settings _s = Settings.Load();
    readonly SysMon _mon = new();
    SysSnap? _snap;
    List<Section> _claude = new(), _agy = new();
    bool _claudeFail, _agyFail, _busy, _sysBusy, _ghost;
    DateTime? _updated;
    IntPtr _hwnd;
    readonly StackPanel _body = new(), _buttons = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock _lock = new(), _gear = new(), _footer = new();
    System.Windows.Forms.NotifyIcon _tray = null!;
    System.Windows.Forms.ToolStripMenuItem _ghostItem = null!;

    public MainWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        var top = new Grid();
        top.Children.Add(new TextBlock { Text = "USO", Foreground = Gray, FontSize = 10, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center });
        StyleButton(_gear, "⚙", "Configurações");
        _gear.MouseLeftButtonDown += (_, e) => { OpenMenu(); e.Handled = true; };
        StyleButton(_lock, "🔓", "Travar/destravar (Ctrl+Alt+U)");
        _lock.MouseLeftButtonDown += (_, e) => { SetGhost(!_ghost); e.Handled = true; };
        _buttons.HorizontalAlignment = HorizontalAlignment.Right;
        _buttons.Children.Add(_gear);
        _buttons.Children.Add(_lock);
        top.Children.Add(_buttons);

        _footer.Foreground = Gray; _footer.FontSize = 9; _footer.Margin = new Thickness(0, 8, 0, 0);

        var root = new StackPanel();
        root.Children.Add(top);
        root.Children.Add(_body);
        root.Children.Add(_footer);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x14, 0x14, 0x18)), // 85%
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            Child = root,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
            BorderThickness = new Thickness(1),
        };
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 11;

        MouseLeftButtonDown += (_, _) => { _anchored = false; try { DragMove(); SavePos(); } catch { } };

        BuildTray();
        Render();
        SetGhost(false);

        var refresh = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        refresh.Tick += async (_, _) => await RefreshAsync();
        refresh.Start();
        var hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        hover.Tick += (_, _) => HoverTick();
        hover.Start();
        var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        tick.Tick += (_, _) => Render();
        tick.Start();
        var sys = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        sys.Tick += async (_, _) => await SampleSysAsync();
        sys.Start();
        Loaded += async (_, _) => { PlaceInitial(); await SampleSysAsync(); await RefreshAsync(); };
    }

    static void StyleButton(TextBlock b, string text, string tip)
    {
        b.Text = text;
        b.Cursor = Cursors.Hand;
        b.FontSize = 12;
        b.Padding = new Thickness(5, 2, 2, 2);
        b.Background = Brushes.Transparent;
        b.ToolTip = tip;
    }

    // ---------- Menu de configuração ----------
    void OpenMenu()
    {
        var cm = new ContextMenu();
        MenuItem Item(string header, bool on, Action change)
        {
            var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = on };
            mi.Click += (_, _) => { change(); _s.Save(); OnSettingsChanged(); };
            return mi;
        }
        MenuItem Title(string t) => new() { Header = t, IsEnabled = false, FontWeight = FontWeights.SemiBold };

        cm.Items.Add(Title("Visão"));
        cm.Items.Add(Item("Resumida", _s.Compact, () => _s.Compact = true));
        cm.Items.Add(Item("Detalhada", !_s.Compact, () => _s.Compact = false));
        cm.Items.Add(new Separator());
        cm.Items.Add(Title("Mostrar"));
        cm.Items.Add(Item("Claude Code", _s.Claude, () => _s.Claude = !_s.Claude));
        cm.Items.Add(Item("Antigravity", _s.Antigravity, () => _s.Antigravity = !_s.Antigravity));
        cm.Items.Add(Item("CPU e memória", _s.Sys, () => _s.Sys = !_s.Sys));
        cm.Items.Add(Item("Processos que mais consomem (detalhada)", _s.Processes, () => _s.Processes = !_s.Processes));
        cm.Items.Add(new Separator());
        cm.Items.Add(Title("Ordenar processos por"));
        cm.Items.Add(Item("CPU", !_s.SortByMemory, () => _s.SortByMemory = false));
        cm.Items.Add(Item("Memória", _s.SortByMemory, () => _s.SortByMemory = true));

        cm.PlacementTarget = _gear;
        cm.Placement = PlacementMode.Bottom;
        cm.IsOpen = true;
    }

    async void OnSettingsChanged()
    {
        Render();
        if ((_s.Claude && _claude.Count == 0) || (_s.Antigravity && _agy.Count == 0)) await RefreshAsync();
        if (_s.Sys) await SampleSysAsync();
    }

    // ---------- Janela / Win32 ----------
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var src = (HwndSource)PresentationSource.FromVisual(this);
        _hwnd = src.Handle;
        src.AddHook(WndProc);
        SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        RegisterHotKey(_hwnd, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x55); // U
        ApplyGhost();
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_HOTKEY && w.ToInt32() == HOTKEY_ID) { SetGhost(!_ghost); handled = true; }
        return IntPtr.Zero;
    }

    void PlaceInitial()
    {
        AnchorRight();
        SizeChanged += (_, _) => { if (_anchored) AnchorRight(); };
        try
        {
            var p = File.ReadAllText(PosFile).Split(';');
            double x = double.Parse(p[0], CultureInfo.InvariantCulture), y = double.Parse(p[1], CultureInfo.InvariantCulture);
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (vs.Contains(new Point(x + 20, y + 20))) { Left = x; Top = y; _anchored = false; }
        }
        catch { }
    }

    bool _anchored = true;

    void AnchorRight()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 16; Top = wa.Top + 16;
    }

    void SavePos()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PosFile)!);
            File.WriteAllText(PosFile, string.Create(CultureInfo.InvariantCulture, $"{Left};{Top}"));
        }
        catch { }
    }

    void SetGhost(bool on)
    {
        _ghost = on;
        _ghostHover = false;
        ApplyGhost();
    }

    bool _ghostHover;

    // No modo fantasma a janela é transparente a cliques, exceto enquanto o cursor
    // estiver sobre os botões (⚙ e cadeado) — assim eles continuam clicáveis.
    void HoverTick()
    {
        if (!_ghost || !IsVisible || _hwnd == IntPtr.Zero) return;
        GetCursorPos(out var c);
        var tl = _buttons.PointToScreen(new Point(0, 0));
        var s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double w = _buttons.ActualWidth * s, h = _buttons.ActualHeight * s;
        bool over = c.X >= tl.X - 4 && c.X <= tl.X + w + 4 && c.Y >= tl.Y - 4 && c.Y <= tl.Y + h + 4;
        if (over != _ghostHover) { _ghostHover = over; ApplyGhost(); }
    }

    void ApplyGhost()
    {
        if (_hwnd != IntPtr.Zero)
        {
            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            ex = _ghost && !_ghostHover ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        }
        _lock.Text = _ghost ? "🔒" : "🔓";
        if (_ghostItem != null) _ghostItem.Checked = _ghost;
    }

    // ---------- Bandeja ----------
    void BuildTray()
    {
        using var bmp = new System.Drawing.Bitmap(16, 16);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.FillRectangle(System.Drawing.Brushes.LimeGreen, 2, 9, 3, 5);
            g.FillRectangle(System.Drawing.Brushes.Gold, 6, 5, 3, 9);
            g.FillRectangle(System.Drawing.Brushes.Tomato, 10, 2, 3, 12);
        }
        var icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Mostrar / ocultar", null, (_, _) => ToggleVisible());
        _ghostItem = new System.Windows.Forms.ToolStripMenuItem("Modo fantasma (Ctrl+Alt+U)");
        _ghostItem.Click += (_, _) => SetGhost(!_ghost);
        menu.Items.Add(_ghostItem);
        menu.Items.Add("Atualizar agora", null, async (_, _) => await RefreshAsync());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Quit());

        _tray = new System.Windows.Forms.NotifyIcon { Icon = icon, Text = "Widget de uso", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ToggleVisible(); };
    }

    void ToggleVisible()
    {
        if (IsVisible) Hide(); else { Show(); SetGhost(_ghost); }
    }

    void Quit()
    {
        SavePos();
        if (_hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, HOTKEY_ID);
        _tray.Visible = false; _tray.Dispose();
        Application.Current.Shutdown();
    }

    // ---------- Atualização ----------
    async Task SampleSysAsync()
    {
        if (!_s.Sys || !IsVisible || _sysBusy) return;
        _sysBusy = true;
        try
        {
            bool procs = _s.Processes && !_s.Compact;
            _snap = await Task.Run(() => _mon.Sample(procs));
            Render();
        }
        catch { }
        finally { _sysBusy = false; }
    }

    async Task RefreshAsync()
    {
        if (_busy || (!_s.Claude && !_s.Antigravity)) return;
        _busy = true;
        try
        {
            var c = _s.Claude ? Fetch(Exe(".local\\bin\\claude.exe", "claude"), "-p \"/usage\"", ParseClaude) : null;
            var a = _s.Antigravity ? Fetch(Exe("AppData\\Local\\agy\\bin\\agy.exe", "agy"), "-p \"/usage\"", ParseAgy) : null;
            if (c != null) { var rc = await c; if (rc != null) _claude = rc; _claudeFail = rc == null; }
            if (a != null) { var ra = await a; if (ra != null) _agy = ra; _agyFail = ra == null; }
            _updated = DateTime.Now;
            Render();
        }
        finally { _busy = false; }
    }

    static string Exe(string relToProfile, string fallback)
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), relToProfile);
        return File.Exists(p) ? p : fallback;
    }

    static async Task<List<Section>?> Fetch(string exe, string args, Func<string, List<Section>> parse)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetTempPath(),
            };
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return null; }
            var res = parse(await so);
            return res.Count > 0 ? res : null;
        }
        catch { return null; }
    }

    // ---------- Parsers ----------
    static List<Section> ParseClaude(string t)
    {
        var rows = new List<Row>();
        var s = ClaudeRow(t, "5h", @"Current session"); if (s != null) rows.Add(s);
        var w = ClaudeRow(t, "Semana", @"Current week \(all models\)"); if (w != null) rows.Add(w);
        return rows.Count > 0 ? new() { new Section("Claude Code", rows) } : new();
    }

    static Row? ClaudeRow(string t, string label, string rx)
    {
        var m = Regex.Match(t, rx + @":\s*(\d+(?:\.\d+)?)%\s*used([^\r\n]*)");
        if (!m.Success) return null;
        double used = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        DateTimeOffset? reset = null;
        var r = Regex.Match(m.Groups[2].Value, @"resets\s+(.+?)\s*(?:\(([^)]+)\))?\s*$");
        if (r.Success) reset = ParseReset(r.Groups[1].Value.Trim(), r.Groups[2].Value.Trim());
        return new Row(label, used, reset);
    }

    static DateTimeOffset? ParseReset(string s, string tz)
    {
        TimeZoneInfo zone;
        try { zone = tz.Length == 0 ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(tz); }
        catch { zone = TimeZoneInfo.Local; }
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        s = Regex.Replace(s, @"\s+", " ");
        s = Regex.Replace(s, @"\s+(am|pm)\b", "$1", RegexOptions.IgnoreCase);
        string[] fmts = { "MMM d, h:mmtt", "MMM d, htt", "MMM d h:mmtt", "MMM d htt", "h:mmtt", "htt" };
        if (!DateTime.TryParseExact(s, fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return null;
        bool hasDate = char.IsLetter(s[0]);
        var local = hasDate ? new DateTime(now.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0)
                            : now.Date + dt.TimeOfDay;
        var res = new DateTimeOffset(local, zone.GetUtcOffset(local));
        if (res < now.AddMinutes(-1)) res = hasDate ? res.AddYears(1) : res.AddDays(1);
        return res;
    }

    static List<Section> ParseAgy(string t)
    {
        var groups = new Dictionary<string, List<Row>>();
        foreach (var line in t.Split('\n'))
        {
            var p = Regex.Split(line.Trim(), @"\t+|\s{2,}");
            if (p.Length < 4) continue;
            var mm = Regex.Match(p[2], @"(\d+(?:\.\d+)?)%");
            if (!mm.Success || !DateTimeOffset.TryParse(p[3], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var reset)) continue;
            string label = p[1].Contains("five", StringComparison.OrdinalIgnoreCase) ? "5h"
                         : p[1].Contains("week", StringComparison.OrdinalIgnoreCase) ? "Semana" : p[1];
            double used = 100 - double.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture);
            if (!groups.TryGetValue(p[0], out var list)) groups[p[0]] = list = new();
            list.Add(new Row(label, used, reset));
        }
        return groups.Select(g => new Section(
            "Antigravity · " + g.Key.Replace(" models", "", StringComparison.OrdinalIgnoreCase).Replace(" and ", " + "),
            g.Value.OrderBy(r => r.Label == "5h" ? 0 : 1).ToList())).ToList();
    }

    // ---------- Desenho ----------
    static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
    static readonly Brush Track = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255));
    const double BarW = 110;

    static Color UsageColor(double used) =>
        used < 50 ? Color.FromRgb(0x4A, 0xDE, 0x80)      // verde
        : used < 80 ? Color.FromRgb(0xFB, 0xBF, 0x24)    // amarelo
        : Color.FromRgb(0xF8, 0x71, 0x71);               // vermelho

    void Render()
    {
        _body.Children.Clear();
        var ai = new List<Section>();
        if (_s.Claude) ai.AddRange(_claude);
        if (_s.Antigravity) ai.AddRange(_agy);
        bool sys = _s.Sys && _snap != null;
        double memPct = sys ? _snap!.MemUsed * 100.0 / Math.Max(1, _snap.MemTotal) : 0;

        if (_s.Compact)
        {
            foreach (var s in ai)
                _body.Children.Add(CompactRow(ShortTitle(s.Title),
                    s.Rows.Select(r => (r.Label == "5h" ? "5h" : "sem", r.Used)).ToList()));
            if (sys)
                _body.Children.Add(CompactRow("PC", new() { ("CPU", _snap!.Cpu), ("RAM", memPct) }));
            if (_body.Children.Count > 0) ((FrameworkElement)_body.Children[0]).Margin = new Thickness(0, 6, 0, 1);
        }
        else
        {
            foreach (var s in ai)
            {
                _body.Children.Add(Header(s.Title));
                foreach (var r in s.Rows)
                    _body.Children.Add(BarRow(r.Label, r.Used, r.Reset == null ? "—" : "↻ " + Remaining(r.Reset.Value)));
            }
            if (sys)
            {
                _body.Children.Add(Header("Sistema"));
                _body.Children.Add(BarRow("CPU", _snap!.Cpu, ""));
                _body.Children.Add(BarRow("RAM", memPct, $"{_snap.MemUsed / GB:0.0}/{_snap.MemTotal / GB:0} GB"));
                if (_s.Processes && _snap.Procs.Count > 0)
                {
                    _body.Children.Add(Header("Processos · por " + (_s.SortByMemory ? "memória" : "CPU")));
                    var top = (_s.SortByMemory ? _snap.Procs.OrderByDescending(p => p.Mem)
                                               : _snap.Procs.OrderByDescending(p => p.Cpu).ThenByDescending(p => p.Mem))
                              .Take(Math.Max(1, _s.TopN));
                    foreach (var p in top) _body.Children.Add(ProcRow(p));
                }
            }
        }

        if (_body.Children.Count == 0)
            _body.Children.Add(new TextBlock { Text = ai.Count == 0 && !_s.Sys ? "Nada habilitado — use ⚙" : "carregando…",
                Foreground = Gray, Margin = new Thickness(0, 6, 0, 0) });

        string status = _updated == null ? "carregando…" : "IA atualizada " + _updated.Value.ToString("HH:mm");
        if (_s.Claude && _claudeFail) status += " · Claude falhou";
        if (_s.Antigravity && _agyFail) status += " · Antigravity falhou";
        _footer.Text = status;
        _footer.Visibility = _s.Compact || (!_s.Claude && !_s.Antigravity) ? Visibility.Collapsed : Visibility.Visible;
    }

    const double GB = 1024.0 * 1024 * 1024;

    static string ShortTitle(string t) => t
        .Replace("Claude Code", "Claude")
        .Replace("Antigravity · ", "AG ")
        .Replace(" + ", "+");

    static TextBlock Header(string text) =>
        new() { Text = text, Foreground = Gray, FontSize = 10, Margin = new Thickness(0, 8, 0, 3) };

    static Border Bar(double used, double width, double height = 6)
    {
        var fill = new Border { Width = Math.Clamp(used, 0, 100) / 100 * width, CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(UsageColor(used)), HorizontalAlignment = HorizontalAlignment.Left };
        return new Border { Width = width, Height = height, CornerRadius = new CornerRadius(3), Background = Track,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Child = fill };
    }

    static Grid Cols(params double[] widths)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        return g;
    }

    static void Put(Grid g, UIElement e, int col) { Grid.SetColumn(e, col); g.Children.Add(e); }

    static TextBlock Pct(double used, double size = 11) => new()
    {
        Text = $"{Math.Round(used)}%", Foreground = new SolidColorBrush(UsageColor(used)), FontWeight = FontWeights.SemiBold,
        FontSize = size, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
    };

    static UIElement BarRow(string label, double used, string right)
    {
        var g = Cols(48, BarW + 8, 36, 62);
        Put(g, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, 0);
        Put(g, Bar(used, BarW), 1);
        Put(g, Pct(used), 2);
        Put(g, new TextBlock { Text = right, Foreground = Gray, FontSize = 10, TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center }, 3);
        return g;
    }

    // Visão resumida: uma linha por fonte, com duas mini barras.
    static UIElement CompactRow(string label, List<(string cap, double used)> pairs)
    {
        var widths = new List<double> { 76 };
        foreach (var _ in pairs) widths.AddRange(new double[] { 26, 30, 30 });
        var g = Cols(widths.ToArray());
        Put(g, new TextBlock { Text = label, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center }, 0);
        int c = 1;
        foreach (var (cap, used) in pairs)
        {
            Put(g, new TextBlock { Text = cap, Foreground = Gray, FontSize = 9, TextAlignment = TextAlignment.Right,
                Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center }, c++);
            Put(g, Bar(used, 30, 5), c++);
            Put(g, Pct(used, 10), c++);
        }
        return g;
    }

    static UIElement ProcRow(ProcInfo p)
    {
        var g = Cols(150, 50, 64);
        Put(g, new TextBlock { Text = p.Name, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center }, 0);
        Put(g, new TextBlock { Text = $"{p.Cpu:0.0}%", FontSize = 10, TextAlignment = TextAlignment.Right,
            Foreground = new SolidColorBrush(UsageColor(p.Cpu)), VerticalAlignment = VerticalAlignment.Center }, 1);
        Put(g, new TextBlock { Text = FormatMem(p.Mem), FontSize = 10, Foreground = Gray, TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center }, 2);
        return g;
    }

    static string FormatMem(long b) => b >= GB ? $"{b / GB:0.0} GB" : $"{b / (1024.0 * 1024):0} MB";

    static string Remaining(DateTimeOffset reset)
    {
        var d = reset - DateTimeOffset.UtcNow;
        if (d <= TimeSpan.Zero) return "agora";
        if (d.TotalDays >= 1) return $"{(int)d.TotalDays}d {d.Hours}h";
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes:00}m";
        return $"{Math.Max(1, d.Minutes)}m";
    }
}
