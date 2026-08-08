using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyAiGen;

public sealed class GlobalHotkeyManager : IDisposable
{
    public readonly record struct HotkeyCombo(int Key1, int? Key2, int? Key3)
    {
        public readonly bool IsEmpty => Key1 == 0 && Key2 is null && Key3 is null;

        public override readonly string ToString()
        {
            if (IsEmpty) return "(none)";
            var parts = new List<string>(3) { KeyName(Key1) };
            if (Key2.HasValue) parts.Add(KeyName(Key2.Value));
            if (Key3.HasValue) parts.Add(KeyName(Key3.Value));
            return string.Join(" + ", parts);
        }

        public static string KeyName(int vk) => vk switch
        {
            0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter",
            0x10 or 0xA0 or 0xA1 => "Shift", 0x11 or 0xA2 or 0xA3 => "Ctrl", 0x12 or 0xA4 or 0xA5 => "Alt",
            0x1B => "Esc", 0x20 => "Space", 0x2E => "Delete",
            0x5B or 0x5C => "Win",
            >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x60 and <= 0x69 => $"Numpad {vk - 0x60}",
            0x6A => "Numpad *", 0x6B => "Numpad +", 0x6D => "Numpad -",
            0x6E => "Numpad .", 0x6F => "Numpad /",
            0x90 => "NumLock", 0x91 => "ScrollLock",
            0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-",
            0xBE => ".", 0xBF => "/", 0xC0 => "`",
            0xDB => "[", 0xDC => @"\", 0xDD => "]", 0xDE => "'",
            _ => $"VK({vk:X})"
        };
    }

    public event Action<int>? HotkeyTriggered;

    private nint _hookId;
    private readonly Dictionary<int, HotkeyCombo> _hotkeys = new();
    private readonly HashSet<int> _keysDown = new();
    private LowLevelKeyboardProc? _proc;
    private bool _disposed;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    public void Register(int overlayId, HotkeyCombo combo)
    {
        if (combo.IsEmpty)
        {
            Unregister(overlayId);
            return;
        }
        _hotkeys[overlayId] = combo;
        EnsureHook();
    }

    public void Unregister(int overlayId)
    {
        _hotkeys.Remove(overlayId);
        if (_hotkeys.Count == 0) RemoveHook();
    }

    public HotkeyCombo? GetCombo(int overlayId)
    {
        return _hotkeys.TryGetValue(overlayId, out var c) ? c : null;
    }

    private void EnsureHook()
    {
        if (_hookId != nint.Zero) return;
        _proc = HookProc;
        var mod = GetModuleHandle(typeof(GlobalHotkeyManager).Module.Name);
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, mod, 0);
        if (_hookId == nint.Zero)
            _proc = null;
    }

    private void RemoveHook()
    {
        if (_hookId == nint.Zero) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = nint.Zero;
        _proc = null;
    }

    private nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var vkCode = Marshal.ReadInt32(lParam);
            bool down = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            bool up = wParam == WM_KEYUP || wParam == WM_SYSKEYUP;

            if (down) _keysDown.Add(vkCode);
            else if (up) _keysDown.Remove(vkCode);
            else return CallNextHookEx(nint.Zero, nCode, wParam, lParam);

            if (down)
            {
                foreach (var (id, combo) in _hotkeys)
                {
                    if (AllKeysDown(combo))
                    {
                        var before = new HashSet<int>(_keysDown) { vkCode };
                        before.Remove(vkCode);
                        if (!AllKeysDown(combo, before))
                        {
                            HotkeyTriggered?.Invoke(id);
                            return (nint)1;
                        }
                    }
                }
            }
        }
        return CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    private bool AllKeysDown(HotkeyCombo combo, HashSet<int>? keys = null)
    {
        keys ??= _keysDown;
        if (!keys.Contains(combo.Key1)) return false;
        if (combo.Key2 is int k2 && !keys.Contains(k2)) return false;
        if (combo.Key3 is int k3 && !keys.Contains(k3)) return false;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveHook();
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint GetModuleHandle(string lpModuleName);

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);
}
