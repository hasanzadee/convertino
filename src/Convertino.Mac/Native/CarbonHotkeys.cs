using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Convertino.Ui;

namespace Convertino.Native;

/// <summary>
/// Global shortcuts through Carbon's RegisterEventHotKey: still the system's hot-key service on current macOS (and on
/// Apple silicon), it needs no Input Monitoring or Accessibility permission, and the keys never reach the app in front.
/// The handler runs on the main thread, which is Avalonia's UI thread here.
/// </summary>
public sealed unsafe class CarbonHotkeys : IHotkeys
{
    const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    [StructLayout(LayoutKind.Sequential)] struct EventTypeSpec { public uint Class, Kind; }
    [StructLayout(LayoutKind.Sequential)] struct EventHotKeyID { public uint Signature, Id; }

    [DllImport(Carbon)] static extern IntPtr GetEventDispatcherTarget();
    [DllImport(Carbon)] static extern int InstallEventHandler(IntPtr target, IntPtr handler, uint count, EventTypeSpec* types, IntPtr userData, out IntPtr handlerRef);
    [DllImport(Carbon)] static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyID id, IntPtr target, uint options, out IntPtr hotKey);
    [DllImport(Carbon)] static extern int UnregisterEventHotKey(IntPtr hotKey);
    [DllImport(Carbon)] static extern int GetEventParameter(IntPtr evt, uint name, uint type, IntPtr actualType, nuint size, IntPtr actualSize, EventHotKeyID* data);

    static uint FourCC(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);
    const uint cmdKey = 1 << 8, shiftKey = 1 << 9, optionKey = 1 << 11, controlKey = 1 << 12;
    const int eventHotKeyExistsErr = -9878;
    static readonly uint Signature = FourCC("cvno");

    static readonly Dictionary<uint, Action> Actions = [];
    readonly List<IntPtr> _refs = [];
    static bool _installed;
    static uint _nextId = 1;

    public string? Register(Hotkey key, Action action)
    {
        // letters and digits where the current layout has them (AZERTY's A is the US Q key), F-keys where they are
        if (((key.Key.Length == 1 ? LayoutKeyCode(key.Key[0]) : null) ?? KeyCode(key.Key)) is not { } code) return L.En("no such key");
        try
        {
            if (!_installed)
            {
                var spec = new EventTypeSpec { Class = FourCC("keyb"), Kind = 5 }; // kEventHotKeyPressed
                int st = InstallEventHandler(GetEventDispatcherTarget(), (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)&OnHotKey, 1, &spec, IntPtr.Zero, out _);
                if (st != 0) return L.En("macOS refused the handler ({0})", st);
                _installed = true;
            }
            uint mods = 0;
            if (key.Mods.HasFlag(HotMods.Ctrl)) mods |= controlKey;
            if (key.Mods.HasFlag(HotMods.Alt)) mods |= optionKey;
            if (key.Mods.HasFlag(HotMods.Shift)) mods |= shiftKey;
            if (key.Mods.HasFlag(HotMods.Super)) mods |= cmdKey;
            uint id = _nextId++;
            int r = RegisterEventHotKey(code, mods, new EventHotKeyID { Signature = Signature, Id = id }, GetEventDispatcherTarget(), 0, out var hotKey);
            if (r != 0)
            {
                Log.Info($"hot key {key.Label} not registered: {r}");
                return r == eventHotKeyExistsErr ? L.En("another app uses it") : L.En("macOS refused it ({0})", r);
            }
            Actions[id] = action;
            _refs.Add(hotKey);
            Log.Info($"hot key {key.Label} registered");
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error("carbon hot keys", ex);
            return L.En("no hot-key service");
        }
    }

    public void Clear()
    {
        foreach (var r in _refs) UnregisterEventHotKey(r);
        _refs.Clear();
        Actions.Clear();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnHotKey(IntPtr callRef, IntPtr evt, IntPtr userData)
    {
        EventHotKeyID hk;
        // kEventParamDirectObject '----', typeEventHotKeyID 'hkid'
        if (GetEventParameter(evt, FourCC("----"), FourCC("hkid"), IntPtr.Zero, (nuint)sizeof(EventHotKeyID), IntPtr.Zero, &hk) != 0) return -9874; // eventNotHandledErr
        if (hk.Signature != Signature || !Actions.TryGetValue(hk.Id, out var action)) return -9874;
        try { action(); } catch (Exception ex) { Log.Error("hot key", ex); }
        return 0;
    }

    // ---------- keyboard layout ----------

    const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [DllImport(Carbon)] static extern IntPtr TISCopyCurrentASCIICapableKeyboardLayoutInputSource();
    [DllImport(Carbon)] static extern IntPtr TISGetInputSourceProperty(IntPtr source, IntPtr key);
    [DllImport(Carbon)] static extern byte LMGetKbdType();
    [DllImport(CoreServices)] static extern int UCKeyTranslate(IntPtr layout, ushort keyCode, ushort keyAction, uint modifiers, uint keyboardType, uint options, ref uint deadKeyState, nuint maxLength, out nuint length, char* chars);
    [DllImport(CoreFoundation)] static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CoreFoundation)] static extern void CFRelease(IntPtr cf);

    /// <summary>
    /// The key that types <paramref name="ch"/> in the current keyboard layout (its Latin one, for Cyrillic, Greek …), as
    /// the shortcut should follow the letter, not a US position. Null when the layout can't be read: the US table then.
    /// </summary>
    static uint? LayoutKeyCode(char ch)
    {
        IntPtr source = IntPtr.Zero;
        try
        {
            source = TISCopyCurrentASCIICapableKeyboardLayoutInputSource();
            if (source == IntPtr.Zero) return null;
            var name = Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(Carbon), "kTISPropertyUnicodeKeyLayoutData"));
            var data = TISGetInputSourceProperty(source, name);
            if (data == IntPtr.Zero) return null;
            var layout = CFDataGetBytePtr(data);
            uint keyboard = LMGetKbdType();
            char* buf = stackalloc char[4];
            char want = char.ToUpperInvariant(ch);
            // the main block first (0–50), so a digit is the top-row key, not the keypad's
            for (ushort code = 0; code < 128; code++)
            {
                uint dead = 0;
                // kUCKeyActionDisplay, no modifiers, kUCKeyTranslateNoDeadKeysMask
                if (UCKeyTranslate(layout, code, 3, 0, keyboard, 1, ref dead, 4, out var length, buf) == 0 && length == 1 && char.ToUpperInvariant(buf[0]) == want)
                {
                    if (KeyCode(ch.ToString()) is { } us && us != code) Log.Info($"hot key letter {ch}: key {code} in this keyboard layout (US {us})");
                    return code;
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            Log.Error("keyboard layout", ex);
            return null;
        }
        finally { if (source != IntPtr.Zero) CFRelease(source); }
    }

    /// <summary>ANSI virtual key codes (kVK_ANSI_*, kVK_F*): what RegisterEventHotKey takes.</summary>
    static uint? KeyCode(string key) => key switch
    {
        "A" => 0, "S" => 1, "D" => 2, "F" => 3, "H" => 4, "G" => 5, "Z" => 6, "X" => 7, "C" => 8, "V" => 9,
        "B" => 11, "Q" => 12, "W" => 13, "E" => 14, "R" => 15, "Y" => 16, "T" => 17, "1" => 18, "2" => 19, "3" => 20,
        "4" => 21, "6" => 22, "5" => 23, "9" => 25, "7" => 26, "8" => 28, "0" => 29, "O" => 31, "U" => 32, "I" => 34,
        "P" => 35, "L" => 37, "J" => 38, "K" => 40, "N" => 45, "M" => 46,
        "F1" => 122, "F2" => 120, "F3" => 99, "F4" => 118, "F5" => 96, "F6" => 97, "F7" => 98, "F8" => 100, "F9" => 101,
        "F10" => 109, "F11" => 103, "F12" => 111, "F13" => 105, "F14" => 107, "F15" => 113, "F16" => 106, "F17" => 64,
        "F18" => 79, "F19" => 80, "F20" => 90,
        _ => null,
    };
}
