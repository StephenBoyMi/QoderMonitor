// bar_dcomp.cs —— QoderMonitor 的 DirectComposition 渲染端（最小样机）
//
// 为什么走这条：这台机器上 UpdateLayeredWindow 只认 color key，逐像素 alpha 不生效，
// 文字的抗锯齿边缘没法正常混合。DirectComposition 是 DWM 自己的合成器，逐像素 alpha
// 是它的本职；DirectWrite 在透明表面会自动把 ClearType 降级成灰度抗锯齿，正好。
//
// 本文件只做样机：一个 854x57 的透明置顶窗（WS_EX_NOREDIRECTIONBITMAP），
// 用 Direct2D 画一行字 + 一块 50% 红的色块（用来验证 alpha 真的生效）。
// 可拖动、双击退出。数据暂不接 bar_state.json。
//
// 编译（系统自带，无需装任何东西）：
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /out:bar_dcomp.exe bar_dcomp.cs
//
// vtable 槽位来自 Windows SDK 10.0.16299 的头文件（dcomp.h / d2d1.h / dxgi*.h / dwrite.h），
// 不要凭记忆改：QueryInterface=0、AddRef=1、Release=2，其余从 3 开始数。

using System;
using System.Runtime.InteropServices;

static class P
{
    // ---------- win32 ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int l, t, r, b; }
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
        public uint time; public int x, y;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }
    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after,
        int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr cb);
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] public static extern bool SetCapture(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string n);


    // ---------- 菜单 / 进程 / 定时器 / 互斥体 ----------
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved,
        IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int idx);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr h, IntPtr id);
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateMutexW(IntPtr sa, bool initialOwner, string name);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern uint GetLastError();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags,
        System.Text.StringBuilder buf, ref int size);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    public const uint ERROR_ALREADY_EXISTS = 183;
    public const uint WAIT_TIMEOUT = 0x102;

    // ---------- d3d / dxgi / dcomp / d2d / dwrite ----------
    [DllImport("d3d11.dll")] public static extern int D3D11CreateDevice(IntPtr adapter, int driverType,
        IntPtr software, uint flags, IntPtr levels, uint levelCount, uint sdk, out IntPtr device,
        out int featureLevel, out IntPtr context);
    [DllImport("dxgi.dll")] public static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [DllImport("dcomp.dll")] public static extern int DCompositionCreateDevice(IntPtr dxgiDevice,
        ref Guid iid, out IntPtr device);
    [DllImport("d2d1.dll")] public static extern int D2D1CreateFactory(int type, ref Guid iid,
        IntPtr options, out IntPtr factory);
    [DllImport("dwrite.dll")] public static extern int DWriteCreateFactory(int type, ref Guid iid,
        out IntPtr factory);
    [StructLayout(LayoutKind.Sequential)]
    public struct D2D1_PIXEL_FORMAT { public int format, alphaMode; }
    [StructLayout(LayoutKind.Sequential)]
    public struct D2D1_RENDER_TARGET_PROPERTIES
    {
        public int type;
        public D2D1_PIXEL_FORMAT pixelFormat;
        public float dpiX, dpiY;
        public int usage, minLevel;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct D2D1_COLOR_F { public float r, g, b, a; }
    [StructLayout(LayoutKind.Sequential)]
    public struct D2D1_POINT_2F { public float x, y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct D2D1_RECT_F { public float left, top, right, bottom; }

    // ---------- vtable 调用 ----------
    public static IntPtr Slot(IntPtr obj, int i)
    {
        return Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), i * IntPtr.Size);
    }
    public static T Fn<T>(IntPtr obj, int i) where T : class
    {
        return (T)(object)Marshal.GetDelegateForFunctionPointer(Slot(obj, i), typeof(T));
    }
    // IUnknown: 2 = Release
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint D_Release(IntPtr self);
    public static void Release(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        Fn<D_Release>(obj, 2)(obj);
    }
    // IDXGIFactory1: 12 = EnumAdapters1
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int D_EnumAdapters1(IntPtr self, uint index, out IntPtr adapter);

    // IDCompositionDevice: 3 = Commit, 6 = CreateTargetForHwnd, 7 = CreateVisual
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int D_CompCommit(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int D_CreateTarget(IntPtr self, IntPtr hwnd, int topmost, out IntPtr target);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int D_CreateVisual(IntPtr self, out IntPtr visual);

    // IDCompositionSurface: 3 = BeginDraw(RECT*, REFIID, void**, POINT*), 4 = EndDraw()
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int D_SurfaceBeginDraw(IntPtr self, ref RECT updateRect, ref Guid iid,
        out IntPtr drawSurface, out POINT offset);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int D_SurfaceEndDraw(IntPtr self);

    [StructLayout(LayoutKind.Sequential)]
    public struct D2D1_MATRIX_3X2_F { public float m11, m12, m21, m22, dx, dy; }

    // ID2D1RenderTarget: 29 = SetTransform
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void D_SetTransform(IntPtr self, ref D2D1_MATRIX_3X2_F m);
    // IDCompositionDevice: 8 = CreateSurface(width, height, DXGI_FORMAT, DXGI_ALPHA_MODE, IDCompositionSurface**)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int D_CreateSurface(IntPtr self, uint w, uint h, int fmt, int alpha, out IntPtr surface);

    // IDCompositionTarget: 3 = SetRoot
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int D_SetRoot(IntPtr self, IntPtr visual);

    // IDCompositionVisual: 3 = SetOffsetX(float), 5 = SetOffsetY(float), 15 = SetContent
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int D_SetContent(IntPtr self, IntPtr content);

    // ID2D1Factory: 15 = CreateDxgiSurfaceRenderTarget
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int D_CreateDxgiRT(IntPtr self, IntPtr surface,
        ref D2D1_RENDER_TARGET_PROPERTIES props, out IntPtr rt);

    // ID2D1RenderTarget: 7 = CreateSolidColorBrush, 27 = DrawTextLayout,
    //                    33 = SetTextAntialiasMode, 46 = Clear, 47 = BeginDraw, 48 = EndDraw
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int D_CreateBrush(IntPtr self, ref D2D1_COLOR_F color, IntPtr props, out IntPtr brush);

    [StructLayout(LayoutKind.Sequential)]
    public struct D2D1_ELLIPSE { public D2D1_POINT_2F center; public float rx, ry; }

    // ID2D1RenderTarget（槽位含 ID2D1Resource::GetFactory 的 +1）：
    // 15 DrawLine, 20 DrawEllipse, 21 FillEllipse
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void D_DrawLineAngles(IntPtr self, D2D1_POINT_2F a, D2D1_POINT_2F b,
        IntPtr brush, float width, IntPtr style);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void D_DrawEllipseAngles(IntPtr self, ref D2D1_ELLIPSE e, IntPtr brush,
        float width, IntPtr style);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void D_FillEllipseAngles(IntPtr self, ref D2D1_ELLIPSE e, IntPtr brush);
    // ID2D1RenderTarget: 27 = DrawText(text, len, format, RECT_F*, brush, options, measuringMode)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void D_DrawText(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string text,
        uint length, IntPtr format, ref D2D1_RECT_F rect, IntPtr brush, int options, int measuring);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void D_Clear(IntPtr self, ref D2D1_COLOR_F color);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void D_BeginDraw(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int D_EndDraw(IntPtr self, out ulong tag1, out ulong tag2);

    // IDWriteFactory: 15 = CreateTextFormat, 17 = CreateTextLayout
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int D_CreateTextFormat(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string family,
        IntPtr collection, int weight, int style, int stretch, float size,
        [MarshalAs(UnmanagedType.LPWStr)] string locale, out IntPtr format);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int D_CreateTextLayout(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string text,
        uint length, IntPtr format, float maxWidth, float maxHeight, out IntPtr layout);

    // IDWriteTextFormat: 3 = SetTextAlignment, 4 = SetParagraphAlignment, 5 = SetWordWrapping
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int D_SetInt(IntPtr self, int value);

    // ---------- GUID ----------
    public static Guid G(string s) { return new Guid(s); }
    public static readonly Guid IID_IDXGIDevice = G("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static readonly Guid IID_IDXGIFactory2 = G("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    public static readonly Guid IID_IDCompositionDevice = G("c37ea93a-e7aa-450d-b16f-9746cb0407f3");
    public static readonly Guid IID_ID2D1Factory = G("06152247-6f50-465a-9245-118bfd3b6007");
    public static readonly Guid IID_IDWriteFactory = G("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
    public static readonly Guid IID_IDXGISurface = G("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
}
