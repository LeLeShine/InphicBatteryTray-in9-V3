// InphicBatteryTray.cs  —  英菲克鼠标电量托盘显示器
// 纯只读：不修改设备、不修改 INPHIC HUB，仅接收设备主动上报的电量报告。
//
// 图标：由 鼠标.svg 渲染的剪影，按电量从下往上填充，颜色随电量变化。
//
// 编译：
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /platform:x64 ^
//     /resource:MouseIcon.png,MouseIcon.png ^
//     /out:InphicBatteryTray.exe /reference:System.dll /reference:System.Windows.Forms.dll ^
//     /reference:System.Drawing.dll InphicBatteryTray.cs

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace InphicBatteryTray
{
    static class Native
    {
        public const int DIGCF_PRESENT = 0x02;
        public const int DIGCF_DEVICEINTERFACE = 0x10;
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const int FILE_SHARE_READ = 1;
        public const int FILE_SHARE_WRITE = 2;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        public const int ERROR_IO_PENDING = 997;
        public const uint WAIT_OBJECT_0 = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct GUID { public uint Data1; public ushort Data2; public ushort Data3; public byte b0, b1, b2, b3, b4, b5, b6, b7; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public GUID InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct OVERLAPPED
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public int Offset;
            public int OffsetHigh;
            public IntPtr hEvent;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr SetupDiGetClassDevsW(ref GUID ClassGuid, IntPtr Enumerator, IntPtr hwndParent, int Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr hDevInfo, IntPtr devInfo, ref GUID interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr hDevInfo, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out GUID guid);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetAttributes(IntPtr device, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        public static extern bool HidD_GetProductString(IntPtr device, IntPtr buffer, int bufferLength);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileW(string fileName, uint desiredAccess, int shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(IntPtr hFile, byte[] buffer, int numberOfBytesToRead, out int numberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(IntPtr hFile, byte[] buffer, int numberOfBytesToWrite, out int numberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr preparsedData, byte[] capabilities);

        [DllImport("hid.dll")]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetFeature(IntPtr hidDeviceObject, byte[] buffer, int bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetFeature(IntPtr hidDeviceObject, byte[] buffer, int bufferLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, IntPtr lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(IntPtr hFile, IntPtr lpOverlapped, out int lpNumberOfBytesTransferred, bool bWait);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ResetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }

    class HidInterface
    {
        public string Path;
        public ushort Vid;
        public ushort Pid;
        public string Product;
        public string Transport;   // USB / 蓝牙
        public bool IsIn9;         // IN9 系列：需要主动查询
        public int InLen;          // 输入报告长度
        public int OutLen;         // 输出报告长度
        public int FeatLen;        // Feature 报告长度
    }

    class DeviceState
    {
        public string Label;
        public int Battery = -1;
        public double LastIntervalMs;
        public DateTime LastSeen = DateTime.MinValue;
        public int ChargeState = -1;   // 1 正常 / 2 已满 / 3 充电
        public string Protocol;        // "IN10 push" / "IN9 query"
        public string Product;
        public ushort Vid;
        public ushort Pid;
        public string Transport;
        public int TypeCode = -1;
    }

    class Reader
    {
        // 从 HID 接口路径推断连接方式（蓝牙设备走 BTHENUM / bth 路径）
        public static string DetectTransport(string path)
        {
            if (string.IsNullOrEmpty(path)) return "未知";
            string p = path.ToLowerInvariant();
            if (p.Contains("bth") || p.Contains("bluetooth")) return "蓝牙 (BLE)";
            return "USB";
        }

        public static List<HidInterface> Enumerate()
        {
            var list = new List<HidInterface>();
            Native.GUID guid;
            Native.HidD_GetHidGuid(out guid);
            IntPtr set = Native.SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == Native.INVALID_HANDLE_VALUE) return list;

            try
            {
                int idx = 0;
                while (true)
                {
                    var did = new Native.SP_DEVICE_INTERFACE_DATA();
                    did.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, idx, ref did))
                        break;
                    idx++;

                    int need;
                    Native.SetupDiGetDeviceInterfaceDetailW(set, ref did, IntPtr.Zero, 0, out need, IntPtr.Zero);
                    if (need <= 0) continue;

                    IntPtr buf = Marshal.AllocHGlobal(need);
                    try
                    {
                        Marshal.WriteInt32(buf, 0, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref did, buf, need, out need, IntPtr.Zero))
                            continue;
                        string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                        if (string.IsNullOrEmpty(path)) continue;

                        var dev = new HidInterface();
                        dev.Path = path;
                        IntPtr h = Native.CreateFileW(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                        if (h == Native.INVALID_HANDLE_VALUE) continue;
                        try
                        {
                            var attr = new Native.HIDD_ATTRIBUTES();
                            attr.Size = Marshal.SizeOf(typeof(Native.HIDD_ATTRIBUTES));
                            if (Native.HidD_GetAttributes(h, ref attr))
                            {
                                dev.Vid = attr.VendorID;
                                dev.Pid = attr.ProductID;
                            }
                            IntPtr sbuf = Marshal.AllocHGlobal(512);
                            try
                            {
                                if (Native.HidD_GetProductString(h, sbuf, 512))
                                    dev.Product = Marshal.PtrToStringUni(sbuf);
                            }
                            finally { Marshal.FreeHGlobal(sbuf); }

                            // 报告长度：WriteFile 必须严格匹配，否则会 ERROR_INVALID_PARAMETER
                            IntPtr pp;
                            if (Native.HidD_GetPreparsedData(h, out pp) && pp != IntPtr.Zero)
                            {
                                try
                                {
                                    byte[] caps = new byte[256];
                                    if (Native.HidP_GetCaps(pp, caps) == 0x00110000)
                                    {
                                        dev.InLen = BitConverter.ToUInt16(caps, 4);
                                        dev.OutLen = BitConverter.ToUInt16(caps, 6);
                                        dev.FeatLen = BitConverter.ToUInt16(caps, 8);
                                    }
                                }
                                finally { Native.HidD_FreePreparsedData(pp); }
                            }
                        }
                        finally { Native.CloseHandle(h); }

                        if (dev.Vid != 0 || dev.Pid != 0)
                        {
                            dev.Transport = DetectTransport(path);
                            list.Add(dev);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }

            return list;
        }
    }

    static class Program
    {
        // 已从 INPHIC.exe 逆向出的候选 VID/PID
        static readonly ushort[][] Candidates = new ushort[][]
        {
            new ushort[]{0x1D57,0xFA60}, new ushort[]{0x1D57,0xFA65},
            new ushort[]{0x045E,0x0095}, new ushort[]{0x045E,0x0090},
            new ushort[]{0x045E,0x0093}, new ushort[]{0x045E,0x0099},
            new ushort[]{0x045E,0x0098}, new ushort[]{0x045E,0x0273},
            new ushort[]{0x045E,0x0271},
        };

        static readonly HashSet<int> TypeCodes = new HashSet<int> { 0x95, 0x90, 0x93, 0x99, 0x98, 0x73 };

        const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RUN_NAME = "InphicBatteryTray";

        static NotifyIcon tray;
        static Icon currentIcon;
        static string logPath = null;
        static string statePath = null;
        static Bitmap srcIcon = null;
        static MenuItem autoItem;
        static int lastDrawnLevel = -999;
        static bool lastDrawnOffline = true;

        // 超过该时长没收到电量报告即视为离线（设备上报周期约 2s）
        const int OfflineAfterMs = 8000;
        static System.Windows.Forms.Timer statusTimer;

        static readonly object sync = new object();
        static readonly Dictionary<string, DeviceState> states = new Dictionary<string, DeviceState>();
        static volatile bool running = true;
        static Thread supervisor;

        static void Log(string s)
        {
            if (logPath == null) return;
            try
            {
                File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\r\n");
            }
            catch { }
        }

        // 充电期间设备只上报 100%，缓存最后一次非充电电量供此时显示
        static int LoadLastBattery(string label)
        {
            try
            {
                if (statePath == null || !File.Exists(statePath)) return -1;
                foreach (string line in File.ReadAllLines(statePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (line.Substring(0, eq) != label) continue;
                    int v;
                    if (int.TryParse(line.Substring(eq + 1), out v) && v >= 0 && v <= 100) return v;
                }
            }
            catch { }
            return -1;
        }

        static void SaveLastBattery(string label, int level)
        {
            try
            {
                if (statePath == null) return;
                var lines = new List<string>();
                if (File.Exists(statePath)) lines.AddRange(File.ReadAllLines(statePath));
                bool found = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq > 0 && lines[i].Substring(0, eq) == label)
                    {
                        lines[i] = label + "=" + level;
                        found = true;
                    }
                }
                if (!found) lines.Add(label + "=" + level);
                File.WriteAllLines(statePath, lines.ToArray());
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "InphicBatteryTray");
                Directory.CreateDirectory(dir);
                statePath = Path.Combine(dir, "last_battery.txt");
            }
            catch { statePath = null; }

            logPath = Environment.GetEnvironmentVariable("INPHIC_TRAY_LOG");
            foreach (string a in args)
            {
                if (a.StartsWith("--log=")) logPath = a.Substring(6);
            }
            if (logPath == null)
            {
                // 默认写日志到程序目录，方便排查（尤其 IN9 支持）
                try
                {
                    string dir = Path.GetDirectoryName(Application.ExecutablePath);
                    logPath = Path.Combine(dir, "tray.log");
                    if (File.Exists(logPath) && new FileInfo(logPath).Length > 512 * 1024)
                        File.Delete(logPath);          // 超过 512KB 就换新的
                }
                catch { logPath = null; }
            }
            Log("=== start ===");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            LoadIconSource();

            tray = new NotifyIcon();
            tray.Text = "英菲克 电量：搜索设备中…";
            tray.Icon = DrawIcon(-1, true);
            tray.Visible = true;

            var menu = new ContextMenu();

            autoItem = new MenuItem("开机自动启动");
            autoItem.Checked = IsAutoStartEnabled();
            autoItem.Click += delegate { ToggleAutoStart(); };

            menu.MenuItems.Add("显示详情", delegate { ShowDetails(); });
            menu.MenuItems.Add("重新扫描设备", delegate { Rescan(); });
            menu.MenuItems.Add(autoItem);
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("退出", delegate { Quit(); });

            tray.ContextMenu = menu;
            tray.DoubleClick += delegate { ShowDetails(); };

            supervisor = new Thread(Supervise);
            supervisor.IsBackground = true;
            supervisor.Start();

            // 每秒检查一次：设备是否已经长时间没有上报（关机/断连/休眠）
            statusTimer = new System.Windows.Forms.Timer();
            statusTimer.Interval = 1000;
            statusTimer.Tick += delegate { TickStatus(); };
            statusTimer.Start();

            // 自测：设置 INPHIC_TEST_POPUP=1 时启动 2.5 秒后自动弹一次浮层
            if (Environment.GetEnvironmentVariable("INPHIC_TEST_POPUP") == "1")
            {
                var testTimer = new System.Windows.Forms.Timer();
                testTimer.Interval = 2500;
                testTimer.Tick += delegate
                {
                    testTimer.Stop();
                    try { ShowDetails(); Log("test popup shown"); }
                    catch (Exception ex) { Log("test popup FAILED: " + ex); }
                };
                testTimer.Start();
            }

            Application.Run();
        }

        // ---------------------------------------------------------- 开机自启

        static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue(RUN_NAME);
                    if (v == null) return false;
                    string cur = v.ToString().Trim('"');
                    return string.Equals(cur, Application.ExecutablePath,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        static void ToggleAutoStart()
        {
            try
            {
                bool want = !IsAutoStartEnabled();
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
                {
                    if (k == null)
                    {
                        MessageBox.Show("无法打开启动项注册表键。", "英菲克 电量",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (want)
                        k.SetValue(RUN_NAME, "\"" + Application.ExecutablePath + "\"");
                    else
                        k.DeleteValue(RUN_NAME, false);
                }
                if (autoItem != null) autoItem.Checked = IsAutoStartEnabled();
                Log("autostart -> " + want);
            }
            catch (Exception ex)
            {
                MessageBox.Show("设置开机自启动失败：" + ex.Message, "英菲克 电量",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---------------------------------------------------------- 图标

        static void LoadIconSource()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream s = asm.GetManifestResourceStream("MouseIcon.png"))
                {
                    if (s != null) srcIcon = new Bitmap(s);
                }
                Log("icon source: " + (srcIcon == null ? "MISSING" : srcIcon.Width + "x" + srcIcon.Height));
            }
            catch (Exception ex) { Log("icon load error: " + ex.Message); }
        }

        static Color StatusColor(int lv)
        {
            if (lv <= 15) return Color.FromArgb(255, 70, 70);
            if (lv <= 40) return Color.FromArgb(255, 190, 60);
            return Color.FromArgb(0x19, 0xFA, 0xC6);   // #19fac6
        }

        // 原图主体是 #1afa29 高饱和绿；缩放混色后仍保持 g 明显优势
        static bool IsGreenBody(Color c)
        {
            return c.G > 110 && (c.G - c.R) > 45 && (c.G - c.B) > 45;
        }

        static Color OutlineColor { get { return Color.FromArgb(38, 32, 62); } }

        // 最外一圈：alpha 有效且四邻域里有背景（保证 16px 下轮廓实心不虚）
        static bool[,] BuildRing(int[,] alpha, int w, int h, int thr)
        {
            bool[,] ring = new bool[h, w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (alpha[y, x] < thr) continue;
                    if (x == 0 || y == 0 || x == w - 1 || y == h - 1)
                    {
                        ring[y, x] = true;
                        continue;
                    }
                    if (alpha[y, x + 1] < thr || alpha[y, x - 1] < thr ||
                        alpha[y + 1, x] < thr || alpha[y - 1, x] < thr)
                        ring[y, x] = true;
                }
            }
            return ring;
        }

        // 按电量从下往上填充鼠标主体；外圈实心描边，滚轮/顶部保持原色
        // offline=true 时整体灰显（设备关机 / 断连 / 长时间未上报）
        static Icon DrawIcon(int level, bool offline)
        {
            Size sz = SystemInformation.SmallIconSize;
            if (sz.Width < 16) sz = new Size(16, 16);
            int w = sz.Width, h = sz.Height;

            using (Bitmap scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.Clear(Color.Transparent);
                    if (srcIcon != null)
                        g.DrawImage(srcIcon, new Rectangle(0, 0, w, h));
                }

                using (Bitmap outp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    double fillTop = h * (1.0 - Math.Max(0, Math.Min(100, level)) / 100.0);
                    Color st = StatusColor(level);
                    Color empty = Color.FromArgb(100, 100, 110);
                    Color outline = OutlineColor;
                    Color offOutline = Color.FromArgb(52, 52, 62);
                    bool grey = offline || level < 0;

                    int[,] alpha = new int[h, w];
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            alpha[y, x] = scaled.GetPixel(x, y).A;

                    bool[,] ring = BuildRing(alpha, w, h, 40);

                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            Color c = scaled.GetPixel(x, y);
                            if (c.A < 40) continue;   // 丢弃极淡边缘，避免 16px 下发虚

                            Color o;
                            if (grey)
                            {
                                if (ring[y, x])
                                {
                                    o = offOutline;
                                }
                                else
                                {
                                    // 灰度化，但保留内部明暗（滚轮仍可辨）
                                    int L = (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);
                                    int v = 74 + L * 108 / 255;
                                    if (v > 255) v = 255;
                                    o = Color.FromArgb(v, v, Math.Min(255, v + 8));
                                }
                            }
                            else if (ring[y, x])
                            {
                                o = outline;
                            }
                            else if (IsGreenBody(c))
                            {
                                o = (y >= fillTop) ? st : empty;
                            }
                            else
                            {
                                o = c;   // 滚轮 / 顶部 / 高光 原样保留
                            }

                            outp.SetPixel(x, y, Color.FromArgb(c.A, o));
                        }
                    }

                    IntPtr hIcon = outp.GetHicon();
                    try
                    {
                        // 调试：把实际生成的图标存一份，便于核对视觉
                        if (logPath != null)
                        {
                            try
                            {
                                outp.Save(logPath + ".icon.png", ImageFormat.Png);
                            }
                            catch { }
                        }
                        using (Icon tmp = Icon.FromHandle(hIcon))
                            return (Icon)tmp.Clone();
                    }
                    finally
                    {
                        Native.DestroyIcon(hIcon);
                    }
                }
            }
        }

        // ---------------------------------------------------------- 菜单动作

        static void Quit()
        {
            running = false;
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            Application.Exit();
        }

        static void Rescan()
        {
            List<Thread> ts = readersSnapshot();
            lock (readerThreads) readerThreads.Clear();
            lock (activePaths) activePaths.Clear();
            foreach (Thread t in ts)
            {
                try { t.Interrupt(); } catch { }
            }
        }

        static List<Thread> readerThreads = new List<Thread>();
        // 已建立读取连接的接口路径，防止每轮枚举重复打开
        static readonly HashSet<string> activePaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static List<Thread> readersSnapshot()
        {
            lock (readerThreads) return new List<Thread>(readerThreads);
        }

        static string ChargeText(int c)
        {
            switch (c)
            {
                case 1: return "正常（未充电）";
                case 2: return "已充满";
                case 3: return "充电中";
                default: return "未知";
            }
        }

        static void ShowDetails()
        {
            string msg;
            lock (sync)
            {
                if (states.Count == 0)
                {
                    msg = "尚未收到任何电量上报。\n\n请确认：\n· 设备已连接并唤醒\n· 原程序不是独占状态（本工具为只读共享）";
                }
                else
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var kv in states)
                    {
                        var d = kv.Value;
                        bool off = (DateTime.Now - d.LastSeen).TotalMilliseconds > OfflineAfterMs;

                        sb.AppendLine(string.IsNullOrEmpty(d.Product) ? d.Label : d.Product);
                        sb.AppendLine("  连接方式  : " + (string.IsNullOrEmpty(d.Transport) ? "未知" : d.Transport));
                        sb.AppendLine(string.Format("  硬件 ID   : {0:X4}:{1:X4}", d.Vid, d.Pid));
                        if (d.TypeCode >= 0)
                            sb.AppendLine(string.Format("  协议族    : type 0x{0:X2}", d.TypeCode));
                        sb.AppendLine("  协议      : " + (string.IsNullOrEmpty(d.Protocol) ? "未知" : d.Protocol));
                        sb.AppendLine("  电量      : " + d.Battery + "%");
                        sb.AppendLine("  充电状态  : " + ChargeText(d.ChargeState));
                        sb.AppendLine(string.Format("  刷新间隔  : {0:F0} ms", d.LastIntervalMs));
                        sb.AppendLine(string.Format("  最后上报  : {0:HH:mm:ss}", d.LastSeen));
                        sb.AppendLine("  当前状态  : " + (off ? "已断开 / 已关机" : "在线"));
                        sb.AppendLine();
                    }
                    msg = sb.ToString();
                }
            }
            ShowInfoPopup(msg);
        }

        static InfoPopup popup;

        // 非模态信息浮层：不抢焦点、不阻塞、鼠标移开自动消失
        static void ShowInfoPopup(string text)
        {
            if (popup != null)
            {
                try { popup.Close(); popup.Dispose(); } catch { }
                popup = null;
            }
            popup = new InfoPopup(text);
            popup.FormClosed += delegate { popup = null; };
            popup.ShowNear(Cursor.Position);
        }

        // ---------------------------------------------------------- 设备读取

        static void Supervise()
        {
            while (running)
            {
                try
                {
                    List<HidInterface> all = Reader.Enumerate();
                    var targets = new List<HidInterface>();
                    foreach (HidInterface d in all)
                    {
                        if (IsIn9Vid(d.Vid))
                            Log(string.Format("IN9 iface: {0:X4}:{1:X4} in={2} out={3} feat={4} {5}",
                                d.Vid, d.Pid, d.InLen, d.OutLen, d.FeatLen, d.Product));

                        // 已知走 IN9 查询协议的型号：不看 Feature 长度，直接主动查询
                        if (IsIn9Pair(d.Vid, d.Pid))
                        {
                            d.IsIn9 = true;
                            targets.Add(d);
                            continue;
                        }

                        bool matched = false;
                        foreach (ushort[] c in Candidates)
                        {
                            if (d.Vid == c[0] && d.Pid == c[1]) { matched = true; break; }
                        }
                        if (matched) { d.IsIn9 = false; targets.Add(d); continue; }

                        if (IsIn9Vid(d.Vid) && d.FeatLen >= 33)
                        {
                            d.IsIn9 = true;
                            targets.Add(d);
                        }
                    }
                    Log(string.Format("enumerate: {0} hid interfaces, {1} candidates ({2} IN9)",
                        all.Count, targets.Count, CountIn9(targets)));

                    foreach (HidInterface d in targets)
                    {
                        HidInterface dd = d;
                        lock (activePaths)
                        {
                            if (activePaths.Contains(dd.Path)) continue;
                            activePaths.Add(dd.Path);
                        }
                        var t = new Thread(delegate ()
                        {
                            try
                            {
                                if (dd.IsIn9) In9QueryLoop(dd);
                                else ReadLoop(dd);
                            }
                            finally
                            {
                                lock (activePaths) activePaths.Remove(dd.Path);
                            }
                        });
                        t.IsBackground = true;
                        t.Start();
                        lock (readerThreads) readerThreads.Add(t);
                    }

                    Thread.Sleep(10000);
                }
                catch (Exception)
                {
                    Thread.Sleep(5000);
                }
            }
        }

        // ---------------------------------------------------------- IN9（主动查询）

        // IN9 系列：USB=VID_248A，2.4G=VID_249A（PID 不固定）
        static readonly ushort[] In9Vids = new ushort[] { 0x248A, 0x249A };

        // 已知走 IN9 主动查询的 VID/PID（V3 接收器 249A:5C2F）
        static readonly ushort[][] In9Pairs = new ushort[][]
        {
            new ushort[]{0x249A,0x5C2F},
        };

        static bool IsIn9Pair(ushort vid, ushort pid)
        {
            for (int i = 0; i < In9Pairs.Length; i++)
                if (In9Pairs[i][0] == vid && In9Pairs[i][1] == pid) return true;
            return false;
        }

        static bool IsIn9Vid(ushort vid)
        {
            for (int i = 0; i < In9Vids.Length; i++)
                if (In9Vids[i] == vid) return true;
            return false;
        }

        static int CountIn9(List<HidInterface> list)
        {
            int n = 0;
            foreach (HidInterface d in list) if (d.IsIn9) n++;
            return n;
        }

        static string Hex(byte[] b, int n)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n && i < b.Length; i++)
                sb.Append(b[i].ToString("X2")).Append(' ');
            return sb.ToString().Trim();
        }

        // IN9 命令帧（33 字节 Feature Report，实测自 IN9 升级版）
        //   0C 01 <cmd> 00 <seq> <payload...>
        //   握手 cmd=0xF0 payload=01 01 FF
        //   查电量 cmd=0x20 payload=00 00
        static byte[] BuildIn9Frame(byte cmd, byte seq, byte[] payload, int len)
        {
            if (len < 33) len = 33;
            byte[] f = new byte[len];
            f[0] = 0x0C;      // 报告 ID
            f[1] = 0x01;
            f[2] = cmd;
            f[3] = 0x00;
            f[4] = seq;
            if (payload != null)
                Array.Copy(payload, 0, f, 5, Math.Min(payload.Length, len - 5));
            return f;
        }

        static bool HidWrite(IntPtr h, byte[] frame, int outLen)
        {
            if (outLen < frame.Length) outLen = frame.Length;
            byte[] b = new byte[outLen];
            Array.Copy(frame, b, frame.Length);

            IntPtr ev = Native.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
            if (ev == IntPtr.Zero) return false;
            IntPtr ovPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.OVERLAPPED)));
            try
            {
                Native.OVERLAPPED ov = new Native.OVERLAPPED();
                ov.hEvent = ev;
                Marshal.StructureToPtr(ov, ovPtr, false);

                int written = 0;
                bool ok = Native.WriteFile(h, b, outLen, out written, ovPtr);
                if (!ok && Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING) return false;

                if (Native.WaitForSingleObject(ev, 1000) != Native.WAIT_OBJECT_0)
                {
                    Native.CancelIoEx(h, ovPtr);
                    return false;
                }
                int got = 0;
                return Native.GetOverlappedResult(h, ovPtr, out got, false);
            }
            finally
            {
                Marshal.FreeHGlobal(ovPtr);
                Native.CloseHandle(ev);
            }
        }

        static int HidReadTimeout(IntPtr h, byte[] buf, int ms)
        {
            IntPtr ev = Native.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
            if (ev == IntPtr.Zero) return -1;
            IntPtr ovPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.OVERLAPPED)));
            try
            {
                Native.OVERLAPPED ov = new Native.OVERLAPPED();
                ov.hEvent = ev;
                Marshal.StructureToPtr(ov, ovPtr, false);

                int read = 0;
                bool ok = Native.ReadFile(h, buf, buf.Length, out read, ovPtr);
                if (!ok && Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING) return -1;

                if (Native.WaitForSingleObject(ev, (uint)ms) != Native.WAIT_OBJECT_0)
                {
                    Native.CancelIoEx(h, ovPtr);
                    return 0;
                }
                int got = 0;
                if (!Native.GetOverlappedResult(h, ovPtr, out got, false)) return -1;
                return got;
            }
            finally
            {
                Marshal.FreeHGlobal(ovPtr);
                Native.CloseHandle(ev);
            }
        }

        static void In9QueryLoop(HidInterface dev)
        {
            string label = string.Format("{0:X4}:{1:X4}", dev.Vid, dev.Pid);

            // V3 接收器没有 Feature 报告，改用 33 字节输出报告下发查询（见 V3QueryLoop）
            bool featureMode = dev.FeatLen >= 33;
            bool outputMode = !featureMode && dev.OutLen >= 33;
            if (!featureMode && !outputMode)
            {
                Log(string.Format("IN9 skip {0} [{1}]: in={2} out={3} feat={4}",
                    dev.Product, label, dev.InLen, dev.OutLen, dev.FeatLen));
                return;
            }

            Log("IN9 try open (" + (outputMode ? "out/in" : "feature") + "): " + dev.Path);
            IntPtr h = Native.CreateFileW(dev.Path,
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING,
                outputMode ? Native.FILE_FLAG_OVERLAPPED : 0, IntPtr.Zero);
            if (h == Native.INVALID_HANDLE_VALUE)
            {
                Log("IN9 open FAILED err=" + Marshal.GetLastWin32Error());
                return;
            }

            int frameLen = featureMode ? Math.Max(33, dev.FeatLen) : Math.Max(33, dev.OutLen);
            int respLen = Math.Max(33, dev.InLen);
            Log(string.Format("IN9 opened OK: {0}  mode={1} in={2} out={3} feat={4} frame={5} resp={6}",
                dev.Product, outputMode ? "out/in" : "feature",
                dev.InLen, dev.OutLen, dev.FeatLen, frameLen, respLen));

            byte seq = 0x01;
            byte[] resp = new byte[respLen];
            int failStreak = 0;
            int polls = 0;

            try
            {
                if (!featureMode)
                {
                    V3QueryLoop(h, dev, label, frameLen, respLen);
                    return;
                }

                while (running)
                {
                    // 握手（只发不收）：0C 01 F0 00 <seq> 01 01 FF
                    byte[] hs = BuildIn9Frame(0xF0, seq, new byte[] { 0x01, 0x01, 0xFF }, frameLen);
                    // 查电量：0C 01 20 00 <seq> 00 00
                    byte[] q = BuildIn9Frame(0x20, seq, new byte[] { 0x00, 0x00 }, frameLen);
                    polls++;

                    Native.HidD_SetFeature(h, hs, hs.Length);
                    bool sent = Native.HidD_SetFeature(h, q, q.Length);

                    if (!sent)
                    {
                        Log("IN9 send FAILED err=" + Marshal.GetLastWin32Error());
                        if (++failStreak >= 3) { Log("IN9 give up this interface"); break; }
                    }
                    else
                    {
                        failStreak = 0;
                        Thread.Sleep(10);
                        Array.Clear(resp, 0, resp.Length);
                        bool got = Native.HidD_GetFeature(h, resp, resp.Length);
                        if (!got && polls <= 5)
                            Log("IN9 GetFeature FAILED err=" + Marshal.GetLastWin32Error());

                        if (got)
                        {
                            if (polls <= 5) Log("IN9 rx: " + Hex(resp, Math.Min(resp.Length, 33)));
                            AcceptIn9(dev, label, resp, polls <= 5);
                        }
                    }

                    seq++;
                    if (seq == 0) seq = 1;
                    Thread.Sleep(2500);
                }
            }
            catch (ThreadInterruptedException) { }
            catch (Exception ex) { Log("IN9 loop exception: " + ex.Message); }
            finally { Native.CloseHandle(h); }
        }

        // V3 接收器（249A:5C2F）实测协议：
        //   查询  33 字节输出报告  00 <queryId> 00 ... 00
        //   回复  00 <id> 00 01 <len> <payload...> <sum>   sum = payload 字节和
        //   NACK  00 <id> 02 FF（背靠背连发时出现，因此每轮只发一条）
        //   推送  00 C0 <无线标志> <电量> <充电标志>
        // 电量位于 0x10 回复的 payload[9]（绝对偏移 14），0-100 百分比；
        // 充电标志：0x10 回复的 payload[8]（绝对偏移 13）或推送里电量之后的一字节
        // （绝对偏移 4），非 0 为充电中。推送里电量前面的 <无线标志> 与充电无关。
        // 充电中设备固定上报 100%，需显示插线前的真实电量（本地缓存兜底）。
        static void V3QueryLoop(IntPtr h, HidInterface dev, string label, int frameLen, int respLen)
        {
            byte[] req = new byte[frameLen];
            byte[] resp = new byte[respLen];
            int failStreak = 0;
            int polls = 0;

            while (running)
            {
                polls++;

                Array.Clear(req, 0, req.Length);
                req[0] = 0x00;
                req[1] = 0x10;   // 设备信息（含电量）

                if (!HidWrite(h, req, frameLen))
                {
                    Log("IN9 v3 send FAILED err=" + Marshal.GetLastWin32Error());
                    if (++failStreak >= 3) { Log("IN9 v3 give up this interface"); break; }
                    Thread.Sleep(2500);
                    continue;
                }
                failStreak = 0;

                bool got = false;
                for (int i = 0; i < 8; i++)
                {
                    Array.Clear(resp, 0, resp.Length);
                    int n = HidReadTimeout(h, resp, 300);
                    if (n <= 0) break;
                    if (polls <= 3) Log("IN9 v3 rx: " + Hex(resp, Math.Min(n, 33)));
                    if (AcceptV3(dev, label, resp, n, polls <= 3)) { got = true; break; }
                }
                if (!got && polls <= 3)
                    Log("IN9 v3 no battery in reply");

                Thread.Sleep(2500);
            }
        }

        static bool AcceptV3(HidInterface dev, string label, byte[] resp, int n, bool dump)
        {
            if (n < 4 || resp[0] != 0x00) return false;

            int lv = -1;
            int charge = -1;

            if (resp[1] == 0xC0)
            {
                // 未请求推送：00 C0 <无线标志> <电量> <充电标志>
                int v = resp[3];
                if (v <= 100) lv = v;
                if (n >= 5) charge = (resp[4] != 0) ? 3 : 1;
            }
            else if (resp[1] == 0x10 && resp[2] == 0x00 && resp[3] == 0x01 && n >= 5)
            {
                int len = resp[4];
                if (len >= 10 && 5 + len <= n - 1)
                {
                    // 校验和固定位于报告最后一字节
                    int sum = 0;
                    for (int i = 0; i < len; i++) sum += resp[5 + i];
                    if ((sum & 0xFF) == resp[n - 1])
                    {
                        int v = resp[5 + 9];
                        if (v <= 100) lv = v;
                        if (len >= 9) charge = (resp[5 + 8] != 0) ? 3 : 1;
                    }
                }
            }

            if (lv < 0) return false;

            DateTime now = DateTime.Now;
            lock (sync)
            {
                DeviceState st;
                if (!states.TryGetValue(label, out st))
                {
                    st = new DeviceState();
                    st.Label = label;
                    states[label] = st;
                }
                if (st.LastSeen != DateTime.MinValue)
                    st.LastIntervalMs = (now - st.LastSeen).TotalMilliseconds;

                bool charging = (charge == 3) || (charge < 0 && st.ChargeState == 3);

                // 充电期间设备把电量固定上报为 100（推送和 0x10 回复都是 0x64），
                // 这时显示插线前的真实电量；程序在充电中启动时用本地缓存兜底
                int shown = lv;
                if (charging && lv == 100)
                {
                    int prev = (st.Battery >= 0 && st.Battery < 100) ? st.Battery : LoadLastBattery(label);
                    if (prev >= 0 && prev < 100) shown = prev;
                }

                bool changed = (st.Battery != shown || (charge >= 0 && st.ChargeState != charge));
                st.Battery = shown;
                if (charge >= 0) st.ChargeState = charge;
                if (!charging && changed) SaveLastBattery(label, shown);
                st.Protocol = "IN9 v3 query";
                st.LastSeen = now;
                st.Product = dev.Product;
                st.Vid = dev.Vid;
                st.Pid = dev.Pid;
                st.Transport = dev.Transport;

                if (changed || dump)
                    Log("IN9 v3 battery " + shown + "%  charge=" + charge + "  ["
                        + dev.Product + "] " + Hex(resp, Math.Min(n, 33)));
            }
            UpdateTrayUI();
            return true;
        }

        static void AcceptIn9(HidInterface dev, string label, byte[] resp, bool dump)
        {
            // 实测布局：(IN9 升级版)
            //   [0]=0x0C [1]=0x01 [2]=0x20(命令回显) [6]!=0 有效
            //   [8..0x0B]="M909" [0x0F]=IC [0x12]=电量 [0x13]==2 充电中 [0x15]=在线
            bool echo = resp[2] == 0x20;
            bool valid = resp[6] != 0;
            int lv = -1;
            if (echo && valid)
            {
                int v = resp[0x12];
                if (v >= 0 && v <= 100) lv = v;
            }

            if (lv < 0)
            {
                Log("IN9 未认定电量 echo=0x" + resp[2].ToString("X2") + " flag=" + resp[6]
                    + "  raw=" + Hex(resp, 33));
                return;
            }

            int charge = (resp[0x13] == 2) ? 3 : 1;   // 3=充电中，1=正常

            DateTime now = DateTime.Now;
            lock (sync)
            {
                DeviceState st;
                if (!states.TryGetValue(label, out st))
                {
                    st = new DeviceState();
                    st.Label = label;
                    states[label] = st;
                }
                if (st.LastSeen != DateTime.MinValue)
                    st.LastIntervalMs = (now - st.LastSeen).TotalMilliseconds;

                bool changed = (st.Battery != lv || st.ChargeState != charge);
                st.Battery = lv;
                st.ChargeState = charge;
                st.Protocol = "IN9 query";
                st.LastSeen = now;
                st.Product = dev.Product;
                st.Vid = dev.Vid;
                st.Pid = dev.Pid;
                st.Transport = dev.Transport;

                if (changed || dump)
                    Log("IN9 battery " + lv + "%  charge=" + charge + "  [" + dev.Product + "]");
            }
            UpdateTrayUI();
        }

        static void ReadLoop(HidInterface dev)
        {
            string label = string.Format("{0:X4}:{1:X4}", dev.Vid, dev.Pid);
            if (!string.IsNullOrEmpty(dev.Product)) label = dev.Product + " [" + label + "]";

            IntPtr h = Native.CreateFileW(dev.Path,
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h == Native.INVALID_HANDLE_VALUE)
            {
                h = Native.CreateFileW(dev.Path, Native.GENERIC_READ,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                    IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            }
            if (h == Native.INVALID_HANDLE_VALUE) return;
            Log("opened: " + label);

            IntPtr ev = Native.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
            var buf = new byte[256];
            var ov = new Native.OVERLAPPED();
            ov.hEvent = ev;
            IntPtr ovPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.OVERLAPPED)));

            DateTime lastReport = DateTime.MinValue;

            try
            {
                while (running)
                {
                    Marshal.StructureToPtr(ov, ovPtr, false);
                    Native.ResetEvent(ev);
                    int read = 0;
                    bool ok = Native.ReadFile(h, buf, buf.Length, out read, ovPtr);
                    int err = Marshal.GetLastWin32Error();
                    if (!ok && err != Native.ERROR_IO_PENDING) break;

                    uint rc = Native.WaitForSingleObject(ev, 3000);
                    if (rc != Native.WAIT_OBJECT_0)
                    {
                        Native.CancelIoEx(h, ovPtr);
                        continue;
                    }
                    int got = 0;
                    if (!Native.GetOverlappedResult(h, ovPtr, out got, false)) continue;
                    if (got <= 0) continue;

                    // 电量事件：0x40 / 0x41，第 4 字节是充电状态（不是设备号）
                    if (buf[0] == 0x03 && TypeCodes.Contains(buf[1]) &&
                        (buf[2] == 0x40 || buf[2] == 0x41) && got >= 5)
                    {
                        int charge = buf[3];
                        int level = buf[4];
                        if (level < 0 || level > 100) continue;

                        DateTime now = DateTime.Now;
                        double interval = 0;
                        if (lastReport != DateTime.MinValue)
                            interval = (now - lastReport).TotalMilliseconds;
                        lastReport = now;

                        lock (sync)
                        {
                            DeviceState st;
                            if (!states.TryGetValue(label, out st))
                            {
                                st = new DeviceState();
                                st.Label = label;
                                states[label] = st;
                            }
                            st.Battery = level;
                            st.ChargeState = charge;
                            st.Protocol = "IN10 push";
                            st.LastSeen = now;
                            st.Product = dev.Product;
                            st.Vid = dev.Vid;
                            st.Pid = dev.Pid;
                            st.Transport = dev.Transport;
                            st.TypeCode = buf[1];
                            if (interval > 0) st.LastIntervalMs = interval;
                        }
                        UpdateTrayUI();
                        Log(string.Format("battery {0}%  charge={1}  {2:F0}ms  transport={3}  type=0x{4:X2}  [{5}]",
                            level, charge, interval, dev.Transport, buf[1], label));
                    }
                }
            }
            catch (ThreadInterruptedException) { }
            catch (Exception) { }
            finally
            {
                Marshal.FreeHGlobal(ovPtr);
                Native.CloseHandle(ev);
                Native.CloseHandle(h);
            }
        }

        static void TickStatus()
        {
            int level = -1;
            DateTime newest = DateTime.MinValue;
            double interval = 0;

            lock (sync)
            {
                foreach (var kv in states)
                {
                    if (kv.Value.LastSeen > newest)
                    {
                        newest = kv.Value.LastSeen;
                        level = kv.Value.Battery;
                        interval = kv.Value.LastIntervalMs;
                    }
                }
            }

            bool offline = (newest == DateTime.MinValue) ||
                           ((DateTime.Now - newest).TotalMilliseconds > OfflineAfterMs);

            RefreshTray(level, interval, offline, newest);
        }

        static void RefreshTray(int level, double interval, bool offline, DateTime lastSeen)
        {
            if (level != lastDrawnLevel || offline != lastDrawnOffline)
            {
                Log(string.Format("tray state: level={0} offline={1}", level, offline));
                Icon old = currentIcon;
                currentIcon = DrawIcon(level, offline);
                tray.Icon = currentIcon;
                lastDrawnLevel = level;
                lastDrawnOffline = offline;
                if (old != null) { try { old.Dispose(); } catch { } }
            }

            string tip;
            if (level < 0)
                tip = "英菲克：未检测到设备";
            else if (offline)
                tip = string.Format("英菲克：已断开 / 已关机（最后 {0}%，{1:HH:mm:ss}）",
                    level, lastSeen);
            else
                tip = string.Format("英菲克 电量 {0}%  ({1:F0}ms 刷新)", level, interval);

            if (tip.Length > 127) tip = tip.Substring(0, 127);
            tray.Text = tip;
        }

        static void UpdateTrayUI()
        {
            int level = -1;
            double interval = 0;
            DateTime newest = DateTime.MinValue;
            lock (sync)
            {
                foreach (var kv in states)
                {
                    if (kv.Value.LastSeen > newest)
                    {
                        newest = kv.Value.LastSeen;
                        level = kv.Value.Battery;
                        interval = kv.Value.LastIntervalMs;
                    }
                }
            }
            if (level < 0) return;

            bool offline = ((DateTime.Now - newest).TotalMilliseconds > OfflineAfterMs);
            RefreshTray(level, interval, offline, newest);
        }
    }

    // 轻量信息浮层：不抢焦点、不阻塞，鼠标移开自动消失
    class InfoPopup : Form
    {
        System.Windows.Forms.Timer lifeTimer;
        int countdown;

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进 Alt+Tab
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：不抢焦点
                return cp;
            }
        }

        public InfoPopup(string text)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(28, 28, 32);

            Label lbl = new Label();
            lbl.Dock = DockStyle.Fill;
            lbl.Text = text;
            lbl.ForeColor = Color.FromArgb(236, 236, 242);
            lbl.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
            lbl.TextAlign = ContentAlignment.TopLeft;
            lbl.BackColor = Color.Transparent;
            Controls.Add(lbl);

            Padding = new Padding(18, 13, 18, 13);
            Size ts = TextRenderer.MeasureText(text, lbl.Font,
                new Size(560, 2000), TextFormatFlags.Left | TextFormatFlags.Top);
            ClientSize = new Size(ts.Width + 36, ts.Height + 26);

            lifeTimer = new System.Windows.Forms.Timer();
            lifeTimer.Interval = 1000;
            lifeTimer.Tick += delegate
            {
                countdown--;
                if (countdown <= 0) Close();
            };

            EventHandler enter = delegate { countdown = 9999; };
            EventHandler leave = delegate { countdown = 2; };
            MouseEnter += enter;
            MouseLeave += leave;
            lbl.MouseEnter += enter;
            lbl.MouseLeave += leave;
            Click += delegate { Close(); };
            lbl.Click += delegate { Close(); };
        }

        public void ShowNear(Point anchor)
        {
            Show();
            Rectangle wa = Screen.FromPoint(anchor).WorkingArea;
            int x = anchor.X - Width / 2;
            int y = anchor.Y - Height - 16;
            if (y < wa.Top) y = anchor.Y + 20;      // 上方放不下就翻到下方
            if (x < wa.Left) x = wa.Left + 4;
            if (x + Width > wa.Right) x = wa.Right - Width - 4;
            Location = new Point(x, y);
            countdown = 4;                          // 4 秒后自动消失；鼠标停留则保持
            lifeTimer.Start();
        }
    }
}
