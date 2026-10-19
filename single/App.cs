// App.cs —— QoderMonitor 单 exe 版的渲染 + 采集入口
//
// 单 exe 方案：Data.cs 在进程内读取 transcript，App.cs 直接消费 SessionCache，
// 不再启动 pythonw、不写 bar_state.json。UI 仍走 DirectComposition + Direct2D/DirectWrite。
//
// 编译（在 single/ 目录下）：
//   csc -nologo -target:winexe -out:QoderMonitor.exe -r:System.Web.Extensions.dll
//       -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.Core.dll
//       Data.cs Interop.cs App.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using QoderMonitor;

static class App
{
    // ---------- 路径（exe 可在桌面，配置/日志放用户目录） ----------
    static readonly string Dir = Path.GetDirectoryName(Application.ExecutablePath);
    static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QoderMonitor");
    static readonly string PosFile = Path.Combine(ConfigDir, "pos.json");
    static readonly string LogFile = Path.Combine(ConfigDir, "logs", "QoderMonitor.log");

    // ---------- 主题 ----------
    static readonly Color C_FG = Hex("333a45"), C_TITLE = Hex("4b5462"), C_LBL = Hex("8b93a1");
    static readonly Color C_ACCENT = Hex("2b6cb8"), C_OK = Hex("1fa971"), C_IDLE = Hex("b8bfcb");
    static readonly Color C_OK_RING = Hex("a8dcc4"), C_IDLE_RING = Hex("dde2ea");
    static readonly Color C_FAST = Hex("178457"), C_SLOW = Hex("a8741c");
    static readonly Color C_WARN = Hex("a8741c"), C_CRIT = Hex("c0473a");
    static readonly Color C_ICON = Hex("6b7482"), C_TRACK = Hex("d8dde5");

    // ---------- 布局（DIP，乘 scale） ----------
    const float GAP = 12, FS = 12, TITLE_W = 150, ROW_H = 19, PAD_H = 4;
    static readonly string[] CELLS = { "dot", "title", "clock", "turns", "tps", "tpsU", "tpsK",
        "db", "tok", "cacheL", "cache", "ring", "ctx", "credL", "cred" };
    static readonly Dictionary<string, float> MARGIN = new Dictionary<string, float> {
        {"dot",3},{"title",GAP},{"clock",3},{"turns",3},{"tps",2},{"tpsU",2},{"tpsK",GAP-3},
        {"db",3},{"tok",3},{"cacheL",4},{"cache",GAP-4},{"ring",3},{"ctx",GAP},{"credL",2},{"cred",0} };
    static readonly Dictionary<string, float> FIXED = new Dictionary<string, float> {
        {"dot",12},{"clock",14},{"db",14},{"ring",14} };
    const float TPS_FAST = 120, TPS_SLOW = 60, CTX_MID = 70, CTX_HOT = 90;
    const int MAX_ROWS = 2;

    // ---------- 状态 ----------
    static IntPtr hwnd, d3dDevice, d3dCtx, dxgiDev, compDevice, target, visual, surface;
    static IntPtr d2dFactory, dwrite, fmtCjk, fmtLat, mutex;
    static P.WndProc procKeep;
    static string[] rowsSig = new string[0];
    static List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
    static readonly SessionCache cache = new SessionCache();
    static int winW, winH, winX, winY;
    static float scale = 1.5f;
    static bool dragging, pin = true, follow = true, visible, always;
    static int dragDx, dragDy;
    static double manualShowUntil, lastDataTick;
    static NotifyIcon tray;
    static ContextMenuStrip trayMenu;
    static Icon trayIcon;
    static ToolStripMenuItem pinItem, followItem, autoItem;
    const uint WM_APP_SHOW = 0x8001;

    static Color Hex(string s)
    {
        return Color.FromArgb(Convert.ToInt32(s.Substring(0, 2), 16),
            Convert.ToInt32(s.Substring(2, 2), 16), Convert.ToInt32(s.Substring(4, 2), 16));
    }

