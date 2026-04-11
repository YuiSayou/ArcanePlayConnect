using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using ArcanePlayConnect.Core.Models;
using Microsoft.UI.Dispatching;

namespace ArcanePlayConnect.Services;

/// <summary>
/// Registers global keyboard shortcuts (hotkeys) for CommandButtons using the Win32 RegisterHotKey API.
/// Shortcuts work even when the app window is not focused.
/// </summary>
public class KeyboardShortcutService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Win32 modifier flags
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly LoggingService _logger;
    private readonly Dictionary<int, string> _hotkeyToButtonId = new();
    private readonly Dictionary<string, int> _buttonIdToHotkey = new();
    private int _nextHotkeyId = 0xB000; // start from a high range to avoid collisions
    private IntPtr _hwnd;
    private bool _isInitialized;

    public event Action<string>? ShortcutTriggered;

    public KeyboardShortcutService(LoggingService logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initializes the service with the main window handle. Must be called once after the window is created.
    /// </summary>
    public void Initialize(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _isInitialized = true;
    }

    /// <summary>
    /// Processes a Win32 window message. Call from a message hook/subclass. Returns true if handled.
    /// </summary>
    public bool ProcessMessage(uint msg, nuint wParam)
    {
        if (msg == WM_HOTKEY)
        {
            var id = (int)wParam;
            if (_hotkeyToButtonId.TryGetValue(id, out var buttonId))
            {
                ShortcutTriggered?.Invoke(buttonId);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Registers a hotkey for the given button. Unregisters any previous hotkey for this button.
    /// </summary>
    public void RegisterShortcut(CommandButton button)
    {
        if (!_isInitialized || string.IsNullOrWhiteSpace(button.KeyboardShortcut))
            return;

        // Unregister existing shortcut for this button if any
        UnregisterShortcut(button.Id);

        if (!TryParseShortcut(button.KeyboardShortcut, out var modifiers, out var vk))
        {
            _logger.LogWarning($"[Hotkey] Invalid shortcut '{button.KeyboardShortcut}' for button '{button.Name}'.");
            return;
        }

        var id = _nextHotkeyId++;
        if (RegisterHotKey(_hwnd, id, modifiers | MOD_NOREPEAT, vk))
        {
            _hotkeyToButtonId[id] = button.Id;
            _buttonIdToHotkey[button.Id] = id;
            _logger.LogInfo($"[Hotkey] Registered {button.KeyboardShortcut} ? '{button.Name}'", LogCategory.System);
        }
        else
        {
            _logger.LogWarning($"[Hotkey] Failed to register {button.KeyboardShortcut} for '{button.Name}'. It may be in use by another app.");
        }
    }

    /// <summary>
    /// Unregisters the hotkey for a given button ID.
    /// </summary>
    public void UnregisterShortcut(string buttonId)
    {
        if (!_isInitialized) return;

        if (_buttonIdToHotkey.TryGetValue(buttonId, out var id))
        {
            UnregisterHotKey(_hwnd, id);
            _hotkeyToButtonId.Remove(id);
            _buttonIdToHotkey.Remove(buttonId);
        }
    }

    /// <summary>
    /// Re-registers all shortcuts from a list of buttons. Clears any previous registrations.
    /// </summary>
    public void RegisterAll(IEnumerable<CommandButton> buttons)
    {
        UnregisterAll();

        foreach (var button in buttons)
        {
            if (!string.IsNullOrWhiteSpace(button.KeyboardShortcut))
                RegisterShortcut(button);
        }
    }

    /// <summary>
    /// Unregisters all hotkeys.
    /// </summary>
    public void UnregisterAll()
    {
        if (!_isInitialized) return;

        foreach (var id in _hotkeyToButtonId.Keys.ToList())
            UnregisterHotKey(_hwnd, id);

        _hotkeyToButtonId.Clear();
        _buttonIdToHotkey.Clear();
    }

    /// <summary>
    /// Parses a shortcut string like "Ctrl+Shift+F1" into Win32 modifier and virtual key values.
    /// </summary>
    public static bool TryParseShortcut(string shortcut, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        if (string.IsNullOrWhiteSpace(shortcut))
            return false;

        var parts = shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            var mod = parts[i].ToUpperInvariant();
            switch (mod)
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= MOD_CONTROL;
                    break;
                case "SHIFT":
                    modifiers |= MOD_SHIFT;
                    break;
                case "ALT":
                    modifiers |= MOD_ALT;
                    break;
                default:
                    return false; // unknown modifier
            }
        }

        var keyStr = parts[^1].ToUpperInvariant();
        vk = KeyNameToVirtualKey(keyStr);

        return vk != 0 && modifiers != 0; // require at least one modifier
    }

    /// <summary>
    /// Converts a key name to its Win32 Virtual Key code.
    /// </summary>
    private static uint KeyNameToVirtualKey(string key) => key switch
    {
        // Function keys
        "F1"  => 0x70,
        "F2"  => 0x71,
        "F3"  => 0x72,
        "F4"  => 0x73,
        "F5"  => 0x74,
        "F6"  => 0x75,
        "F7"  => 0x76,
        "F8"  => 0x77,
        "F9"  => 0x78,
        "F10" => 0x79,
        "F11" => 0x7A,
        "F12" => 0x7B,

        // Number keys
        "0" => 0x30,
        "1" => 0x31,
        "2" => 0x32,
        "3" => 0x33,
        "4" => 0x34,
        "5" => 0x35,
        "6" => 0x36,
        "7" => 0x37,
        "8" => 0x38,
        "9" => 0x39,

        // Letter keys
        "A" => 0x41, "B" => 0x42, "C" => 0x43, "D" => 0x44,
        "E" => 0x45, "F" => 0x46, "G" => 0x47, "H" => 0x48,
        "I" => 0x49, "J" => 0x4A, "K" => 0x4B, "L" => 0x4C,
        "M" => 0x4D, "N" => 0x4E, "O" => 0x4F, "P" => 0x50,
        "Q" => 0x51, "R" => 0x52, "S" => 0x53, "T" => 0x54,
        "U" => 0x55, "V" => 0x56, "W" => 0x57, "X" => 0x58,
        "Y" => 0x59, "Z" => 0x5A,

        // Numpad
        "NUMPAD0" or "NUM0" => 0x60,
        "NUMPAD1" or "NUM1" => 0x61,
        "NUMPAD2" or "NUM2" => 0x62,
        "NUMPAD3" or "NUM3" => 0x63,
        "NUMPAD4" or "NUM4" => 0x64,
        "NUMPAD5" or "NUM5" => 0x65,
        "NUMPAD6" or "NUM6" => 0x66,
        "NUMPAD7" or "NUM7" => 0x67,
        "NUMPAD8" or "NUM8" => 0x68,
        "NUMPAD9" or "NUM9" => 0x69,

        // Special keys
        "SPACE"     => 0x20,
        "ENTER"     => 0x0D,
        "TAB"       => 0x09,
        "ESCAPE" or "ESC" => 0x1B,
        "BACKSPACE" or "BACK" => 0x08,
        "DELETE" or "DEL" => 0x2E,
        "INSERT" or "INS" => 0x2D,
        "HOME"      => 0x24,
        "END"       => 0x23,
        "PAGEUP"    => 0x21,
        "PAGEDOWN"  => 0x22,

        // Arrow keys
        "UP"    => 0x26,
        "DOWN"  => 0x28,
        "LEFT"  => 0x25,
        "RIGHT" => 0x27,

        // Punctuation / OEM
        ";" or "SEMICOLON"       => 0xBA,
        "=" or "EQUALS"          => 0xBB,
        "," or "COMMA"           => 0xBC,
        "-" or "MINUS"           => 0xBD,
        "." or "PERIOD"          => 0xBE,
        "/" or "SLASH"           => 0xBF,
        "`" or "GRAVE" or "TILDE" => 0xC0,
        "[" or "OPENBRACKET"     => 0xDB,
        "\\" or "BACKSLASH"      => 0xDC,
        "]" or "CLOSEBRACKET"    => 0xDD,
        "'" or "QUOTE"           => 0xDE,

        _ => 0
    };

    /// <summary>
    /// Formats a display-friendly shortcut string from key event data.
    /// </summary>
    public static string FormatShortcut(bool ctrl, bool shift, bool alt, Windows.System.VirtualKey key)
    {
        var keyName = VirtualKeyToName(key);
        if (string.IsNullOrEmpty(keyName))
            return string.Empty;

        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt) parts.Add("Alt");

        if (parts.Count == 0)
            return string.Empty; // require at least one modifier

        parts.Add(keyName);
        return string.Join("+", parts);
    }

    /// <summary>
    /// Converts a WinUI VirtualKey to its display name.
    /// </summary>
    public static string VirtualKeyToName(Windows.System.VirtualKey key) => key switch
    {
        >= Windows.System.VirtualKey.F1 and <= Windows.System.VirtualKey.F12
            => $"F{(int)key - (int)Windows.System.VirtualKey.F1 + 1}",

        >= Windows.System.VirtualKey.Number0 and <= Windows.System.VirtualKey.Number9
            => $"{(int)key - (int)Windows.System.VirtualKey.Number0}",

        >= Windows.System.VirtualKey.A and <= Windows.System.VirtualKey.Z
            => key.ToString(),

        >= Windows.System.VirtualKey.NumberPad0 and <= Windows.System.VirtualKey.NumberPad9
            => $"Numpad{(int)key - (int)Windows.System.VirtualKey.NumberPad0}",

        Windows.System.VirtualKey.Space     => "Space",
        Windows.System.VirtualKey.Enter     => "Enter",
        Windows.System.VirtualKey.Tab       => "Tab",
        Windows.System.VirtualKey.Escape    => "Escape",
        Windows.System.VirtualKey.Back      => "Backspace",
        Windows.System.VirtualKey.Delete    => "Delete",
        Windows.System.VirtualKey.Insert    => "Insert",
        Windows.System.VirtualKey.Home      => "Home",
        Windows.System.VirtualKey.End       => "End",
        Windows.System.VirtualKey.PageUp    => "PageUp",
        Windows.System.VirtualKey.PageDown  => "PageDown",
        Windows.System.VirtualKey.Up        => "Up",
        Windows.System.VirtualKey.Down      => "Down",
        Windows.System.VirtualKey.Left      => "Left",
        Windows.System.VirtualKey.Right     => "Right",

        _ => string.Empty
    };

    public void Dispose()
    {
        UnregisterAll();
    }
}
