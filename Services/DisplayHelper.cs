using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using WForms = System.Windows.Forms;

namespace Wisp.Services
{
    /// <summary>
    /// One shared notion of "a monitor" for the whole app. Wraps WinForms' <c>Screen.AllScreens</c>
    /// (already the app's monitor source for notifications) plus a couple of Win32 calls, so the
    /// recorder (which display to capture), the auto-follow watcher (did the game move displays?), and
    /// the notification windows (place at a physical rect under per-monitor DPI) all agree.
    ///
    /// Coordinates are PHYSICAL pixels of the virtual desktop. Since Wisp is per-monitor-DPI aware
    /// (see app.manifest), Screen bounds are already true physical pixels - exactly what gdigrab,
    /// SetWindowPos and ddagrab need.
    /// </summary>
    public static class DisplayHelper
    {
        /// <summary>A single display: its stable device key, geometry, ddagrab output index and DPI scale.</summary>
        public sealed record MonitorTarget(
            string DeviceName,   // e.g. "\\.\DISPLAY2" - stable key persisted in settings
            bool IsPrimary,
            int Index,           // position in Screen.AllScreens; also used as the ddagrab output_idx
            int X, int Y,        // physical top-left on the virtual desktop (can be negative)
            int Width, int Height,
            double DpiScale)     // effective DPI / 96 (1.0 = 100%, 1.5 = 150%)
        {
            public int DdaOutputIndex => Index;
        }

        /// <summary>All displays, ordered as Windows enumerates them (Screen.AllScreens order).</summary>
        public static IReadOnlyList<MonitorTarget> GetMonitors()
        {
            var list = new List<MonitorTarget>();
            try
            {
                var screens = WForms.Screen.AllScreens;
                for (int i = 0; i < screens.Length; i++)
                {
                    var s = screens[i];
                    var b = s.Bounds;
                    list.Add(new MonitorTarget(s.DeviceName, s.Primary, i, b.X, b.Y, b.Width, b.Height,
                        GetDpiScaleForScreen(s)));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"DisplayHelper.GetMonitors failed: {ex.Message}");
            }
            return list;
        }

