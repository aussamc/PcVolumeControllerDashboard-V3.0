#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App.Platform;

/// <summary>
/// Windows global-hotkey service: registers system-wide hotkeys with
/// <c>RegisterHotKey</c> against a dedicated message-only window running its own
/// message loop on a background thread, and raises <see cref="HotkeyPressed"/> on
/// <c>WM_HOTKEY</c>. The Avalonia counterpart of the WPF host's HwndSource AddHook +
/// RegisterAllHotkeys, isolated from Avalonia's own window/message loop.
///
/// <c>RegisterHotKey</c> must run on the thread that owns the message queue the
/// hotkey messages are delivered to, so all (un)registration is marshalled onto the
/// pump thread via a posted message; callers just set the desired set and poke it.
/// </summary>
internal sealed class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    private const int WmHotkey = 0x0312;
    private const int WmApp = 0x8000;   // "apply the pending registration set"
    private const int WmClose = 0x0010;
    private static readonly IntPtr HwndMessage = new(-3); // message-only parent

    public event Action<int>? HotkeyPressed;

    private readonly Thread _pumpThread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly WndProc _wndProc;      // kept alive so the delegate isn't GC'd
    private readonly string _className = "PcVcHotkeys_" + Guid.NewGuid().ToString("N");

    private IntPtr _hwnd;
    private volatile IReadOnlyList<HotkeyRegistration> _desired = Array.Empty<HotkeyRegistration>();
    private readonly List<int> _registered = new(); // touched only on the pump thread
    private volatile bool _disposed;

    public WindowsGlobalHotkeyService()
    {
        _wndProc = WindowProc;
        _pumpThread = new Thread(PumpThread)
        {
            IsBackground = true,
            Name = "GlobalHotkeyPump",
        };
        _pumpThread.SetApartmentState(ApartmentState.STA);
        _pumpThread.Start();
        _ready.Wait(2000); // let the window exist before the first RegisterAll poke
    }

    public void RegisterAll(IReadOnlyList<HotkeyRegistration> registrations)
    {
        _desired = registrations ?? Array.Empty<HotkeyRegistration>();
        Poke();
    }

    public void UnregisterAll()
    {
        _desired = Array.Empty<HotkeyRegistration>();
        Poke();
    }

    private void Poke()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        PostMessage(_hwnd, WmApp, IntPtr.Zero, IntPtr.Zero);
    }

    // ── Pump thread ────────────────────────────────────────────────────────────

    private void PumpThread()
    {
        IntPtr hInstance = GetModuleHandle(null);

        var wc = new WNDCLASS
        {
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = _className,
        };
        if (RegisterClass(ref wc) == 0)
        {
            _ready.Set();
            return; // can't register a class → give up quietly (Null-like behaviour)
        }

        _hwnd = CreateWindowEx(0, _className, string.Empty, 0, 0, 0, 0, 0,
                               HwndMessage, IntPtr.Zero, hInstance, IntPtr.Zero);
        _ready.Set();
        if (_hwnd == IntPtr.Zero)
            return;

        // Apply whatever set was requested before the window existed.
        ApplyDesired();

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        // Loop ended (WM_QUIT) — tear the window/class down on this thread.
        foreach (int id in _registered)
            UnregisterHotKey(_hwnd, id);
        _registered.Clear();
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        UnregisterClass(_className, hInstance);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmHotkey:
                try { HotkeyPressed?.Invoke(wParam.ToInt32()); } catch { }
                return IntPtr.Zero;

            case WmApp:
                ApplyDesired();
                return IntPtr.Zero;

            case WmClose:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Replaces the registered set with <see cref="_desired"/>. Pump thread only.</summary>
    private void ApplyDesired()
    {
        if (_hwnd == IntPtr.Zero) return;

        foreach (int id in _registered)
            UnregisterHotKey(_hwnd, id);
        _registered.Clear();

        foreach (HotkeyRegistration r in _desired)
        {
            if (r.VirtualKey == 0) continue;
            if (RegisterHotKey(_hwnd, r.Id, (uint)r.Modifiers, (uint)r.VirtualKey))
                _registered.Add(r.Id);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_hwnd != IntPtr.Zero)
                PostMessage(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            _pumpThread.Join(2000);
        }
        catch { /* best-effort teardown */ }
        _ready.Dispose();
    }

    // ── Win32 interop ──────────────────────────────────────────────────────────

    private delegate IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
#endif
