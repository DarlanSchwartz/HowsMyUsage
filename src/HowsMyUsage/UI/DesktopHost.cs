using System.Runtime.InteropServices;
using System.Text;

namespace Usage;

// Never own or parent the widget to Explorer: shell restarts and Show Desktop
// can hide or destroy owned popups. Desktop mode is an independent bottom window.
sealed class DesktopHost
{
    bool attached;
    public bool IsAttached(Form widget) => attached &&
        GetWindowLongPtr(widget.Handle, -8) == IntPtr.Zero &&
        (GetWindowLongPtr(widget.Handle, -16).ToInt64() & 0x40000000L) == 0;
    public void Detach(Form widget)
    {
        SetWindowLongPtr(widget.Handle, -8, IntPtr.Zero);
        attached = false;
    }
    public void Attach(Form widget)
    {
        SetWindowLongPtr(widget.Handle, -8, IntPtr.Zero);
        attached = true;
        Lower(widget);
        widget.Invalidate(true);
    }

    public void Lower(Form widget)
    {
        // HWND_BOTTOM can put the widget behind Explorer's wallpaper. Insert it
        // just above the first desktop shell window, below normal applications.
        IntPtr after = new(-2); // NOTOPMOST if the shell is temporarily unavailable.
        EnumWindows((window, _) =>
        {
            var name = new StringBuilder(64);
            GetClassName(window, name, name.Capacity);
            if (!IsWindowVisible(window) || name.ToString() is not ("WorkerW" or "Progman")) return true;
            var previous = GetWindow(window, 3); // GW_HWNDPREV
            while (previous != IntPtr.Zero && (previous == widget.Handle ||
                (GetWindowLongPtr(previous, -20).ToInt64() & 8) != 0)) previous = GetWindow(previous, 3);
            if (previous != IntPtr.Zero) after = previous;
            return false;
        }, IntPtr.Zero);
        SetWindowPos(widget.Handle, after, 0, 0, 0, 0, 0x13);
    }

    delegate bool EnumWindow(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, int command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