        /// <summary>
        /// Resolves the monitor to record. <paramref name="setting"/> is "Auto" (follow the active
        /// game/foreground window given by <paramref name="followWindow"/>) or a stored device name.
        /// Always returns a usable target - falls back to the primary, then a 1080p stand-in.
        /// </summary>
        public static MonitorTarget ResolveTarget(string? setting, IntPtr followWindow)
        {
            var monitors = GetMonitors();
            if (monitors.Count == 0)
                return new MonitorTarget("", true, 0, 0, 0, 1920, 1080, 1.0);

            if (string.IsNullOrWhiteSpace(setting) || setting.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                var fg = GetMonitorForWindow(followWindow);
                if (fg != null) return fg;
                return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
            }

            var byName = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, setting, StringComparison.OrdinalIgnoreCase));
            return byName ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        }

        /// <summary>The monitor a window mostly sits on, or null if it can't be determined.</summary>
        public static MonitorTarget? GetMonitorForWindow(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return null;
                var screen = WForms.Screen.FromHandle(hwnd);
                return GetMonitors().FirstOrDefault(m => string.Equals(m.DeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        /// <summary>
        /// The DXGI output ordinal for a monitor, as FFmpeg's <c>ddagrab=output_idx</c> counts them - which
        /// is NOT necessarily <see cref="MonitorTarget.Index"/> (Screen.AllScreens order). ddagrab indexes
        /// <c>IDXGIAdapter::EnumOutputs</c> on the default adapter, and that order can differ from the
        /// Windows display list; when it does, feeding it the Screen index makes ddagrab capture the wrong
        /// physical monitor (or an index that doesn't exist, which fails and drops to the fallback chain).
        /// We enumerate DXGI ourselves and match by device name - <c>DXGI_OUTPUT_DESC.DeviceName</c> is the
        /// same <c>\\.\DISPLAYn</c> string as <see cref="MonitorTarget.DeviceName"/>.
        ///
        /// Best-effort and non-throwing: returns null on any failure, an empty name, or no match, so the
        /// caller keeps using the Screen index (the historical behavior - no regression). Only the default
        /// adapter (index 0, the one ddagrab uses) is consulted; a monitor driven by a second GPU is absent
        /// here and yields null, leaving the existing GDI / GPU-preference fallbacks to handle it.
        /// </summary>
        public static int? TryGetDdaGrabOutputIndex(string? deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName)) return null;
            IDXGIFactory1? factory = null;
            IDXGIAdapter1? adapter = null;
            try
            {
                var iid = typeof(IDXGIFactory1).GUID;
                if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == null) return null;
                if (factory.EnumAdapters1(0, out adapter) < 0 || adapter == null) return null;

                for (uint i = 0; ; i++)
                {
                    if (adapter.EnumOutputs(i, out IDXGIOutput? output) < 0 || output == null)
                        break; // DXGI_ERROR_NOT_FOUND: no more outputs on this adapter
                    try
                    {
                        if (output.GetDesc(out DXGI_OUTPUT_DESC desc) >= 0 &&
                            string.Equals(desc.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                            return (int)i;
                    }
                    finally { Marshal.ReleaseComObject(output); }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"DisplayHelper.TryGetDdaGrabOutputIndex('{deviceName}') failed: {ex.Message}");
            }
            finally
            {
                if (adapter != null) Marshal.ReleaseComObject(adapter);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
            return null;
        }

        /// <summary>Effective DPI scale of the monitor a screen represents (1.0 if it can't be read).</summary>
        public static double GetDpiScaleForScreen(WForms.Screen s)
        {
            try
            {
                var b = s.Bounds;
                var center = new POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 };
                IntPtr hmon = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
                if (hmon != IntPtr.Zero && GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                    return dpiX / 96.0;
            }
            catch { }
            return 1.0;
        }

        /// <summary>
        /// Moves a window to an absolute physical-pixel top-left, leaving its size to WPF. Bypasses
        /// WPF's DIP↔physical mapping, which is ambiguous across mixed-DPI monitors - so a window lands
        /// exactly where intended on any display.
        /// </summary>
        public static void MoveWindowPhysical(IntPtr hwnd, int x, int y)
        {
            try { SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE); }
            catch { }
        }

        /// <summary>Places a window at an absolute physical-pixel rectangle (position + size).</summary>
        public static void SetWindowRectPhysical(IntPtr hwnd, int x, int y, int cx, int cy)
        {
            try { SetWindowPos(hwnd, IntPtr.Zero, x, y, cx, cy, SWP_NOZORDER | SWP_NOACTIVATE); }
            catch { }
        }

        /// <summary>
        /// Forces a window to the very top of the z-order (above a fullscreen game) WITHOUT activating it -
        /// so a transient toast wins the z-order over composited (flip-model / Windows Fullscreen-
        /// Optimizations) fullscreen, where a set-once WPF Topmost can be buried when the game re-asserts
        /// its own placement. No effect on true legacy EXCLUSIVE fullscreen (that bypasses the compositor
        /// entirely - nothing but an injected overlay can draw over it, which we deliberately don't do).
        /// Safe: SWP_NOACTIVATE means we never steal focus or disturb the game window.
        /// </summary>
        public static void ForceTopMost(IntPtr hwnd)
        {
            try { SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); }
            catch { }
        }

        // ===================== Win32 =====================

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int MDT_EFFECTIVE_DPI = 0;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        // ---- DXGI output enumeration (maps a monitor to ddagrab's output_idx; see TryGetDdaGrabOutputIndex).
        // Minimal COM interop: only the methods we actually call carry real signatures. Earlier vtable slots
        // are placeholders (_0, _1, ...) that exist purely to push the called methods to the correct offset;
        // we never invoke a placeholder, so its signature is irrelevant. Slot order is verified against the
        // dxgi.h inheritance chain (IUnknown 0-2, then IDXGIObject, then the interface's own methods).
        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 ppFactory);

        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            void _0(); void _1(); void _2(); void _3();               // IDXGIObject (SetPrivateData..GetParent)
            void _4(); void _5(); void _6(); void _7(); void _8();     // IDXGIFactory (EnumAdapters..CreateSoftwareAdapter)
            [PreserveSig] int EnumAdapters1(uint Adapter, out IDXGIAdapter1 ppAdapter); // IDXGIFactory1
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            void _0(); void _1(); void _2(); void _3();               // IDXGIObject
            [PreserveSig] int EnumOutputs(uint Output, out IDXGIOutput ppOutput); // IDXGIAdapter
        }

        [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutput
        {
            void _0(); void _1(); void _2(); void _3();               // IDXGIObject
            [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC pDesc);    // IDXGIOutput
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public RECT DesktopCoordinates;
            [MarshalAs(UnmanagedType.Bool)] public bool AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
    }
}