    static void Log(string s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile));
            File.AppendAllText(LogFile, DateTime.Now.ToString("HH:mm:ss") + " " + s + Environment.NewLine);
        }
        catch { }
    }

    static void Hr(string what, int hr)
    {
        if (hr < 0) Log(what + " hr=0x" + hr.ToString("X8"));
    }

    // ---------------- 入口 ----------------
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
        {
            Log("UNHANDLED " + e.ExceptionObject);
        };
        try { MainInner(args); }
        catch (Exception ex) { Log("FATAL " + ex); }
    }

    static void MainInner(string[] args)
    {
        SetProcessDPIAware();                 // 漏了会 scale=1、被 DWM 拉伸、字糊
        always = Array.IndexOf(args, "--always") >= 0;
        bool autoStartArg = Array.IndexOf(args, "--autostart") >= 0;
        if (!GrabMutex()) return;
        LoadPos();
        manualShowUntil = autoStartArg ? 0 : QmUtil.UnixNow() + 8.0;

        IntPtr inst = P.GetModuleHandleW(null);
        var wc = new P.WNDCLASSEX();
        wc.cbSize = (uint)Marshal.SizeOf(typeof(P.WNDCLASSEX));
        wc.style = 0x0003u | 0x0008u;
        procKeep = WndProc;
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procKeep);
        wc.hInstance = inst;
        wc.hCursor = P.LoadCursorW(IntPtr.Zero, (IntPtr)32512);
        wc.lpszClassName = "QoderMonitorSingle";
        if (P.RegisterClassExW(ref wc) == 0) { Log("RegisterClass failed " + Marshal.GetLastWin32Error()); return; }

        uint ex = 0x00200000u | 0x00000008u | 0x00000080u | 0x08000000u;
        hwnd = P.CreateWindowExW(ex, wc.lpszClassName, "QoderMonitor", 0x80000000u,
            winX, winY, 200, RowsHeight(), IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { Log("CreateWindow failed " + Marshal.GetLastWin32Error()); return; }

        uint dpi = P.GetDpiForWindow(hwnd);
        if (dpi > 0) scale = dpi / 96f;
        Log("dpi=" + dpi + " scale=" + scale + " pos=" + winX + "," + winY);

        if (!InitGraphics()) { Log("gfx init failed"); return; }
        SetupTray();
        P.SetTimer(hwnd, (IntPtr)1, 500, IntPtr.Zero);

        Tick();                       // 第一次立刻读一次
        P.MSG m;
        while (P.GetMessageW(out m, IntPtr.Zero, 0, 0) > 0)
        {
            P.TranslateMessage(ref m);
            P.DispatchMessageW(ref m);
        }
        Shutdown();
    }

    static int RowsHeight()
    {
        int n = rows.Count > 0 ? rows.Count : 1;
        return (int)Math.Round(ROW_H * n * scale);
    }

    // ---------------- 单实例 ----------------
    static bool GrabMutex()
    {
        IntPtr h = P.CreateMutexW(IntPtr.Zero, true, @"Global\QoderMonitorBar");
        if (h == IntPtr.Zero) return true;
        uint err = P.GetLastError();
        if (err != P.ERROR_ALREADY_EXISTS) { mutex = h; return true; }
        if (P.WaitForSingleObject(h, 0) == P.WAIT_TIMEOUT)
        {
            // 已有实例：让它显示一次，然后本进程退出。
            IntPtr other = P.FindWindowW("QoderMonitorSingle", null);
            if (other != IntPtr.Zero)
                P.PostMessageW(other, WM_APP_SHOW, IntPtr.Zero, IntPtr.Zero);
            P.CloseHandle(h);
            return false;
        }
        mutex = h;
        return true;
    }

    // ---------------- 位置 ----------------
    static void LoadPos()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            if (!File.Exists(PosFile))
            {
                // 从旧版“配置文件放在 exe 旁边”迁移一次，保留窄条位置。
                string legacy = Path.Combine(Dir, "pos.json");
                if (File.Exists(legacy)) File.Copy(legacy, PosFile, false);
            }
        }
        catch { }
        try
        {
            var d = NewSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(PosFile));
            if (d.ContainsKey("pin")) pin = Convert.ToBoolean(d["pin"]);
            if (!always && d.ContainsKey("follow")) follow = Convert.ToBoolean(d["follow"]);
            double? x = Num(d, "dcomp_x");
            double? y = Num(d, "dcomp_y");
            if (x.HasValue && y.HasValue)
            {
                winX = (int)Math.Round(x.Value * scale);
                winY = (int)Math.Round(y.Value * scale);
                int vx2 = P.GetSystemMetrics(76), vy2 = P.GetSystemMetrics(77);
                int vw2 = P.GetSystemMetrics(78), vh2 = P.GetSystemMetrics(79);
                winX = Math.Max(vx2, Math.Min(winX, vx2 + vw2 - 200));
                winY = Math.Max(vy2, Math.Min(winY, vy2 + vh2 - 60));
                return;
            }
        }
        catch { }
        int sw = Screen.PrimaryScreen.WorkingArea.Width;
        int sh = Screen.PrimaryScreen.WorkingArea.Height;
        winX = Math.Max(0, (sw - 860) / 2);
        winY = Math.Max(0, sh - 120);
    }

    static void SavePos()
    {
        if (hwnd == IntPtr.Zero) return;
        P.RECT r;
        P.GetWindowRect(hwnd, out r);
        int vx = P.GetSystemMetrics(76), vy = P.GetSystemMetrics(77);
        int vw = P.GetSystemMetrics(78), vh = P.GetSystemMetrics(79);
        if (r.l < vx - 20 || r.l > vx + vw - 40 || r.t < vy - 20 || r.t > vy + vh - 20)
        {
            Log("save pos skipped: " + r.l + "," + r.t + " outside virtual screen");
            return;                              // 明显跑飞的坐标不落盘
        }
        winX = r.l; winY = r.t;
        try
        {
            Directory.CreateDirectory(ConfigDir);
            Dictionary<string, object> d;
            try { d = NewSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(PosFile)); }
            catch { d = new Dictionary<string, object>(); }
            d["dcomp_x"] = Math.Round(r.l / scale, 1);
            d["dcomp_y"] = Math.Round(r.t / scale, 1);
            File.WriteAllText(PosFile, NewSerializer().Serialize(d));
        }
        catch (Exception e) { Log("save pos failed: " + e.Message); }
    }

    static JavaScriptSerializer NewSerializer()
    {
        return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
    }

    static double? Num(Dictionary<string, object> d, string k)
    {
        object v;
        if (!d.TryGetValue(k, out v) || v == null) return null;
        try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return null; }
    }

    // ---------------- 图形初始化 ----------------
    static bool InitGraphics()
    {
        Guid iidF2 = P.IID_IDXGIFactory2;
        IntPtr factory2;
        int hr = P.CreateDXGIFactory1(ref iidF2, out factory2);
        if (hr < 0) { Hr("CreateDXGIFactory1", hr); return false; }

        IntPtr adapter;
        hr = P.Fn<P.D_EnumAdapters1>(factory2, 12)(factory2, 0, out adapter);
        if (hr < 0) { Hr("EnumAdapters1", hr); return false; }
        int fl;
        hr = P.D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0x20u, IntPtr.Zero, 0, 7,
            out d3dDevice, out fl, out d3dCtx);
        P.Release(adapter);
        if (hr < 0) { Hr("D3D11CreateDevice", hr); return false; }

        Guid iidDxgi = P.IID_IDXGIDevice;
        hr = Marshal.QueryInterface(d3dDevice, ref iidDxgi, out dxgiDev);
        if (hr < 0) { Hr("QI IDXGIDevice", hr); return false; }

        Guid iidComp = P.IID_IDCompositionDevice;
        hr = P.DCompositionCreateDevice(dxgiDev, ref iidComp, out compDevice);
        if (hr < 0) { Hr("DCompositionCreateDevice", hr); return false; }

        hr = P.Fn<P.D_CreateTarget>(compDevice, 6)(compDevice, hwnd, 1, out target);
        if (hr < 0) { Hr("CreateTargetForHwnd", hr); return false; }
        hr = P.Fn<P.D_CreateVisual>(compDevice, 7)(compDevice, out visual);
        if (hr < 0) { Hr("CreateVisual", hr); return false; }
        hr = P.Fn<P.D_SetRoot>(target, 3)(target, visual);
        if (hr < 0) { Hr("SetRoot", hr); return false; }

        Guid iidD2D = P.IID_ID2D1Factory;
        hr = P.D2D1CreateFactory(0, ref iidD2D, IntPtr.Zero, out d2dFactory);
        if (hr < 0) { Hr("D2D1CreateFactory", hr); return false; }

        Guid iidDW = P.IID_IDWriteFactory;
        hr = P.DWriteCreateFactory(0, ref iidDW, out dwrite);
        if (hr < 0) { Hr("DWriteCreateFactory", hr); return false; }

        // DirectWrite：中文 SimSun、西文 Times New Roman，和 GDI 版一致
        hr = P.Fn<P.D_CreateTextFormat>(dwrite, 15)(dwrite, "SimSun", IntPtr.Zero,
            400, 0, 5, FS * scale, "zh-CN", out fmtCjk);
        if (hr < 0) { Hr("CreateTextFormat(cjk)", hr); return false; }
        hr = P.Fn<P.D_CreateTextFormat>(dwrite, 15)(dwrite, "Times New Roman", IntPtr.Zero,
            400, 0, 5, FS * scale, "zh-CN", out fmtLat);
        if (hr < 0) { Hr("CreateTextFormat(lat)", hr); return false; }
        P.Fn<P.D_SetInt>(fmtCjk, 5)(fmtCjk, 2);
        P.Fn<P.D_SetInt>(fmtLat, 5)(fmtLat, 2);

        return MakeSurface(winW > 0 ? winW : 800, RowsHeight());
    }

    static bool MakeSurface(int w, int h)
    {
        if (w < 4) w = 4;
        if (h < 4) h = 4;
        if (surface != IntPtr.Zero)
        {
            P.Release(surface);
            surface = IntPtr.Zero;
        }
        int hr = P.Fn<P.D_CreateSurface>(compDevice, 8)(compDevice, (uint)w, (uint)h, 87, 1, out surface);
        if (hr < 0) { Hr("CreateSurface", hr); return false; }
        hr = P.Fn<P.D_SetContent>(visual, 15)(visual, surface);
        if (hr < 0) { Hr("SetContent", hr); return false; }
        hr = P.Fn<P.D_CompCommit>(compDevice, 3)(compDevice);
        if (hr < 0) { Hr("Commit", hr); return false; }
        winW = w; winH = h;
        Log("surface " + w + "x" + h);
        return true;
    }

    // ---------------- 数据（进程内） ----------------
    static void Tick()
    {
        double now = QmUtil.UnixNow();
        if (now - lastDataTick >= 0.9)
        {
            lastDataTick = now;
            RefreshData(now);
        }
        SyncVisible();
    }

    static void RefreshData(double now)
    {
        List<Finding> top = SessionFinder.FindActiveTranscripts(MAX_ROWS, 30.0);
        List<Dictionary<string, object>> list = cache.Build(top, now);
        string[] sig = new string[list.Count];
        for (int i = 0; i < list.Count; i++) sig[i] = QmJson.Serialize(list[i]);
        if (string.Join("|", sig) == string.Join("|", rowsSig)) return;
        rows = list;
        rowsSig = sig;
        if (rows.Count > 0) Relayout();
    }

    // ---------------- 可见性（两道闸） ----------------
    static void SyncVisible()
    {
        if (hwnd == IntPtr.Zero) return;
        double now = QmUtil.UnixNow();
        bool want;
        if (always || now < manualShowUntil)
            want = true;
        else if (follow)
        {
            string fg = ForegroundName();
            bool isQ = fg.StartsWith("qoder", StringComparison.OrdinalIgnoreCase) &&
                       !fg.EndsWith("ide.exe", StringComparison.OrdinalIgnoreCase);
            want = isQ && QoderWindowExists();
        }
        else want = true;
        if (rows.Count == 0) want = false;
        if (want != visible)
        {
            visible = want;
            P.ShowWindow(hwnd, want ? 8 : 0);
            Log((want ? "SHOW" : "HIDE") + " fg=" + ForegroundName() + " rows=" + rows.Count);
        }
    }

    static string ForegroundName()
    {
        try
        {
            IntPtr h = P.GetForegroundWindow();
            uint pid;
            P.GetWindowThreadProcessId(h, out pid);
            IntPtr hp = P.OpenProcess(0x1000, false, pid);
            if (hp == IntPtr.Zero) return "";
            var sb = new StringBuilder(512);
            int n = sb.Capacity;
            P.QueryFullProcessImageNameW(hp, 0, sb, ref n);
            P.CloseHandle(hp);
            return Path.GetFileName(sb.ToString());
        }
        catch { return ""; }
    }

    static bool QoderWindowExists()
    {
        bool found = false;
        P.EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!P.IsWindowVisible(h)) return true;
            var t = new StringBuilder(64);
            P.GetWindowTextW(h, t, 64);
            if (t.Length == 0) return true;
            uint pid;
            P.GetWindowThreadProcessId(h, out pid);
            IntPtr hp = P.OpenProcess(0x1000, false, pid);
            if (hp == IntPtr.Zero) return true;
            var sb = new StringBuilder(512);
            int n = sb.Capacity;
            P.QueryFullProcessImageNameW(hp, 0, sb, ref n);
            P.CloseHandle(hp);
            string nm = Path.GetFileName(sb.ToString()).ToLowerInvariant();
            if (nm.StartsWith("qoder") && !nm.EndsWith("ide.exe")) { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ---------------- 布局 ----------------
    static void Relayout()
    {
        if (d2dFactory == IntPtr.Zero) return;
        var cells = new List<Dictionary<string, string[]>>();
        foreach (var r in rows) cells.Add(BuildCells(r));
        if (cells.Count == 0) return;

        var widths = new Dictionary<string, float>();
        foreach (string col in CELLS)
        {
            if (FIXED.ContainsKey(col)) { widths[col] = FIXED[col] * scale; continue; }
            float w = 0;
            foreach (var c in cells)
            {
                string txt = c[col][0];
                if (txt.Length == 0) continue;
                w = Math.Max(w, Measure(txt));
            }
            if (col == "title") w = Math.Min(w, TITLE_W * scale);
            widths[col] = w;
        }
        float total = PAD_H * scale * 2;
        foreach (string col in CELLS) total += widths[col] + MARGIN[col] * scale;
        int w2 = (int)Math.Round(total);
        int h2 = (int)Math.Round(ROW_H * cells.Count * scale);
        if (w2 != winW || h2 != winH)
        {
            if (!MakeSurface(w2, h2)) return;
            P.SetWindowPos(hwnd, (IntPtr)(pin ? -1 : -2), winX, winY, w2, h2, 0x0004 | 0x0010);
            P.RECT rr; P.GetWindowRect(hwnd, out rr); winX = rr.l; winY = rr.t;
        }
        Render(cells, widths);
    }

    // ---------------- 画 ----------------
    static void Render(List<Dictionary<string, string[]>> cells, Dictionary<string, float> widths)
    {
        if (surface == IntPtr.Zero || d2dFactory == IntPtr.Zero) return;
        var upd = new P.RECT(); upd.l = 0; upd.t = 0; upd.r = winW; upd.b = winH;
        Guid iidSurf = P.IID_IDXGISurface;
        IntPtr dxgiSurf; P.POINT off;
        int hr = P.Fn<P.D_SurfaceBeginDraw>(surface, 3)(surface, ref upd, ref iidSurf, out dxgiSurf, out off);
        if (hr < 0) { Hr("SurfaceBeginDraw", hr); return; }

        var props = new P.D2D1_RENDER_TARGET_PROPERTIES();
        props.pixelFormat.format = 87;
        props.pixelFormat.alphaMode = 1;
        props.dpiX = 96f; props.dpiY = 96f;
        IntPtr rt;
        hr = P.Fn<P.D_CreateDxgiRT>(d2dFactory, 15)(d2dFactory, dxgiSurf, ref props, out rt);
        P.Release(dxgiSurf);
        if (hr < 0) { Hr("RT from surface", hr); P.Fn<P.D_SurfaceEndDraw>(surface, 4)(surface); return; }

        P.Fn<P.D_BeginDraw>(rt, 48)(rt);
        var clear = new P.D2D1_COLOR_F();
        P.Fn<P.D_Clear>(rt, 47)(rt, ref clear);
        var m = new P.D2D1_MATRIX_3X2_F { m11 = 1f, m22 = 1f, dx = off.x, dy = off.y };
        P.Fn<P.D_SetTransform>(rt, 30)(rt, ref m);

        float s = scale;
        for (int i = 0; i < cells.Count; i++)
        {
            var row = cells[i];
            float baseY = ROW_H * i * s + 3.2f * s;
            float x = PAD_H * s;
            foreach (string col in CELLS)
            {
                string txt = row[col][0];
                Color rgb = Hex(row[col][1]);
                float cy = baseY + 6 * s;
                if (col == "dot") DrawDot(rt, x + 6 * s, cy, rgb);
                else if (col == "clock") DrawClock(rt, x + 6 * s, cy, rgb);
                else if (col == "db") DrawDb(rt, x + 6 * s, cy, rgb);
                else if (col == "ring")
                {
                    float frac = 0;
                    string ct = row["ctx"][0];
                    if (ct.Length > 0 && ct != "—")
                        float.TryParse(ct.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out frac);
                    DrawRing(rt, x + 6 * s, cy, frac / 100f, Hex(row["ctx"][1]));
                }
                else if (txt.Length > 0)
                    DrawTextRun(rt, x, baseY, txt, rgb);

                x += widths[col] + MARGIN[col] * s;
            }
        }

        ulong t1, t2;
        hr = P.Fn<P.D_EndDraw>(rt, 49)(rt, out t1, out t2);
        if (hr < 0) Hr("EndDraw", hr);
        P.Release(rt);
        hr = P.Fn<P.D_SurfaceEndDraw>(surface, 4)(surface);
        if (hr < 0) { Hr("SurfaceEndDraw", hr); return; }
        P.Fn<P.D_CompCommit>(compDevice, 3)(compDevice);
    }

    static IntPtr Brush(IntPtr rt, Color c)
    {
        var col = new P.D2D1_COLOR_F();
        col.r = c.R / 255f; col.g = c.G / 255f; col.b = c.B / 255f; col.a = 1f;
        IntPtr b;
        P.Fn<P.D_CreateBrush>(rt, 8)(rt, ref col, IntPtr.Zero, out b);
        return b;
    }

    static void DrawTextRun(IntPtr rt, float x, float y, string text, Color c)
    {
        IntPtr b = Brush(rt, c);
        float cx = x;
        foreach (var run in SplitRuns(text))
        {
            var rect = new P.D2D1_RECT_F();
            rect.left = cx; rect.top = y; rect.right = cx + 2000; rect.bottom = y + ROW_H * scale + 6;
            P.Fn<P.D_DrawText>(rt, 27)(rt, run.Item2, (uint)run.Item2.Length,
                run.Item1 == 1 ? fmtCjk : fmtLat, ref rect, b, 0, 0);
            cx += Measure(run.Item2);
        }
        P.Release(b);
    }

    static IEnumerable<Tuple<int, string>> SplitRuns(string s)
    {
        var outp = new List<Tuple<int, string>>();
        string cur = "";
        int kind = -1;
        foreach (char ch in s)
        {
            int k = IsCjk(ch) ? 1 : 0;
            if (kind < 0) kind = k;
            if (k != kind) { outp.Add(Tuple.Create(kind, cur)); cur = ""; kind = k; }
            cur += ch;
        }
        if (cur.Length > 0) outp.Add(Tuple.Create(kind, cur));
        return outp;
    }

    static bool IsCjk(char ch)
    {
        int o = ch;
        return (o >= 0x3000 && o <= 0x9FFF) || (o >= 0xFF00 && o <= 0xFFEF) ||
               (o >= 0x2018 && o <= 0x201D);
    }

    static readonly Dictionary<string, float> MeasCache = new Dictionary<string, float>();
    static Font gdiCjk, gdiLat;

    static float Measure(string text)
    {
        float v;
        if (MeasCache.TryGetValue(text, out v)) return v;
        if (gdiCjk == null) gdiCjk = new Font("SimSun", FS * scale, GraphicsUnit.Pixel);
        if (gdiLat == null) gdiLat = new Font("Times New Roman", FS * scale, GraphicsUnit.Pixel);
        float total = 0;
        using (var bmp = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var sf = StringFormat.GenericTypographic;
            sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            foreach (var run in SplitRuns(text))
                total += g.MeasureString(run.Item2, run.Item1 == 1 ? gdiCjk : gdiLat, PointF.Empty, sf).Width;
        }
        MeasCache[text] = total;
        return total;
    }

    static void Ellipse(IntPtr rt, float cx, float cy, float rx, float ry, Color c, float stroke)
    {
        IntPtr b = Brush(rt, c);
        var e = new P.D2D1_ELLIPSE();
        e.center.x = cx; e.center.y = cy; e.rx = rx; e.ry = ry;
        P.Fn<P.D_DrawEllipseAngles>(rt, 20)(rt, ref e, b, stroke, IntPtr.Zero);
        P.Release(b);
    }

    static void Dot(IntPtr rt, float cx, float cy, float r, Color c)
    {
        IntPtr b = Brush(rt, c);
        var e = new P.D2D1_ELLIPSE();
        e.center.x = cx; e.center.y = cy; e.rx = r; e.ry = r;
        P.Fn<P.D_FillEllipseAngles>(rt, 21)(rt, ref e, b);
        P.Release(b);
    }

    static void Line(IntPtr rt, float x1, float y1, float x2, float y2, Color c, float w)
    {
        IntPtr b = Brush(rt, c);
        P.Fn<P.D_DrawLineAngles>(rt, 15)(rt, new P.D2D1_POINT_2F { x = x1, y = y1 },
            new P.D2D1_POINT_2F { x = x2, y = y2 }, b, w, IntPtr.Zero);
        P.Release(b);
    }

    static void DrawDot(IntPtr rt, float cx, float cy, Color c)
    {
        float s = scale;
        Ellipse(rt, cx, cy, 5.5f * s, 5.5f * s, c == C_OK ? C_OK_RING : C_IDLE_RING, 1.2f * s);
        Dot(rt, cx, cy, 3.5f * s, c);
    }

    static void DrawClock(IntPtr rt, float cx, float cy, Color c)
    {
        float s = scale, r = 5.5f * s;
        Ellipse(rt, cx, cy, r, r, c, 1.2f * s);
        Line(rt, cx, cy, cx, cy - r * 0.55f, c, 1.2f * s);
        Line(rt, cx, cy, cx + r * 0.4f, cy, c, 1.2f * s);
    }

    static void DrawDb(IntPtr rt, float cx, float cy, Color c)
    {
        float s = scale, w = 9 * s, h = 10 * s, lw = 1.2f * s;
        Ellipse(rt, cx, cy - h * 0.28f, w / 2, h * 0.22f, c, lw);
        Line(rt, cx - w / 2, cy - h * 0.28f, cx - w / 2, cy + h * 0.3f, c, lw);
        Line(rt, cx + w / 2, cy - h * 0.28f, cx + w / 2, cy + h * 0.3f, c, lw);
        // 下半弧用几段短直线近似
        int seg = 10;
        for (int i = 0; i < seg; i++)
        {
            double a1 = Math.PI * i / seg, a2 = Math.PI * (i + 1) / seg;
            Line(rt, cx + (float)(Math.Cos(a1) * w / 2), cy + h * 0.3f + (float)(Math.Sin(a1) * h * 0.22f),
                 cx + (float)(Math.Cos(a2) * w / 2), cy + h * 0.3f + (float)(Math.Sin(a2) * h * 0.22f), c, lw);
        }
    }

    static void DrawRing(IntPtr rt, float cx, float cy, float frac, Color c)
    {
        float s = scale, r = 5.5f * s, lw = 1.6f * s;
        Ellipse(rt, cx, cy, r, r, C_TRACK, lw);
        if (frac <= 0.002f) return;
        frac = Math.Min(1f, frac);
        int seg = Math.Max(6, (int)(40 * frac));
        for (int i = 0; i < seg; i++)
        {
            double a1 = -Math.PI / 2 + 2 * Math.PI * frac * i / seg;
            double a2 = -Math.PI / 2 + 2 * Math.PI * frac * (i + 1) / seg;
            Line(rt, cx + (float)(Math.Cos(a1) * r), cy + (float)(Math.Sin(a1) * r),
                 cx + (float)(Math.Cos(a2) * r), cy + (float)(Math.Sin(a2) * r), c, lw);
        }
    }

    // ---------------- 单元格 ----------------
    static string F0(object v) { return Convert.ToInt32(Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture))).ToString(); }
    static string F1(object v) { return Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("0.0", CultureInfo.InvariantCulture); }

    static string FmtTokens(object v)
    {
        if (v == null) return "—";
        double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
        if (d >= 1e6) return (d / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M";
        if (d >= 1e3) return (d / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + "k";
        return F0(d);
    }

    // 标题最多 6 个字符：超出时前 5 字加省略号，避免窄条压到状态栏右侧控件。
    static string ShortTitle(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= 6) return s;
        return s.Substring(0, 5) + "…";
    }

    static Dictionary<string, string[]> BuildCells(Dictionary<string, object> s)
    {
        var d = new Dictionary<string, string[]>();
        object busy, tps, ctx, tok, cache, cred, turns, steps, kind, title;
        s.TryGetValue("busy", out busy); s.TryGetValue("tps", out tps); s.TryGetValue("ctx_pct", out ctx);
        s.TryGetValue("total_tokens", out tok); s.TryGetValue("cache_pct", out cache);
        s.TryGetValue("credits", out cred); s.TryGetValue("turns", out turns);
        s.TryGetValue("steps", out steps); s.TryGetValue("tps_kind", out kind); s.TryGetValue("title", out title);

        bool isBusy = busy != null && Convert.ToBoolean(busy);
        string tpsTxt = "—", tpsCol = HexStr(C_LBL);
        if (tps != null)
        {
            double v = Convert.ToDouble(tps, CultureInfo.InvariantCulture);
            tpsTxt = F0(v);
            tpsCol = v >= TPS_FAST ? HexStr(C_FAST) : (v < TPS_SLOW ? HexStr(C_SLOW) : HexStr(C_FG));
        }
        string ctxTxt = "—", ctxCol = HexStr(C_LBL);
        if (ctx != null)
        {
            double cv = Convert.ToDouble(ctx, CultureInfo.InvariantCulture);
            ctxTxt = cv.ToString("0.0", CultureInfo.InvariantCulture) + "%";
            ctxCol = cv >= CTX_HOT ? HexStr(C_CRIT) : (cv >= CTX_MID ? HexStr(C_WARN) : HexStr(C_ACCENT));
        }

        d["dot"] = new[] { "", HexStr(isBusy ? C_OK : C_IDLE) };
        d["title"] = new[] { ShortTitle(Convert.ToString(title) ?? ""), HexStr(C_TITLE) };
        d["clock"] = new[] { "", HexStr(C_ICON) };
        d["turns"] = new[] { (turns == null ? "—" : Convert.ToString(turns)) + "轮 " +
                             (steps == null ? "—" : Convert.ToString(steps)) + "步 ·", HexStr(C_FG) };
        d["tps"] = new[] { tpsTxt, tpsCol };
        d["tpsU"] = new[] { "tok/s", HexStr(C_LBL) };
        d["tpsK"] = new[] { Convert.ToString(kind) ?? "", HexStr(C_LBL) };
        d["db"] = new[] { "", HexStr(C_ICON) };
        d["tok"] = new[] { FmtTokens(tok), HexStr(tok == null ? C_LBL : C_FG) };
        d["cacheL"] = new[] { "tok · 缓存命中", HexStr(C_LBL) };
        d["cache"] = new[] { cache == null ? "—" : F0(cache) + "%", HexStr(cache == null ? C_LBL : C_FG) };
        d["ring"] = new[] { "", HexStr(C_ACCENT) };
        d["ctx"] = new[] { ctxTxt, ctxCol };
        d["credL"] = new[] { "credits", HexStr(C_LBL) };
        d["cred"] = new[] { cred == null ? "—" : F1(cred), HexStr(cred == null ? C_LBL : C_FG) };
        return d;
    }

    static string HexStr(Color c) { return c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

    // ---------------- 窗口过程 ----------------
    static IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            case 0x0113:                       // WM_TIMER
                Tick();
                return IntPtr.Zero;
            case 0x8001:                       // 第二实例请求显示
                manualShowUntil = QmUtil.UnixNow() + 10.0;
                Tick();
                return IntPtr.Zero;
            case 0x0201:                       // WM_LBUTTONDOWN
                {
                    P.POINT pt; P.GetCursorPos(out pt);
                    P.RECT wr; P.GetWindowRect(h, out wr);
                    dragDx = pt.x - wr.l; dragDy = pt.y - wr.t;
                    dragging = true;
                    P.SetCapture(h);
                    return IntPtr.Zero;
                }
            case 0x0200:                       // WM_MOUSEMOVE
                if (dragging && ((int)w & 1) != 0)
                {
                    P.POINT pt; P.GetCursorPos(out pt);
                    P.SetWindowPos(h, (IntPtr)(pin ? -1 : -2), pt.x - dragDx, pt.y - dragDy, 0, 0,
                        0x0001 | 0x0010);
                }
                return IntPtr.Zero;
            case 0x0202:                       // WM_LBUTTONUP
                if (dragging) { dragging = false; P.ReleaseCapture(); SavePos(); }
                return IntPtr.Zero;
            case 0x0203:                       // WM_LBUTTONDBLCLK
                P.PostQuitMessage(0);
                return IntPtr.Zero;
            case 0x0205:                       // WM_RBUTTONUP
                ShowMenu();
                return IntPtr.Zero;
            case 0x0010:                       // WM_CLOSE
                P.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return P.DefWindowProcW(h, msg, w, l);
    }

    // ---------------- 托盘 ----------------
    static void SetupTray()
    {
        try
        {
            trayIcon = LoadTrayIcon(true);
            trayMenu = new ContextMenuStrip();
            ToolStripMenuItem showItem = new ToolStripMenuItem("显示悬浮条");
            showItem.Click += delegate { ShowBar(); };
            pinItem = new ToolStripMenuItem("总在最前");
            pinItem.Click += delegate
            {
                pin = !pin;
                P.SetWindowPos(hwnd, (IntPtr)(pin ? -1 : -2), 0, 0, 0, 0, 0x0001 | 0x0010);
                SaveFlag("pin", pin);
                pinItem.Checked = pin;
            };
            followItem = new ToolStripMenuItem("只在 Qoder 前台时显示");
            followItem.Click += delegate
            {
                follow = !follow;
                SaveFlag("follow", follow);
                followItem.Checked = follow;
                SyncVisible();
            };
            autoItem = new ToolStripMenuItem("开机自启");
            autoItem.Click += delegate
            {
                SetAutostart(!IsAutostart());
                autoItem.Checked = IsAutostart();
            };
            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { P.PostQuitMessage(0); };

            trayMenu.Items.Add(showItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(pinItem);
            trayMenu.Items.Add(followItem);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(autoItem);
            trayMenu.Items.Add(exitItem);
            trayMenu.Opening += delegate
            {
                pinItem.Checked = pin;
                followItem.Checked = follow;
                autoItem.Checked = IsAutostart();
            };

            tray = new NotifyIcon();
            tray.Icon = trayIcon;
            tray.Text = "QoderMonitor";
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { ShowBar(); };
            tray.Visible = true;
        }
        catch (Exception e) { Log("tray setup failed: " + e.Message); }
    }

    static Icon LoadTrayIcon(bool on)
    {
        string name = on ? "monitor-on.ico" : "monitor-off.ico";
        try
        {
            Stream s = typeof(App).Assembly.GetManifestResourceStream(name);
            if (s != null) { using (s) return new Icon(s); }
        }
        catch { }
        try
        {
            string p = Path.Combine(Path.Combine(Dir, "icons"), name);
            if (File.Exists(p)) return new Icon(p);
        }
        catch { }
        return SystemIcons.Application;
    }

    static void ShowBar()
    {
        manualShowUntil = QmUtil.UnixNow() + 10.0;
        Tick();
    }

    static bool IsAutostart()
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", false))
            {
                return k != null && k.GetValue("QoderMonitor") != null;
            }
        }
        catch { return false; }
    }

    static void SetAutostart(bool on)
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (k == null) return;
                if (on)
                    k.SetValue("QoderMonitor", "\"" + Application.ExecutablePath + "\" --autostart");
                else
                    k.DeleteValue("QoderMonitor", false);
            }
        }
        catch (Exception e) { Log("autostart failed: " + e.Message); }
    }

    static void ShowMenu()
    {
        IntPtr m = P.CreatePopupMenu();
        P.AppendMenuW(m, 0x0000, (IntPtr)101, "退出（也可双击窄条）");
        P.AppendMenuW(m, 0x0000 | (pin ? 0x0008u : 0u), (IntPtr)102, "总在最前");
        P.AppendMenuW(m, 0x0000 | (follow ? 0x0008u : 0u), (IntPtr)103, "只在 Qoder 前台时显示");
        P.POINT pt; P.GetCursorPos(out pt);
        int cmd = P.TrackPopupMenu(m, 0x0100 | 0x0002, pt.x, pt.y, 0, hwnd, IntPtr.Zero);
        P.PostMessageW(hwnd, 0, IntPtr.Zero, IntPtr.Zero);   // 菜单收不干净时补一发
        P.DestroyMenu(m);
        if (cmd == 101) P.PostQuitMessage(0);
        else if (cmd == 102)
        {
            pin = !pin;
            P.SetWindowPos(hwnd, (IntPtr)(pin ? -1 : -2), 0, 0, 0, 0, 0x0001 | 0x0010);
            SaveFlag("pin", pin);
        }
        else if (cmd == 103)
        {
            follow = !follow;
            SaveFlag("follow", follow);
            SyncVisible();
        }
    }

    static void SaveFlag(string key, bool v)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            Dictionary<string, object> d;
            try { d = NewSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(PosFile)); }
            catch { d = new Dictionary<string, object>(); }
            d[key] = v;
            File.WriteAllText(PosFile, NewSerializer().Serialize(d));
        }
        catch { }
    }

    static void Shutdown()
    {
        try
        {
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            if (trayMenu != null) { trayMenu.Dispose(); trayMenu = null; }
            if (trayIcon != null) { trayIcon.Dispose(); trayIcon = null; }
        }
        catch { }
        P.Release(fmtCjk); P.Release(fmtLat); P.Release(dwrite); P.Release(d2dFactory);
        P.Release(visual); P.Release(target); P.Release(surface); P.Release(compDevice);
        P.Release(dxgiDev); P.Release(d3dCtx); P.Release(d3dDevice);
        if (mutex != IntPtr.Zero) P.CloseHandle(mutex);
    }
}
