// QuickSidebar —— 桌面右侧快速启动侧栏（原生 WPF，.NET Framework 4.8）
// 上 5 格：最近访问（自动追踪前台窗口，MRU）；下 5 格：快捷访问（手动固定）。
// 视觉与动效遵循 emilkowalski/skills 的设计工程规范：
//   强 ease-out 曲线（ExponentialEase）、按压 scale 0.94~0.96、进入从 scale 0.96 起、
//   退出比进入快、错峰动画 28ms、只动 transform/opacity。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace QuickSidebar
{
    // ============================ 配置持久化 ============================

    public class Config
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public bool Minimized { get; set; }
        public List<string> Recents { get; set; }
        public List<string> Quick { get; set; }
        public Config()
        {
            Left = -9999; Top = -9999;
            Recents = new List<string>();
            Quick = new List<string>();
        }
    }

    public static class Store
    {
        static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickSidebar"); }
        }
        static string FilePath
        {
            get { return Path.Combine(Dir, "config.json"); }
        }

        public static Config Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    Config c = ser.Deserialize<Config>(File.ReadAllText(FilePath));
                    if (c != null) return c;
                }
            }
            catch { }
            return new Config();
        }

        public static void Save(Config c)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(c);
                // 原子写入：先写临时文件再覆盖，避免进程被杀时留下截断的 JSON
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Copy(tmp, FilePath, true);
                File.Delete(tmp);
            }
            catch { }
        }
    }

    // ============================ 应用信息 / 图标 ============================

    public class AppInfo
    {
        public string FullPath;
        public string Name;
        public ImageSource Icon;
    }

    public static class Icons
    {
        static Dictionary<string, AppInfo> cache = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);

        public static AppInfo Get(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (cache)
            {
                AppInfo hit;
                if (cache.TryGetValue(path, out hit)) return hit;
            }
            AppInfo info = new AppInfo();
            info.FullPath = path;
            try { info.Name = DisplayName(path); } catch { }
            if (string.IsNullOrEmpty(info.Name)) info.Name = Path.GetFileNameWithoutExtension(path);
            info.Icon = GetIcon(path);
            lock (cache) { cache[path] = info; }
            return info;
        }

        static string DisplayName(string path)
        {
            Native.SHFILEINFO sfi = new Native.SHFILEINFO();
            IntPtr res = Native.SHGetFileInfo(path, 0, ref sfi, (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO)), Native.SHGFI_DISPLAYNAME);
            if (res != IntPtr.Zero && !string.IsNullOrEmpty(sfi.szDisplayName)) return sfi.szDisplayName;
            return Path.GetFileNameWithoutExtension(path);
        }

        static ImageSource GetIcon(string path)
        {
            // 高分辨率图标：IShellItemImageFactory（对 exe/lnk 都有效）
            try
            {
                Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                IShellItemImageFactory factory;
                Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
                IntPtr hbm;
                Native.SIZE sz = new Native.SIZE();
                sz.cx = 96; sz.cy = 96;
                int hr = factory.GetImage(sz, Native.SIIGBF_ICONONLY | Native.SIIGBF_BIGGERSIZEOK, out hbm);
                if (hr == 0 && hbm != IntPtr.Zero)
                {
                    BitmapSource bs = HBitmapToPbgra(hbm);
                    Native.DeleteObject(hbm);
                    if (bs != null) return bs;
                }
            }
            catch { }
            // 兜底：SHGetFileInfo 大图标
            try
            {
                Native.SHFILEINFO sfi = new Native.SHFILEINFO();
                IntPtr res = Native.SHGetFileInfo(path, 0x80, ref sfi, (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO)), Native.SHGFI_ICON | Native.SHGFI_LARGEICON);
                if (res != IntPtr.Zero && sfi.hIcon != IntPtr.Zero)
                {
                    BitmapSource bs = Imaging.CreateBitmapSourceFromHIcon(sfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    Native.DestroyIcon(sfi.hIcon);
                    return bs;
                }
            }
            catch { }
            return null;
        }

        // HBITMAP → 预乘 alpha 的 Pbgra32，避免透明区域发黑
        static BitmapSource HBitmapToPbgra(IntPtr hbm)
        {
            Native.BITMAP bm = new Native.BITMAP();
            if (Native.GetObject(hbm, Marshal.SizeOf(typeof(Native.BITMAP)), ref bm) == 0) return null;
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            if (w <= 0 || h <= 0) return null;
            Native.BITMAPINFO bi = new Native.BITMAPINFO();
            bi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            bi.bmiHeader.biWidth = w;
            bi.bmiHeader.biHeight = -h;          // top-down
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = 0;      // BI_RGB
            byte[] buf = new byte[w * h * 4];
            IntPtr dc = Native.CreateCompatibleDC(IntPtr.Zero);
            try
            {
                if (Native.GetDIBits(dc, hbm, 0, (uint)h, buf, ref bi, 0) == 0) return null;
            }
            finally { Native.DeleteDC(dc); }
            byte[] outBuf = new byte[buf.Length];
            for (int i = 0; i < buf.Length; i += 4)
            {
                byte b = buf[i], g = buf[i + 1], r = buf[i + 2], a = buf[i + 3];
                outBuf[i] = (byte)((b * a + 127) / 255);
                outBuf[i + 1] = (byte)((g * a + 127) / 255);
                outBuf[i + 2] = (byte)((r * a + 127) / 255);
                outBuf[i + 3] = a;
            }
            return BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, outBuf, w * 4);
        }
    }

    // ============================ Win32 ============================

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        // 原生签名：GetImage(SIZE size, SIIGBF flags, HBITMAP* phbm) —— SIZE 按值传递，必须声明为结构体
        [PreserveSig] int GetImage(Native.SIZE size, uint flags, out IntPtr phbm);
    }

    internal static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("kernel32.dll")] internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        internal static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")] internal static extern int GetObject(IntPtr hgdiobj, int cb, ref BITMAP lpObject);
        [DllImport("gdi32.dll")] internal static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFO bi, uint usage);

        [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        internal const uint SHGFI_ICON = 0x100;
        internal const uint SHGFI_LARGEICON = 0x0;
        internal const uint SHGFI_DISPLAYNAME = 0x200;
        internal const uint SIIGBF_ICONONLY = 0x4;
        internal const uint SIIGBF_BIGGERSIZEOK = 0x1;
        internal const int GWL_EXSTYLE = -20;
        internal const int WS_EX_TOOLWINDOW = 0x80;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BITMAP
        {
            public int bmType; public int bmWidth; public int bmHeight; public int bmWidthBytes;
            public ushort bmPlanes; public ushort bmBitsPixel; public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount;
            public uint biCompression; public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
            public uint biClrUsed; public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SIZE { public int cx; public int cy; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int left; public int top; public int right; public int bottom; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }
    }

    // ============================ 视觉工具 ============================

    internal static class Ui
    {
        public static SolidColorBrush Brush(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        // Emil 规范：强 ease-out（≈ cubic-bezier(0.23, 1, 0.32, 1)）
        public static ExponentialEase Out
        {
            get { return new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut }; }
        }
    }

    // ============================ 槽位控件 ============================

    public class Slot : Border
    {
        public string AppPath;
        public bool Interactive = true;
        public Action Activate;             // 左键点击行为（由 App 注入）
        Image icon;
        Ellipse runDot;
        FrameworkElement mark;              // 空位占位符
        Grid host;
        ScaleTransform scaleT;
        TranslateTransform transT;
        SolidColorBrush bgBrush;
        bool pressed;

        public Slot(double size)
        {
            Width = size; Height = size;
            CornerRadius = new CornerRadius(12);
            bgBrush = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255));
            Background = bgBrush;

            TransformGroup tg = new TransformGroup();
            scaleT = new ScaleTransform(1, 1);
            transT = new TranslateTransform(0, 0);
            tg.Children.Add(scaleT);
            tg.Children.Add(transT);
            RenderTransform = tg;
            RenderTransformOrigin = new Point(0.5, 0.5);
            Cursor = Cursors.Hand;
            Margin = new Thickness(0, 3, 0, 5);

            double iconSize = Math.Round(size * 0.58);
            host = new Grid();
            icon = new Image
            {
                Width = iconSize,
                Height = iconSize,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, Math.Round(size * 0.13), 0, 0),
                Visibility = Visibility.Hidden
            };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            runDot = new Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = Ui.Brush("#8CFFFFFF"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 6),
                Visibility = Visibility.Hidden
            };
            host.Children.Add(icon);
            host.Children.Add(runDot);
            Child = host;

            MouseEnter += OnEnter;
            MouseLeave += OnLeave;
            MouseLeftButtonDown += OnDown;
            MouseLeftButtonUp += OnUp;
            MouseRightButtonUp += OnRightUp;
            ToolTipService.SetInitialShowDelay(this, 420);
        }

        void OnRightUp(object sender, MouseButtonEventArgs e)
        {
            if (ContextMenu == null) return;
            ContextMenu.PlacementTarget = this;
            ContextMenu.Placement = PlacementMode.MousePoint;
            ContextMenu.IsOpen = true;
            e.Handled = true;
        }

        public void SetApp(AppInfo info)
        {
            AppPath = info == null ? null : info.FullPath;
            ClearMark();
            if (info == null) { icon.Visibility = Visibility.Hidden; ToolTip = null; return; }
            if (info.Icon != null)
            {
                icon.Source = info.Icon;
                icon.Visibility = Visibility.Visible;
            }
            else
            {
                // 图标提取失败的兜底：首字母圆片
                TextBlock letter = new TextBlock
                {
                    Text = string.IsNullOrEmpty(info.Name) ? "?" : info.Name.Substring(0, 1).ToUpperInvariant(),
                    FontSize = 16,
                    FontWeight = FontWeights.Medium,
                    Foreground = Ui.Brush("#C0FFFFFF"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 12, 0, 0)
                };
                SetMark(letter);
                icon.Visibility = Visibility.Hidden;
            }
            ToolTip = info.Name;
        }

        public void SetEmpty(string kind)
        {
            AppPath = null;
            icon.Source = null;
            icon.Visibility = Visibility.Hidden;
            runDot.Visibility = Visibility.Hidden;
            ClearMark();
            if (kind == "quick")
            {
                ToolTip = "添加应用";
                TextBlock plus = new TextBlock
                {
                    Text = "+",
                    FontSize = 20,
                    FontWeight = FontWeights.Light,
                    Foreground = Ui.Brush("#59FFFFFF"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -3, 0, 0)
                };
                SetMark(plus);
            }
            else
            {
                ToolTip = null;
                Ellipse dim = new Ellipse
                {
                    Width = 4,
                    Height = 4,
                    Fill = Ui.Brush("#2EFFFFFF"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                SetMark(dim);
            }
        }

        public void SetRunning(bool on)
        {
            runDot.Visibility = (on && AppPath != null) ? Visibility.Visible : Visibility.Hidden;
        }

        public void SetInteractive(bool on)
        {
            Interactive = on;
            Cursor = on ? Cursors.Hand : Cursors.Arrow;
        }


        void SetMark(FrameworkElement el) { mark = el; host.Children.Add(el); }
        void ClearMark() { if (mark != null) { host.Children.Remove(mark); mark = null; } }

        void OnEnter(object sender, MouseEventArgs e)
        {
            if (!Interactive) return;
            AnimateBg(0x14);
            AnimateScale(1.04, 120);
        }

        void OnLeave(object sender, MouseEventArgs e)
        {
            AnimateBg(0);
            AnimateScale(1, 150);
            pressed = false;
        }

        void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (!Interactive || e.ChangedButton != MouseButton.Left) return;
            pressed = true;
            CaptureMouse();
            AnimateScale(0.94, 110);     // 按压反馈：略低于 0.95 下限也可，保持干脆
            e.Handled = true;
        }

        void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!Interactive || !pressed) return;
            pressed = false;
            ReleaseMouseCapture();
            AnimateScale(1, 150);
            if (IsMouseOver && Activate != null) Activate();
            e.Handled = true;
        }

        void AnimateBg(byte alpha)
        {
            ColorAnimation ca = new ColorAnimation();
            ca.To = Color.FromArgb(alpha, 255, 255, 255);
            ca.Duration = new Duration(TimeSpan.FromMilliseconds(120));
            ca.EasingFunction = Ui.Out;
            bgBrush.BeginAnimation(SolidColorBrush.ColorProperty, ca);
        }

        void AnimateScale(double to, int ms)
        {
            DoubleAnimation dx = new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(ms)));
            dx.EasingFunction = Ui.Out;
            scaleT.BeginAnimation(ScaleTransform.ScaleXProperty, dx);
            DoubleAnimation dy = new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(ms)));
            dy.EasingFunction = Ui.Out;
            scaleT.BeginAnimation(ScaleTransform.ScaleYProperty, dy);
        }
    }

    // .lnk 解析结果（WScript.Shell COM，按快捷方式路径缓存）
    public class LnkInfo
    {
        public string Target;       // 目标 exe 全路径；指向非 exe / 解析失败时为 null
        public string Arguments;
    }

    // ============================ 主程序 ============================

    public class Program
    {
        const double PAD = 36;          // 窗口透明边距（容纳阴影）
        const double BUB = 52;          // 最小化圆圈直径
        const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        const string RunKeyName = "QuickSidebar";

        double PANEL_W = 84;
        double SLOT = 52;
        bool compact;

        Config cfg;
        List<string> recents;
        bool minimized;
        bool dirty;
        bool entrancePlayed;
        int tick;
        IntPtr lastHwnd = IntPtr.Zero;
        HashSet<string> runningNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, LnkInfo> lnkCache = new Dictionary<string, LnkInfo>(StringComparer.OrdinalIgnoreCase);

        Application app;
        Window panel, bubble;
        Border card, bubbleCard;
        ScaleTransform cardScale, bubbleScale;
        Border minBtn;
        Slot[] recSlots;
        Slot[] qSlots;
        DispatcherTimer tracker, saveTimer;

        [STAThread]
        public static void Main()
        {
            bool createdNew;
            Mutex mutex = new Mutex(true, "Local\\QuickSidebar.Instance", out createdNew);
            if (!createdNew) return;
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                LogCrash(e.ExceptionObject as Exception);
            };
            try
            {
                new Program().Run();
            }
            catch (Exception ex)
            {
                LogCrash(ex);
            }
        }

        static void LogCrash(Exception ex)
        {
            LogLine("CRASH " + ex);
        }

        internal static void LogLine(string msg)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickSidebar");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"), DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        void Run()
        {
            app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                try { Directory.CreateDirectory(StoreDir()); File.AppendAllText(Path.Combine(StoreDir(), "error.log"), DateTime.Now + " " + e.Exception + Environment.NewLine); } catch { }
                e.Handled = true;
            };
            app.Exit += delegate { SaveNow(); };

            cfg = Store.Load();
            NormalizeQuick();
            recents = cfg.Recents != null ? new List<string>(cfg.Recents) : new List<string>();
            if (recents.Count > 5) recents.RemoveRange(5, recents.Count - 5);

            double waH = SystemParameters.WorkArea.Height;
            compact = waH < 820;
            SLOT = compact ? 44 : 52;
            PANEL_W = compact ? 76 : 84;

            BuildStyles();
            BuildPanel();
            BuildBubble();
            RefreshRecents();
            RefreshQuick();
            StartTimers();

            if (cfg.Minimized)
            {
                minimized = true;
                if (cfg.Left > -9000) { panel.Left = cfg.Left; panel.Top = cfg.Top; }
                ClampWindow(panel);
                PositionBubbleAtMinButton();
                bubbleCard.Opacity = 0;
                bubbleScale.ScaleX = bubbleScale.ScaleY = 0.9;
                bubble.Show();
                AnimateBubbleIn(230);
            }
            else
            {
                panel.Show();
            }

            app.Run();
        }

        static string StoreDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickSidebar");
        }

        // ---------------- 深色样式（ToolTip / ContextMenu / MenuItem / Separator） ----------------

        void BuildStyles()
        {
            string dict =
                "<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
                  "<Style TargetType='ToolTip'>" +
                    "<Setter Property='Background' Value='#F01D1D20'/>" +
                    "<Setter Property='Foreground' Value='#E8FFFFFF'/>" +
                    "<Setter Property='BorderBrush' Value='#2EFFFFFF'/>" +
                    "<Setter Property='BorderThickness' Value='1'/>" +
                    "<Setter Property='FontSize' Value='12'/>" +
                    "<Setter Property='Padding' Value='10,6'/>" +
                    "<Setter Property='HasDropShadow' Value='False'/>" +
                    "<Setter Property='Template'>" +
                      "<Setter.Value>" +
                        "<ControlTemplate TargetType='ToolTip'>" +
                          "<Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='7' Padding='{TemplateBinding Padding}'>" +
                            "<ContentPresenter/>" +
                          "</Border>" +
                        "</ControlTemplate>" +
                      "</Setter.Value>" +
                    "</Setter>" +
                  "</Style>" +
                  "<Style TargetType='ContextMenu'>" +
                    "<Setter Property='Background' Value='#F51B1B1E'/>" +
                    "<Setter Property='BorderBrush' Value='#2EFFFFFF'/>" +
                    "<Setter Property='BorderThickness' Value='1'/>" +
                    "<Setter Property='Foreground' Value='#E8FFFFFF'/>" +
                    "<Setter Property='FontSize' Value='12'/>" +
                    "<Setter Property='Padding' Value='5'/>" +
                    "<Setter Property='HasDropShadow' Value='False'/>" +
                    "<Setter Property='FontFamily' Value='Segoe UI, Microsoft YaHei UI'/>" +
                    "<Setter Property='Template'>" +
                      "<Setter.Value>" +
                        "<ControlTemplate TargetType='ContextMenu'>" +
                          "<Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='9' Padding='5'>" +
                            "<StackPanel IsItemsHost='True'/>" +
                          "</Border>" +
                        "</ControlTemplate>" +
                      "</Setter.Value>" +
                    "</Setter>" +
                  "</Style>" +
                  "<Style TargetType='MenuItem'>" +
                    "<Setter Property='Foreground' Value='#E8FFFFFF'/>" +
                    "<Setter Property='FontSize' Value='12'/>" +
                    "<Setter Property='Cursor' Value='Hand'/>" +
                    "<Setter Property='Template'>" +
                      "<Setter.Value>" +
                        "<ControlTemplate TargetType='MenuItem'>" +
                          "<Border x:Name='bd' CornerRadius='6' Background='Transparent' Padding='10,6'>" +
                            "<ContentPresenter ContentSource='Header' RecognizesAccessKey='True'/>" +
                          "</Border>" +
                          "<ControlTemplate.Triggers>" +
                            "<Trigger Property='IsHighlighted' Value='True'>" +
                              "<Setter TargetName='bd' Property='Background' Value='#16FFFFFF'/>" +
                            "</Trigger>" +
                            "<Trigger Property='IsEnabled' Value='False'>" +
                              "<Setter Property='Opacity' Value='0.38'/>" +
                            "</Trigger>" +
                          "</ControlTemplate.Triggers>" +
                        "</ControlTemplate>" +
                      "</Setter.Value>" +
                    "</Setter>" +
                  "</Style>" +
                  "<Style TargetType='Separator'>" +
                    "<Setter Property='Template'>" +
                      "<Setter.Value>" +
                        "<ControlTemplate TargetType='Separator'>" +
                          "<Border Height='1' Background='#14FFFFFF' Margin='6,4'/>" +
                        "</ControlTemplate>" +
                      "</Setter.Value>" +
                    "</Setter>" +
                  "</Style>" +
                "</ResourceDictionary>";
            app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(dict));
        }

        // ---------------- 面板 ----------------

        void BuildPanel()
        {
            panel = new Window();
            panel.AllowsTransparency = true;
            panel.WindowStyle = WindowStyle.None;
            panel.ResizeMode = ResizeMode.NoResize;
            panel.ShowInTaskbar = false;
            panel.ShowActivated = false;
            panel.Topmost = true;
            panel.Background = Brushes.Transparent;
            panel.SizeToContent = SizeToContent.Height;
            panel.Width = PAD * 2 + PANEL_W;
            panel.FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            panel.SourceInitialized += delegate
            {
                IntPtr h = new WindowInteropHelper(panel).Handle;
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW);   // 不进 Alt+Tab
            };
            panel.Loaded += delegate
            {
                if (cfg.Left <= -9000)
                {
                    Rect wa = SystemParameters.WorkArea;
                    panel.Left = wa.Right - panel.ActualWidth - 12;
                    panel.Top = wa.Top + Math.Max(10, (wa.Height - panel.ActualHeight) / 2);
                }
                else
                {
                    panel.Left = cfg.Left;
                    panel.Top = cfg.Top;
                }
                ClampWindow(panel);
                if (!entrancePlayed) { entrancePlayed = true; PlayEntrance(); }
            };

            Grid root = new Grid();
            root.Margin = new Thickness(PAD);

            card = new Border();
            card.CornerRadius = new CornerRadius(18);
            card.BorderThickness = new Thickness(1);
            card.BorderBrush = Ui.Brush("#24FFFFFF");      // Emil：半透明描边优于实线
            LinearGradientBrush bg = new LinearGradientBrush();
            bg.StartPoint = new Point(0, 0);
            bg.EndPoint = new Point(0, 1);
            bg.GradientStops.Add(new GradientStop(Color.FromArgb(0xF2, 0x1A, 0x1A, 0x1D), 0));
            bg.GradientStops.Add(new GradientStop(Color.FromArgb(0xEE, 0x10, 0x10, 0x13), 1));
            card.Background = bg;
            card.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 30,
                ShadowDepth = 6,
                Direction = 270,
                Opacity = 0.5
            };
            cardScale = new ScaleTransform(1, 1);
            card.RenderTransform = cardScale;
            card.RenderTransformOrigin = new Point(0.9, 0.05);   // 从收起按钮处展开
            card.MouseRightButtonUp += delegate { ShowHeaderMenu(); };

            // 顶部 1px 高光
            Border hl = new Border();
            hl.Height = 1;
            hl.Margin = new Thickness(1, 1, 1, 0);
            hl.VerticalAlignment = VerticalAlignment.Top;
            hl.CornerRadius = new CornerRadius(18, 18, 0, 0);
            hl.IsHitTestVisible = false;
            LinearGradientBrush hlg = new LinearGradientBrush();
            hlg.StartPoint = new Point(0, 0);
            hlg.EndPoint = new Point(1, 0);
            hlg.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0));
            hlg.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, 255, 255, 255), 0.5));
            hlg.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 1));
            hl.Background = hlg;

            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(6, 0, 6, 10);

            // 头部：拖动区 + 收起按钮
            Grid header = new Grid();
            header.Height = compact ? 34.0 : 38.0;
            header.Background = Brushes.Transparent;
            header.Cursor = Cursors.SizeAll;
            header.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ChangedButton == MouseButton.Left)
                {
                    try { panel.DragMove(); ClampWindow(panel); dirty = true; } catch { }
                }
            };
            header.MouseRightButtonUp += delegate { ShowHeaderMenu(); };

            header.Children.Add(BuildGrip());

            minBtn = new Border();
            minBtn.Width = 28; minBtn.Height = 28;
            minBtn.CornerRadius = new CornerRadius(8);
            minBtn.Background = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255));
            minBtn.HorizontalAlignment = HorizontalAlignment.Right;
            minBtn.VerticalAlignment = VerticalAlignment.Center;
            minBtn.Margin = new Thickness(0, 0, 4, 0);
            minBtn.Cursor = Cursors.Hand;
            minBtn.ToolTip = "收起";
            ScaleTransform minScale = new ScaleTransform(1, 1);
            minBtn.RenderTransform = minScale;
            minBtn.RenderTransformOrigin = new Point(0.5, 0.5);
            TextBlock chev = new TextBlock
            {
                Text = "\uE76B",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = Ui.Brush("#A6FFFFFF"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            minBtn.Child = chev;
            minBtn.MouseEnter += delegate { AnimateSolid(minBtn.Background, 0x1A); };
            minBtn.MouseLeave += delegate { AnimateSolid(minBtn.Background, 0x00); };
            minBtn.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                AnimateScaleOn(minScale, 0.9, 110);
            };
            minBtn.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                AnimateScaleOn(minScale, 1, 150);
                Minimize();
            };
            header.Children.Add(minBtn);

            sp.Children.Add(header);
            sp.Children.Add(MakeLabel("最近访问"));

            recSlots = new Slot[5];
            for (int i = 0; i < 5; i++)
            {
                int idx = i;
                Slot s = new Slot(SLOT);
                s.Activate = delegate { string p = s.AppPath; if (!string.IsNullOrEmpty(p)) LaunchPath(p); };
                recSlots[idx] = s;
                sp.Children.Add(s);
            }

            Border sep = new Border
            {
                Height = 1,
                Background = Ui.Brush("#14FFFFFF"),
                Margin = new Thickness(8, 6, 8, 2),
                Opacity = 0.9
            };
            sp.Children.Add(sep);
            sp.Children.Add(MakeLabel("快捷访问"));

            qSlots = new Slot[5];
            for (int i = 0; i < 5; i++)
            {
                int idx = i;
                Slot s = new Slot(SLOT);
                s.Activate = delegate
                {
                    string p = s.AppPath;
                    if (string.IsNullOrEmpty(p)) PickApp(idx); else LaunchPath(p);
                };
                qSlots[idx] = s;
                sp.Children.Add(s);
            }

            card.Child = sp;
            root.Children.Add(card);
            root.Children.Add(hl);
            panel.Content = root;
        }

        Border BuildGrip()
        {
            Grid g = new Grid();
            g.HorizontalAlignment = HorizontalAlignment.Left;
            g.VerticalAlignment = VerticalAlignment.Center;
            g.Margin = new Thickness(8, 0, 0, 0);
            g.IsHitTestVisible = false;
            for (int col = 0; col < 2; col++)
            {
                for (int row = 0; row < 3; row++)
                {
                    Ellipse dot = new Ellipse
                    {
                        Width = 3,
                        Height = 3,
                        Fill = Ui.Brush("#38FFFFFF"),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(col * 7, row * 7, 0, 0)
                    };
                    g.Children.Add(dot);
                }
            }
            g.Width = 10;
            g.Height = 17;
            return new Border { Child = g };
        }

        TextBlock MakeLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 10.5,
                Foreground = Ui.Brush("#66FFFFFF"),
                Margin = new Thickness(8, 7, 0, 1),
                IsHitTestVisible = false
            };
        }

        // ---------------- 圆圈 ----------------

        void BuildBubble()
        {
            bubble = new Window();
            bubble.AllowsTransparency = true;
            bubble.WindowStyle = WindowStyle.None;
            bubble.ResizeMode = ResizeMode.NoResize;
            bubble.ShowInTaskbar = false;
            bubble.ShowActivated = false;
            bubble.Topmost = true;
            bubble.Background = Brushes.Transparent;
            bubble.Width = PAD * 2 + BUB;
            bubble.Height = PAD * 2 + BUB;
            bubble.SourceInitialized += delegate
            {
                IntPtr h = new WindowInteropHelper(bubble).Handle;
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW);
            };

            Grid root = new Grid();
            root.Margin = new Thickness(PAD);

            bubbleCard = new Border();
            bubbleCard.Width = BUB;
            bubbleCard.Height = BUB;
            bubbleCard.CornerRadius = new CornerRadius(BUB / 2);
            bubbleCard.BorderThickness = new Thickness(1);
            bubbleCard.BorderBrush = Ui.Brush("#24FFFFFF");
            bubbleCard.Background = Ui.Brush("#F2151517");
            bubbleCard.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 24,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.55
            };
            bubbleScale = new ScaleTransform(1, 1);
            bubbleCard.RenderTransform = bubbleScale;
            bubbleCard.RenderTransformOrigin = new Point(0.5, 0.5);
            bubbleCard.Cursor = Cursors.Hand;
            bubbleCard.ToolTip = "展开";

            UniformGrid ug = new UniformGrid { Rows = 2, Columns = 2, Width = 26, Height = 26 };
            for (int i = 0; i < 4; i++)
            {
                ug.Children.Add(new Ellipse
                {
                    Width = 5,
                    Height = 5,
                    Fill = Ui.Brush("#B8FFFFFF"),
                    Margin = new Thickness(4)
                });
            }
            bubbleCard.Child = ug;

            bubbleCard.MouseEnter += delegate { AnimateScaleOn(bubbleScale, 1.07, 130); };
            bubbleCard.MouseLeave += delegate { AnimateScaleOn(bubbleScale, 1, 150); };
            bubbleCard.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                AnimateScaleOn(bubbleScale, 0.92, 110);
            };
            bubbleCard.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                AnimateScaleOn(bubbleScale, 1, 150);
                Expand();
            };

            root.Children.Add(bubbleCard);
            bubble.Content = root;
        }

        // ---------------- 刷新 UI ----------------

        void RefreshRecents()
        {
            for (int i = 0; i < 5; i++)
            {
                Slot s = recSlots[i];
                string p = (recents != null && i < recents.Count) ? recents[i] : null;
                if (string.IsNullOrEmpty(p))
                {
                    s.SetEmpty("recent");
                    s.ContextMenu = null;
                    s.SetInteractive(false);
                }
                else
                {
                    AppInfo info = Icons.Get(p);
                    s.SetApp(info);
                    s.ContextMenu = BuildRecentMenu(info);
                    s.SetInteractive(true);
                }
            }
            UpdateDots();
        }

        void RefreshQuick()
        {
            for (int i = 0; i < 5; i++)
            {
                Slot s = qSlots[i];
                string p = (cfg.Quick != null && i < cfg.Quick.Count) ? cfg.Quick[i] : null;
                if (string.IsNullOrEmpty(p))
                {
                    s.SetEmpty("quick");
                    s.ContextMenu = BuildQuickEmptyMenu(i);
                }
                else
                {
                    AppInfo info = Icons.Get(p);
                    s.SetApp(info);
                    s.ContextMenu = BuildQuickMenu(info, i);
                }
                s.SetInteractive(true);
            }
            UpdateDots();
        }

        void UpdateDots()
        {
            for (int i = 0; i < 5; i++)
            {
                recSlots[i].SetRunning(IsRunning(recSlots[i].AppPath));
                qSlots[i].SetRunning(IsRunning(qSlots[i].AppPath));
            }
        }

        bool IsRunning(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string name = Path.GetFileNameWithoutExtension(path);
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                LnkInfo li = ResolveLnk(path);
                if (li != null && li.Target != null) name = Path.GetFileNameWithoutExtension(li.Target);
            }
            return runningNames.Contains(name);
        }

        // ---------------- 菜单 ----------------

        ContextMenu BuildRecentMenu(AppInfo info)
        {
            ContextMenu m = new ContextMenu();
            string p = info == null ? null : info.FullPath;
            MenuItem open = new MenuItem { Header = "打开" };
            open.Click += delegate { if (!string.IsNullOrEmpty(p)) LaunchPath(p); };
            m.Items.Add(open);
            MenuItem pin = new MenuItem { Header = "固定到快捷访问" };
            pin.IsEnabled = !string.IsNullOrEmpty(p) && FirstFreeQuick() >= 0;
            pin.Click += delegate
            {
                int i = FirstFreeQuick();
                if (i >= 0 && !string.IsNullOrEmpty(p))
                {
                    cfg.Quick[i] = p;
                    RefreshQuick();
                    SaveNow();
                }
            };
            m.Items.Add(pin);
            return m;
        }

        ContextMenu BuildQuickMenu(AppInfo info, int index)
        {
            ContextMenu m = new ContextMenu();
            string p = info == null ? null : info.FullPath;
            MenuItem open = new MenuItem { Header = "打开" };
            open.Click += delegate { if (!string.IsNullOrEmpty(p)) LaunchPath(p); };
            m.Items.Add(open);
            MenuItem change = new MenuItem { Header = "更换应用…" };
            change.Click += delegate { PickApp(index); };
            m.Items.Add(change);
            m.Items.Add(new Separator());
            MenuItem remove = new MenuItem { Header = "移除" };
            remove.Click += delegate { cfg.Quick[index] = null; RefreshQuick(); SaveNow(); };
            m.Items.Add(remove);
            return m;
        }

        ContextMenu BuildQuickEmptyMenu(int index)
        {
            ContextMenu m = new ContextMenu();
            MenuItem add = new MenuItem { Header = "选择应用…" };
            add.Click += delegate { PickApp(index); };
            m.Items.Add(add);
            return m;
        }

        void ShowHeaderMenu()
        {
            ContextMenu m = new ContextMenu();
            bool autoOn = AutoStartEnabled();
            MenuItem auto = new MenuItem { Header = (autoOn ? "✓ " : "") + "开机自启" };
            auto.Click += delegate { SetAutoStart(!AutoStartEnabled()); };
            m.Items.Add(auto);
            m.Items.Add(new Separator());
            MenuItem quit = new MenuItem { Header = "退出" };
            quit.Click += delegate { app.Shutdown(); };
            m.Items.Add(quit);
            m.PlacementTarget = panel;
            m.Placement = PlacementMode.MousePoint;
            m.IsOpen = true;
        }

        int FirstFreeQuick()
        {
            for (int i = 0; i < 5; i++)
            {
                if (i >= cfg.Quick.Count || string.IsNullOrEmpty(cfg.Quick[i])) return i;
            }
            return -1;
        }

        // ---------------- 启动 / 激活 ----------------

        void LaunchPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                string lower = path.ToLowerInvariant();
                string fileName = Path.GetFileName(lower);
                if (lower.EndsWith(".url") || lower.EndsWith(".bat") || fileName == "explorer.exe")
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    return;
                }
                IntPtr hwnd = IntPtr.Zero;
                if (lower.EndsWith(".lnk"))
                {
                    // 快捷方式：解析出目标 exe，已运行则调出；带参数或指向 explorer 的仍直接启动
                    LnkInfo li = ResolveLnk(path);
                    if (li != null && li.Target != null && string.IsNullOrEmpty(li.Arguments)
                        && !string.Equals(Path.GetFileName(li.Target), "explorer.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        hwnd = FindRunningWindow(li.Target);
                    }
                }
                else
                {
                    hwnd = FindRunningWindow(path);
                }
                if (hwnd != IntPtr.Zero)
                {
                    ActivateWindow(hwnd);
                    return;
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
            }
        }

        // 解析 .lnk（结果缓存）。Target 为 null 表示目标不是 exe 或解析失败，此时始终直接启动。
        LnkInfo ResolveLnk(string lnkPath)
        {
            LnkInfo hit;
            if (lnkCache.TryGetValue(lnkPath, out hit)) return hit;
            LnkInfo info = new LnkInfo();
            try
            {
                object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                object sc = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                string target = (string)sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null);
                string args = (string)sc.GetType().InvokeMember("Arguments", BindingFlags.GetProperty, null, sc, null);
                if (!string.IsNullOrEmpty(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) info.Target = target;
                info.Arguments = args;
            }
            catch { }
            lnkCache[lnkPath] = info;
            return info;
        }

        // 找到目标应用当前可见的顶层窗口：先按 exe 全路径精确匹配，再按进程名兜底。
        IntPtr FindRunningWindow(string path)
        {
            try
            {
                string wantName = Path.GetFileNameWithoutExtension(path);
                uint myPid = (uint)Process.GetCurrentProcess().Id;
                IntPtr exact = IntPtr.Zero, exactMin = IntPtr.Zero, byName = IntPtr.Zero, byNameMin = IntPtr.Zero;
                Native.EnumWindows(delegate(IntPtr h, IntPtr lp)
                {
                    try
                    {
                        if (h == IntPtr.Zero || !Native.IsWindowVisible(h)) return true;
                        int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                        if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;      // 跳过工具窗口
                        if (Native.GetWindowTextLength(h) <= 0) return true;
                        uint pid = 0;
                        Native.GetWindowThreadProcessId(h, out pid);
                        if (pid == 0 || pid == myPid) return true;
                        string exe = ExePathOfPid(pid);
                        if (exe == null) return true;
                        bool min = Native.IsIconic(h);
                        if (string.Equals(exe, path, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!min) { exact = h; return false; }                 // 枚举到非最小化的目标窗口即可停止
                            if (exactMin == IntPtr.Zero) exactMin = h;
                        }
                        else if (byName == IntPtr.Zero && string.Equals(Path.GetFileNameWithoutExtension(exe), wantName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (min) { if (byNameMin == IntPtr.Zero) byNameMin = h; }
                            else byName = h;
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
                if (exact != IntPtr.Zero) return exact;
                if (exactMin != IntPtr.Zero) return exactMin;
                if (byName != IntPtr.Zero) return byName;
                return byNameMin;
            }
            catch { return IntPtr.Zero; }
        }

        void ActivateWindow(IntPtr hwnd)
        {
            try
            {
                if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, 9);             // SW_RESTORE
                else if (!Native.IsWindowVisible(hwnd)) Native.ShowWindow(hwnd, 8); // SW_SHOWNA
                if (Native.SetForegroundWindow(hwnd)) return;
                // 前台锁兜底：把自己附加到前台线程的输入队列后再置前
                IntPtr fg = Native.GetForegroundWindow();
                uint dummy = 0;
                uint fgThread = fg != IntPtr.Zero ? Native.GetWindowThreadProcessId(fg, out dummy) : 0;
                uint curThread = Native.GetCurrentThreadId();
                bool attached = false;
                try
                {
                    if (fgThread != 0 && fgThread != curThread) attached = Native.AttachThreadInput(curThread, fgThread, true);
                    Native.BringWindowToTop(hwnd);
                    Native.SetForegroundWindow(hwnd);
                }
                finally
                {
                    if (attached) Native.AttachThreadInput(curThread, fgThread, false);
                }
            }
            catch { }
        }

        static string ExePathOfPid(uint pid)
        {
            if (pid == 0) return null;
            IntPtr h = Native.OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            StringBuilder sb = new StringBuilder(1024);
            uint n = 1024;
            bool ok = Native.QueryFullProcessImageName(h, 0, sb, ref n);
            Native.CloseHandle(h);
            return ok ? sb.ToString() : null;
        }

        void PickApp(int index)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "选择要添加的应用";
            dlg.Filter = "程序或快捷方式 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*";
            dlg.CheckFileExists = true;
            bool? r = dlg.ShowDialog(panel);
            if (r == true)
            {
                while (cfg.Quick.Count <= index) cfg.Quick.Add(null);
                cfg.Quick[index] = dlg.FileName;
                RefreshQuick();
                SaveNow();
            }
        }

        // ---------------- 最近访问追踪 ----------------

        void StartTimers()
        {
            tracker = new DispatcherTimer();
            tracker.Interval = TimeSpan.FromMilliseconds(600);
            tracker.Tick += TrackerTick;
            tracker.Start();

            saveTimer = new DispatcherTimer();
            saveTimer.Interval = TimeSpan.FromSeconds(2);
            saveTimer.Tick += delegate { if (dirty) SaveNow(); };
            saveTimer.Start();
        }

        void TrackerTick(object sender, EventArgs e)
        {
            tick++;
            IntPtr fg = Native.GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != lastHwnd)
            {
                lastHwnd = fg;
                string exe = ForegroundExe(fg);
                if (exe != null && ShouldTrack(exe))
                {
                    recents.Remove(exe);
                    recents.Insert(0, exe);
                    if (recents.Count > 5) recents.RemoveRange(5, recents.Count - 5);
                    RefreshRecents();
                    dirty = true;
                }
            }
            if (tick % 5 == 0) UpdateRunningSet();
        }

        string ForegroundExe(IntPtr fg)
        {
            try
            {
                if (Native.GetWindowTextLength(fg) <= 0) return null;
                uint pid;
                Native.GetWindowThreadProcessId(fg, out pid);
                return ExePathOfPid(pid);
            }
            catch { return null; }
        }

        bool ShouldTrack(string exe)
        {
            try
            {
                string myExe = Process.GetCurrentProcess().MainModule.FileName;
                if (string.Equals(exe, myExe, StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch { }
            string name = Path.GetFileName(exe).ToLowerInvariant();
            switch (name)
            {
                case "applicationframehost.exe":
                case "runtimebroker.exe":
                case "shellexperiencehost.exe":
                case "startmenuexperiencehost.exe":
                case "searchhost.exe":
                case "textinputhost.exe":
                case "dllhost.exe":
                case "lockapp.exe":
                case "widgets.exe":
                case "svchost.exe":
                case "conhost.exe":
                    return false;
            }
            return true;
        }

        void UpdateRunningSet()
        {
            runningNames.Clear();
            try
            {
                Process[] ps = Process.GetProcesses();
                foreach (Process p in ps)
                {
                    try { runningNames.Add(p.ProcessName); } catch { }
                    try { p.Dispose(); } catch { }
                }
            }
            catch { }
            UpdateDots();
        }

        // ---------------- 最小化 / 展开 ----------------

        void Minimize()
        {
            if (minimized) return;
            minimized = true;
            cfg.Minimized = true;
            SaveNow();
            PositionBubbleAtMinButton();
            card.RenderTransformOrigin = new Point(0.9, 0.05);
            AnimateCardOut(170);
            bubbleCard.Opacity = 0;
            bubbleScale.ScaleX = bubbleScale.ScaleY = 0.9;
            bubble.Show();
            AnimateBubbleIn(230);
        }

        void Expand()
        {
            if (!minimized) return;
            minimized = false;
            cfg.Minimized = false;
            SaveNow();
            double cx = bubble.Left + bubble.Width / 2.0;
            double cy = bubble.Top + bubble.Height / 2.0;
            AnimateBubbleOut(150);
            double bx = panel.Width - (PAD + 4 + 14);    // 收起按钮中心相对窗口的偏移
            double by = PAD + 19;
            panel.Left = cx - bx;
            panel.Top = cy - by;
            ClampWindow(panel);
            card.RenderTransformOrigin = new Point(0.9, 0.05);
            card.Opacity = 0;
            cardScale.ScaleX = cardScale.ScaleY = 0.96;
            panel.Show();
            AnimateCardIn(250);
        }

        void PositionBubbleAtMinButton()
        {
            double bx = panel.Width - (PAD + 4 + 14);
            double by = PAD + 19;
            bubble.Left = panel.Left + bx - bubble.Width / 2.0;
            bubble.Top = panel.Top + by - bubble.Height / 2.0;
            ClampWindow(bubble);
        }

        void AnimateCardOut(int ms)
        {
            DoubleAnimation ox = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(ms)));
            ox.EasingFunction = Ui.Out;
            ox.Completed += delegate
            {
                if (minimized)
                {
                    card.BeginAnimation(UIElement.OpacityProperty, null);
                    card.Opacity = 1;
                    cardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    cardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    cardScale.ScaleX = cardScale.ScaleY = 1;
                    panel.Hide();
                }
            };
            card.BeginAnimation(UIElement.OpacityProperty, ox);
            AnimateScaleOn(cardScale, 0.9, ms);
        }

        void AnimateCardIn(int ms)
        {
            DoubleAnimation ox = new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(ms)));
            ox.EasingFunction = Ui.Out;
            card.BeginAnimation(UIElement.OpacityProperty, ox);
            AnimateScaleOn(cardScale, 1, ms);
        }

        void AnimateBubbleIn(int ms)
        {
            DoubleAnimation ox = new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(ms)));
            ox.EasingFunction = Ui.Out;
            bubbleCard.BeginAnimation(UIElement.OpacityProperty, ox);
            AnimateScaleOn(bubbleScale, 1, ms);
        }

        void AnimateBubbleOut(int ms)
        {
            DoubleAnimation ox = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(ms)));
            ox.EasingFunction = Ui.Out;
            ox.Completed += delegate
            {
                bubbleCard.BeginAnimation(UIElement.OpacityProperty, null);
                bubbleCard.Opacity = 1;
                bubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                bubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                bubbleScale.ScaleX = bubbleScale.ScaleY = 1;
                bubble.Hide();
            };
            bubbleCard.BeginAnimation(UIElement.OpacityProperty, ox);
            AnimateScaleOn(bubbleScale, 0.92, ms);
        }

        // 首次显示：整体淡入 + 10 个槽位错峰上浮（28ms 间隔，装饰性、不阻塞交互）
        void PlayEntrance()
        {
            card.Opacity = 0;
            cardScale.ScaleX = cardScale.ScaleY = 0.96;
            DoubleAnimation ox = new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(260)));
            ox.EasingFunction = Ui.Out;
            card.BeginAnimation(UIElement.OpacityProperty, ox);
            AnimateScaleOn(cardScale, 1, 260);

            for (int i = 0; i < 10; i++)
            {
                Slot slot = i < 5 ? recSlots[i] : qSlots[i - 5];
                TransformGroup tg = (TransformGroup)slot.RenderTransform;
                TranslateTransform tt = (TranslateTransform)tg.Children[1];
                slot.Opacity = 0;
                tt.Y = 8;
                TimeSpan delay = TimeSpan.FromMilliseconds(i * 28);
                DoubleAnimation dy = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(230)));
                dy.EasingFunction = Ui.Out;
                dy.BeginTime = delay;
                tt.BeginAnimation(TranslateTransform.YProperty, dy);
                DoubleAnimation op = new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(230)));
                op.EasingFunction = Ui.Out;
                op.BeginTime = delay;
                slot.BeginAnimation(UIElement.OpacityProperty, op);
            }
        }

        // ---------------- 位置 / DPI ----------------

        Rect WorkAreaFor(Window w)
        {
            try
            {
                double sx = VisualTreeHelper.GetDpi(w).PixelsPerInchX / 96.0;
                double sy = VisualTreeHelper.GetDpi(w).PixelsPerInchY / 96.0;
                Native.POINT p = new Native.POINT();
                p.X = (int)Math.Round((w.Left + w.Width / 2.0) * sx);
                p.Y = (int)Math.Round((w.Top + Math.Max(1, w.ActualHeight) / 2.0) * sy);
                IntPtr mon = Native.MonitorFromPoint(p, 2);   // MONITOR_DEFAULTTONEAREST
                if (mon != IntPtr.Zero)
                {
                    Native.MONITORINFO mi = new Native.MONITORINFO();
                    mi.cbSize = (uint)Marshal.SizeOf(typeof(Native.MONITORINFO));
                    if (Native.GetMonitorInfo(mon, ref mi))
                    {
                        return new Rect(mi.rcWork.left / sx, mi.rcWork.top / sy,
                            (mi.rcWork.right - mi.rcWork.left) / sx, (mi.rcWork.bottom - mi.rcWork.top) / sy);
                    }
                }
            }
            catch { }
            return SystemParameters.WorkArea;
        }

        void ClampWindow(Window w)
        {
            Rect wa = WorkAreaFor(w);
            double h = double.IsNaN(w.ActualHeight) || w.ActualHeight < 1 ? w.Height : w.ActualHeight;
            if (w.Width >= wa.Width) w.Left = wa.Left;
            else w.Left = Math.Max(wa.Left, Math.Min(w.Left, wa.Right - w.Width));
            if (h >= wa.Height) w.Top = wa.Top;
            else w.Top = Math.Max(wa.Top, Math.Min(w.Top, wa.Bottom - h));
        }

        // ---------------- 持久化 ----------------

        void NormalizeQuick()
        {
            if (cfg.Quick == null) cfg.Quick = new List<string>();
            while (cfg.Quick.Count < 5) cfg.Quick.Add(null);
            if (cfg.Quick.Count > 5) cfg.Quick.RemoveRange(5, cfg.Quick.Count - 5);

            bool allEmpty = true;
            foreach (string q in cfg.Quick) { if (!string.IsNullOrEmpty(q)) { allEmpty = false; break; } }
            if (allEmpty) cfg.Quick = DefaultQuick();
        }

        List<string> DefaultQuick()
        {
            List<string> list = new List<string>();
            list.Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\explorer.exe");
            string edge = null;
            string e1 = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
            string e2 = @"C:\Program Files\Microsoft\Edge\Application\msedge.exe";
            if (File.Exists(e1)) edge = e1; else if (File.Exists(e2)) edge = e2;
            list.Add(edge);
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string np = windir + "\\System32\\notepad.exe";
            string cc = windir + "\\System32\\calc.exe";
            string cm = windir + "\\System32\\cmd.exe";
            list.Add(File.Exists(np) ? np : null);
            list.Add(File.Exists(cc) ? cc : null);
            list.Add(File.Exists(cm) ? cm : null);
            return list;
        }

        void SaveNow()
        {
            try
            {
                if (panel != null && !double.IsNaN(panel.Left) && !double.IsInfinity(panel.Left))
                {
                    cfg.Left = panel.Left;
                    cfg.Top = panel.Top;
                }
                if (recents != null) cfg.Recents = new List<string>(recents);
                Store.Save(cfg);
            }
            catch { }
            dirty = false;
        }

        // ---------------- 开机自启 ----------------

        bool AutoStartEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    return k != null && k.GetValue(RunKeyName) != null;
                }
            }
            catch { return false; }
        }

        void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunKeyName, "\"" + ExePath() + "\"");
                    else if (k.GetValue(RunKeyName) != null) k.DeleteValue(RunKeyName);
                }
            }
            catch { }
        }

        string ExePath()
        {
            try { return Process.GetCurrentProcess().MainModule.FileName; }
            catch { return Assembly.GetExecutingAssembly().Location; }
        }

        // ---------------- 动画小工具 ----------------

        static void AnimateScaleOn(ScaleTransform st, double to, int ms)
        {
            DoubleAnimation dx = new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(ms)));
            dx.EasingFunction = Ui.Out;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, dx);
            DoubleAnimation dy = new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(ms)));
            dy.EasingFunction = Ui.Out;
            st.BeginAnimation(ScaleTransform.ScaleYProperty, dy);
        }

        static void AnimateSolid(Brush b, byte alpha)
        {
            SolidColorBrush sb = b as SolidColorBrush;
            if (sb == null) return;
            ColorAnimation ca = new ColorAnimation();
            ca.To = Color.FromArgb(alpha, 255, 255, 255);
            ca.Duration = new Duration(TimeSpan.FromMilliseconds(120));
            ca.EasingFunction = Ui.Out;
            sb.BeginAnimation(SolidColorBrush.ColorProperty, ca);
        }
    }
}
