using System.Runtime.InteropServices;

namespace Usage;

// Host inside the desktop shell, so normal apps always cover the widget.
sealed class DesktopHost
{
    IntPtr host;
    public bool IsAttached(Form widget) => host != IntPtr.Zero && IsWindow(host) && GetParent(widget.Handle) == host;
    public void Detach(Form widget)
    {
        if (!IsAttached(widget)) return;
        var point = widget.PointToScreen(Point.Empty);
        SetParent(widget.Handle, IntPtr.Zero);
        var style = GetWindowLongPtr(widget.Handle, -16).ToInt64();
        SetWindowLongPtr(widget.Handle, -16, new IntPtr((style & ~0x40000000L) | 0x80000000L));
        host = IntPtr.Zero;
        SetWindowPos(widget.Handle, IntPtr.Zero, point.X, point.Y, 0, 0, 0x35);
    }
    public void Attach(Form widget)
    {
        if (host != IntPtr.Zero && IsWindow(host) && GetParent(widget.Handle) == host) return;
        IntPtr desktop = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            desktop = window;
            return false;
        }, IntPtr.Zero);
        if (desktop == IntPtr.Zero)
        {
            SetWindowPos(widget.Handle, new IntPtr(1), 0, 0, 0, 0, 0x13);
            return;
        }
        var point = widget.PointToScreen(Point.Empty);
        var style = GetWindowLongPtr(widget.Handle, -16).ToInt64();
        SetWindowLongPtr(widget.Handle, -16, new IntPtr((style & ~0x80000000L) | 0x40000000L));
        SetParent(widget.Handle, desktop);
        if (GetParent(widget.Handle) != desktop)
        {
            SetWindowLongPtr(widget.Handle, -16, new IntPtr(style));
            SetWindowPos(widget.Handle, new IntPtr(1), 0, 0, 0, 0, 0x13);
            return;
        }
        host = desktop;
        MapWindowPoints(IntPtr.Zero, host, ref point, 1);
        SetWindowPos(widget.Handle, IntPtr.Zero, point.X, point.Y, 0, 0, 0x31);
    }

    delegate bool EnumWindow(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr window, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] static extern int MapWindowPoints(IntPtr from, IntPtr to, ref Point point, uint count);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
