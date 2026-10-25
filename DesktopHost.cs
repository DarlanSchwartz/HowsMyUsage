using System.Runtime.InteropServices;

namespace Usage;

// Keep a normal popup owned by the desktop. Reparenting into Explorer can
// reset the process DPI context and break painting on other Windows builds.
sealed class DesktopHost
{
    IntPtr host;
    public bool IsAttached(Form widget) => host != IntPtr.Zero && IsWindow(host) &&
        GetWindowLongPtr(widget.Handle, -8) == host &&
        (GetWindowLongPtr(widget.Handle, -16).ToInt64() & 0x40000000L) == 0;
    public void Detach(Form widget)
    {
        if (IsAttached(widget)) SetWindowLongPtr(widget.Handle, -8, IntPtr.Zero);
        host = IntPtr.Zero;
    }
    public void Attach(Form widget)
    {
        if (!IsAttached(widget))
        {
            host = GetShellWindow();
            if (host != IntPtr.Zero) SetWindowLongPtr(widget.Handle, -8, host);
        }
        Lower(widget);
        widget.Invalidate(true);
    }

    public void Lower(Form widget) => SetWindowPos(widget.Handle, new IntPtr(1), 0, 0, 0, 0, 0x13);

    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
