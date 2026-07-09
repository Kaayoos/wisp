using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace Wisp.Services
{
    public class KeyboardHookManager : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private readonly LowLevelKeyboardProc _proc;
        private IntPtr _hookId = IntPtr.Zero;
        private volatile List<int> _hotkeyKeys = new();
        private volatile bool _isRebindingMode = false;

        private readonly HashSet<int> _currentlyPressed = new();
        private readonly List<int> _rebindPressed = new();
        private bool _hotkeyTriggeredActive = false;

        // The low-level keyboard hook lives on its OWN dedicated, always-pumping thread - never the UI
        // thread. A WH_KEYBOARD_LL callback is serviced on the thread that installed the hook, and while
        // that thread is busy Windows stalls delivery of EVERY keystroke system-wide (up to
        // LowLevelHooksTimeout, ~300ms each). When the hook rode on the WPF UI thread, any UI-thread hitch -
        // starting the recorder (incl. its 800ms startup probe), assembling a clip, or the stop+start
        // monitor re-target on alt-tab (a multi-second freeze) - froze the whole machine's keyboard for
        // that long, felt in games as delayed input and keys that "stick" down. A dedicated pump means app
        // and UI work can never delay game input again.
        private Thread? _hookThread;
        private Dispatcher? _hookDispatcher;
        private readonly object _lifecycleLock = new();

        public event Action? HotkeyTriggered;
        
        /// <summary>
        /// Fired when a new hotkey combination is finalized. Args: List of VK codes, displayText
        /// </summary>
        public event Action<List<int>, string>? HotkeyRebound;

        /// <summary>
        /// Fired live while keys are being pressed during rebinding to show progress. Args: currentDisplayText
        /// </summary>
        public event Action<string>? HotkeyReboundLive;

        public KeyboardHookManager()
        {
            _proc = HookCallback;
        }

        public void RegisterHotkey(List<int> keys)
        {
            _hotkeyKeys = keys;
            
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                bool isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                Logger.Info($"Registering hotkey: {FormatHotkeyListText(keys)} (RunAsAdmin: {isAdmin})");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to check Admin privileges during hotkey registration.", ex);
            }

            EnsureHookThread();
        }

        public void UnregisterHotkey() => StopHookThread();

        public void StartRebindingMode()
        {
            Logger.Info("Starting hotkey rebinding mode.");
            EnsureHookThread();
            // _rebindPressed is otherwise only touched on the hook thread; clear + arm it THERE to avoid a
            // cross-thread List race with a callback that may be running concurrently. BeginInvoke (not
            // Invoke) so the UI thread never blocks on the hook thread here - the user hasn't pressed a key
            // yet, so the sub-millisecond arming gap is harmless.
            var disp = _hookDispatcher;
            if (disp != null) disp.BeginInvoke(new Action(() => { _currentlyPressed.Clear(); _rebindPressed.Clear(); _isRebindingMode = true; }));
            else { _currentlyPressed.Clear(); _rebindPressed.Clear(); _isRebindingMode = true; }
        }

        public void StopRebindingMode()
        {
            Logger.Info("Stopping hotkey rebinding mode.");
            _isRebindingMode = false;
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            IntPtr hMod = GetModuleHandle(null);
            Logger.Info($"Installing low-level keyboard hook. Module handle: {hMod}");
            IntPtr hookId = SetWindowsHookEx(WH_KEYBOARD_LL, proc, hMod, 0);
            if (hookId == IntPtr.Zero)
            {
                int errorCode = Marshal.GetLastWin32Error();
                Logger.Error($"SetWindowsHookEx failed with error code: {errorCode}");
            }
            else
            {
                Logger.Info($"Keyboard hook installed successfully (Hook ID: {hookId}).");
            }
            return hookId;
        }

        /// <summary>
        /// Starts (idempotently) the dedicated background thread that installs the low-level hook and pumps
        /// its message loop. Hosting the hook here - off the UI thread - keeps global keyboard input
        /// responsive no matter how busy the UI thread gets (recorder start/stop, clip assembly, etc.).
        /// Blocks briefly until the hook + dispatcher are live so callers can rely on them.
        /// </summary>
        private void EnsureHookThread()
        {
            lock (_lifecycleLock)
            {
                if (_hookThread != null && _hookThread.IsAlive) return;

                var ready = new ManualResetEventSlim(false);
                var t = new Thread(() =>
                {
                    _hookDispatcher = Dispatcher.CurrentDispatcher;
                    _hookId = SetHook(_proc);
                    ready.Set();
                    Dispatcher.Run(); // pump messages so the LL hook is serviced; returns on InvokeShutdown
                    // The pump has ended (Unregister/Dispose): unhook on the SAME thread that installed it.
                    if (_hookId != IntPtr.Zero)
                    {
                        UnhookWindowsHookEx(_hookId);
                        _hookId = IntPtr.Zero;
                    }
                })
                {
                    IsBackground = true,
                    Name = "Wisp Input Hook",
                };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                _hookThread = t;
                ready.Wait(2000); // let the hook install before returning
            }
        }

        /// <summary>Stops the hook thread: ends its message pump (which unhooks the hook) and joins it.</summary>
        private void StopHookThread()
        {
            lock (_lifecycleLock)
            {
                var disp = _hookDispatcher;
                var t = _hookThread;
                if (t == null) return;

                Logger.Info("Stopping keyboard hook thread.");
                try { disp?.InvokeShutdown(); } catch { /* dispatcher already gone */ }
                try { if (t.IsAlive) t.Join(2000); } catch { }

                _hookThread = null;
                _hookDispatcher = null;
                _hookId = IntPtr.Zero;
            }
        }

        private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        /// <summary>
        /// Is this hotkey key held RIGHT NOW? Reads the real hardware state via GetAsyncKeyState rather than
        /// our accumulated <see cref="_currentlyPressed"/> set. The set silently drifts whenever a key-up is
        /// missed - which happens routinely on Alt+Tab, focus changes and the secure desktop - leaving a
        /// phantom "stuck" key that made an Alt-based hotkey (e.g. Alt+K) false-fire on every later Alt press
        /// (once per alt-tab). The hardware state can't get stuck. Modifiers match either L/R side, as before.
        /// The key whose key-down we're processing is forced true (<paramref name="justPressedVk"/>), since
        /// GetAsyncKeyState may not yet reflect that very key inside its own hook callback.
        /// </summary>
        private bool IsHotkeyKeyHeld(int key, int justPressedVk)
        {
            if (SameKey(key, justPressedVk)) return true;
            return key switch
            {
                0x11 or 0xA2 or 0xA3 => IsDown(0x11) || IsDown(0xA2) || IsDown(0xA3), // Ctrl
                0x12 or 0xA4 or 0xA5 => IsDown(0x12) || IsDown(0xA4) || IsDown(0xA5), // Alt
                0x10 or 0xA0 or 0xA1 => IsDown(0x10) || IsDown(0xA0) || IsDown(0xA1), // Shift
                0x5B or 0x5C         => IsDown(0x5B) || IsDown(0x5C),                 // Win
                _                    => IsDown(key),
            };
        }

        /// <summary>True if two VKs are the same key, treating the L/R variants of a modifier as equal.</summary>
        private static bool SameKey(int a, int b)
        {
            if (a == b) return true;
            int ga = ModifierGroup(a);
            return ga != 0 && ga == ModifierGroup(b);
        }

        private static int ModifierGroup(int vk) => vk switch
        {
            0x11 or 0xA2 or 0xA3 => 1, // Ctrl
            0x12 or 0xA4 or 0xA5 => 2, // Alt
            0x10 or 0xA0 or 0xA1 => 3, // Shift
            0x5B or 0x5C         => 4, // Win
            _ => 0,
        };

        /// <summary>
        /// Whether the full hotkey combo is currently held, judged by real hardware state.
        /// Pass <paramref name="justPressedVk"/> = the key-down being processed (or -1 for a pure state check
        /// on key-up / re-arm).
        /// </summary>
        private bool MatchesHotkey(int justPressedVk)
        {
            if (_hotkeyKeys.Count == 0) return false;
            foreach (var key in _hotkeyKeys)
            {
                if (!IsHotkeyKeyHeld(key, justPressedVk)) return false;
            }
            return true;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int vkCode = Marshal.ReadInt32(lParam);

                bool isKeyDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
                bool isKeyUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;

                if (isKeyDown)
                {
                    _currentlyPressed.Add(vkCode);

                    if (_isRebindingMode)
                    {
                        if (!_rebindPressed.Contains(vkCode))
                        {
                            _rebindPressed.Add(vkCode);
                            string currentText = FormatHotkeyListText(_rebindPressed);
                            HotkeyReboundLive?.Invoke(currentText);
                        }
                        return (IntPtr)1;
                    }

                    if (_hotkeyKeys.Count > 0)
                    {
                        if (MatchesHotkey(vkCode))
                        {
                            if (!_hotkeyTriggeredActive)
                            {
                                _hotkeyTriggeredActive = true;
                                Logger.Info("Hotkey combination triggered!");
                                HotkeyTriggered?.Invoke();
                            }
                            return (IntPtr)1; // swallow the trigger key while the combo is held
                        }
                        // Combo not (or no longer) fully held per real hardware state - re-arm so the next
                        // genuine press fires. This is what stops a phantom "stuck" key (a key-up missed
                        // during Alt+Tab) from re-triggering the hotkey on every subsequent Alt press.
                        _hotkeyTriggeredActive = false;
                    }
                }
                else if (isKeyUp)
                {
                    _currentlyPressed.Remove(vkCode);

                    if (_isRebindingMode)
                    {
                        if (_currentlyPressed.Count == 0 && _rebindPressed.Count > 0)
                        {
                            _isRebindingMode = false;
                            var finalizedKeys = new List<int>(_rebindPressed);
                            string finalName = FormatHotkeyListText(finalizedKeys);
                            Logger.Info($"Hotkey rebound finalized: {finalName}");
                            HotkeyRebound?.Invoke(finalizedKeys, finalName);
                        }
                        return (IntPtr)1;
                    }

                    // Re-arm once the combo is no longer fully held. Judged by real hardware state (-1 = no
                    // forced key), so a missed earlier key-up can't wedge the trigger permanently on.
                    if (!MatchesHotkey(-1))
                    {
                        _hotkeyTriggeredActive = false;
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public static string GetKeyDisplayName(int vkCode)
        {
            if (vkCode == 0x11 || vkCode == 0xA2 || vkCode == 0xA3) return "Ctrl";
            if (vkCode == 0x12 || vkCode == 0xA4 || vkCode == 0xA5) return "Alt";
            if (vkCode == 0x10 || vkCode == 0xA0 || vkCode == 0xA1) return "Shift";
            if (vkCode == 0x5B || vkCode == 0x5C) return "Win";

            if (vkCode >= 0x70 && vkCode <= 0x87)
            {
                return "F" + (vkCode - 0x70 + 1);
            }

            return vkCode switch
            {
                0x08 => "Backspace",
                0x09 => "Tab",
                0x0D => "Enter",
                0x1B => "Escape",
                0x20 => "Space",
                0x21 => "PageUp",
                0x22 => "PageDown",
                0x23 => "End",
                0x24 => "Home",
                0x25 => "Left",
                0x26 => "Up",
                0x27 => "Right",
                0x28 => "Down",
                0x2C => "PrintScreen",
                0x2D => "Insert",
                0x2E => "Delete",
                0x6A => "NumPad*",
                0x6B => "NumPad+",
                0x6D => "NumPad-",
                0x6E => "NumPad.",
                0x6F => "NumPad/",
                0x90 => "NumLock",
                0x91 => "ScrollLock",
                0xBA => ";",
                0xBB => "=",
                0xBC => ",",
                0xBD => "-",
                0xBE => ".",
                0xBF => "/",
                0xC0 => "`",
                0xDB => "[",
                0xDC => "\\",
                0xDD => "]",
                0xDE => "'",
                _ => vkCode >= 0x30 && vkCode <= 0x5A
                    ? ((char)vkCode).ToString()
                    : $"Key{vkCode:X2}"
            };
        }

        public static string FormatHotkeyListText(List<int> keys)
        {
            var list = new List<string>();
            foreach (var key in keys)
            {
                string name = GetKeyDisplayName(key);
                if (!list.Contains(name))
                    list.Add(name);
            }
            return string.Join(" + ", list);
        }

        public void Dispose()
        {
            UnregisterHotkey();
        }
    }
}
