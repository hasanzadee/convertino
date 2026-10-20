using System.Runtime.InteropServices;

namespace Convertino.Native;

/// <summary>
/// A plain click on the menu-bar item opens the panel, like the tray icon on Windows; right-click and Ctrl-click
/// still open the menu. Avalonia never raises TrayIcon.Clicked on macOS: its status item only has the menu, which
/// AppKit opens from the button's mouseDown:. So -[NSStatusBarButton mouseDown:] is hooked (Convertino's item is
/// the only status item in the process) and hands a Ctrl-click on to AppKit.
/// </summary>
public static unsafe class StatusItem
{
    const string Lib = "/usr/lib/libobjc.A.dylib";
    const nuint ControlKey = 1 << 18; // NSEventModifierFlagControl

    [DllImport(Lib)] static extern IntPtr class_getInstanceMethod(IntPtr cls, IntPtr sel);
    [DllImport(Lib)] static extern IntPtr class_getMethodImplementation(IntPtr cls, IntPtr sel);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.I1)] static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
    [DllImport(Lib)] static extern IntPtr method_setImplementation(IntPtr method, IntPtr imp);

    static delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> _appKit;
    static Action? _clicked;
    static IntPtr _button;

    /// <summary>Calls <paramref name="clicked"/> (on the UI thread) for a plain left click on the menu-bar item.</summary>
    public static bool OnClick(Action clicked)
    {
        _clicked = clicked;
        if (_appKit != null) return true;
        try
        {
            var cls = ObjC.Class("NSStatusBarButton");
            if (cls == IntPtr.Zero) return false;
            var sel = ObjC.Sel("mouseDown:");
            var inherited = class_getMethodImplementation(cls, sel);
            delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> hook = &MouseDown;
            // NSStatusBarButton inherits mouseDown: or has its own; either way AppKit's stays callable
            var appKit = class_addMethod(cls, sel, (IntPtr)hook, "v@:@")
                ? inherited
                : method_setImplementation(class_getInstanceMethod(cls, sel), (IntPtr)hook);
            _appKit = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)appKit;
            return true;
        }
        catch (Exception ex) { Log.Error("status item click", ex); return false; }
    }

    /// <summary>
    /// Whether a point (points, top-left origin, like CGEventGetLocation) is on the item. The global input poller
    /// sees the same press as a click outside the panel, sometimes only after the panel has opened.
    /// </summary>
    public static bool Contains(ScreenPoint p)
    {
        if (_button == IntPtr.Zero) return false;
        try
        {
            var f = ObjC.SendRect(ObjC.Send(_button, "window"), "frame");
            var screens = ObjC.Send(ObjC.Class("NSScreen"), "screens");
            if (ObjC.Count(screens) == 0) return false;
            double top = ObjC.SendRect(ObjC.At(screens, 0), "frame").Height - (f.Y + f.Height); // Cocoa counts from the bottom
            return p.X >= f.X && p.X < f.X + f.Width && p.Y >= top && p.Y < top + f.Height;
        }
        catch (Exception ex) { Log.Error("status item frame", ex); return false; }
    }

    /// <summary>Shows the item pressed while the panel is open, like the system's own menu-bar panels.</summary>
    public static void Highlight(bool on)
    {
        if (_button == IntPtr.Zero) return;
        try { ObjC.Send(_button, "highlight:", on); }
        catch (Exception ex) { Log.Error("status item highlight", ex); }
    }

    [UnmanagedCallersOnly]
    static void MouseDown(IntPtr self, IntPtr sel, IntPtr ev)
    {
        try
        {
            if (_clicked != null && (ObjC.SendUInt(ev, "modifierFlags") & ControlKey) == 0)
            {
                _button = self;
                _clicked();
                return;
            }
        }
        catch (Exception ex) { Log.Error("status item click", ex); }
        if (_appKit != null) _appKit(self, sel, ev);
    }
}
