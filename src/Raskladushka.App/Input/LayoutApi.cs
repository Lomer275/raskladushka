using System.Runtime.InteropServices;
using Raskladushka.App.Native;
using Raskladushka.Core.Keys;
using Raskladushka.Core.Typing;

namespace Raskladushka.App.Input;

public sealed class LayoutApi : ILayoutApi
{
    public Layout? GetCurrent()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0) return null;
        var thread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        if (thread == 0) return null;
        return ((long)NativeMethods.GetKeyboardLayout(thread) & 0xFFFF) switch
        {
            0x0409 => Layout.En,
            0x0419 => Layout.Ru,
            _ => null
        };
    }

    public void Request(Layout target)
    {
        var count = NativeMethods.GetKeyboardLayoutList(0, null);
        if (count <= 0) return;
        var layouts = new nint[count];
        count = NativeMethods.GetKeyboardLayoutList(count, layouts);
        var language = target == Layout.En ? 0x0409 : 0x0419;
        for (var i = 0; i < count; i++)
            if (((long)layouts[i] & 0xFFFF) == language)
            {
                RequestHandle(layouts[i]);
                return;
            }
    }

    internal static IReadOnlyList<int> InstalledLanguageIds()
    {
        var count = NativeMethods.GetKeyboardLayoutList(0, null);
        if (count <= 0) return [];
        var layouts = new nint[count];
        count = NativeMethods.GetKeyboardLayoutList(count, layouts);
        if (count <= 0) return [];
        var ids = new int[count];
        for (var i = 0; i < count; i++) ids[i] = (int)((long)layouts[i] & 0xFFFF);
        return ids;
    }

    internal static void RequestHandle(nint layout)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0 || layout == 0) return;
        var thread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        if (thread == 0) return;
        var info = new NativeMethods.GUITHREADINFO { Size = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
        var target = NativeMethods.GetGUIThreadInfo(thread, ref info) && info.Focus != 0 ? info.Focus : hwnd;
        NativeMethods.PostMessageW(target, NativeMethods.WM_INPUTLANGCHANGEREQUEST, 0, layout);
    }
}
