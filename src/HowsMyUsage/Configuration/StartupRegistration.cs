using Microsoft.Win32;

namespace Usage;

static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string Name = "UsageWidget";
    public static string Command => $"\"{Path.Combine(AppContext.BaseDirectory, "Usage.exe")}\" --startup";

    public static bool Enabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(Name) is not string command) return false;
            var legacyScript = Path.Combine(AppContext.BaseDirectory, "startup.ps1");
            if (!command.Equals(Command, StringComparison.OrdinalIgnoreCase) &&
                !command.Contains($"-File \"{legacyScript}\"", StringComparison.OrdinalIgnoreCase)) return false;
            using var approval = Registry.CurrentUser.OpenSubKey(ApprovalKey);
            return approval?.GetValue(Name) is not byte[] state || state.Length == 0 || state[0] is not (3 or 7);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enabled) { run.DeleteValue(Name, false); return; }
        run.SetValue(Name, Command);
        // An explicit opt-in also clears a previous Task Manager disable flag.
        using var approval = Registry.CurrentUser.OpenSubKey(ApprovalKey, true);
        approval?.DeleteValue(Name, false);
    }

    public static void Check()
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        using var approval = Registry.CurrentUser.CreateSubKey(ApprovalKey);
        var previousCommand = run.GetValue(Name);
        var previousState = approval.GetValue(Name);
        try
        {
            SetEnabled(false);
            if (Enabled) throw new Exception("Removed startup must be disabled.");
            SetEnabled(true);
            if (!Enabled || !Equals(run.GetValue(Name), Command)) throw new Exception("Native startup registration failed.");
            foreach (byte state in new byte[] { 3, 7 })
            {
                var bytes = new byte[12]; bytes[0] = state;
                approval.SetValue(Name, bytes);
                if (Enabled) throw new Exception("Windows-disabled startup must appear unchecked.");
                SetEnabled(true);
                if (!Enabled) throw new Exception("Explicit opt-in must re-enable startup.");
            }
            run.SetValue(Name, $"powershell.exe -File \"{Path.Combine(AppContext.BaseDirectory, "startup.ps1")}\"");
            if (!Enabled) throw new Exception("Legacy startup registration must remain detectable during upgrades.");
            run.SetValue(Name, "\"G:\\OtherApp\\Usage.exe\" --startup");
            if (Enabled) throw new Exception("Another installation must not appear enabled for this one.");
        }
        finally
        {
            if (previousCommand is null) run.DeleteValue(Name, false);
            else run.SetValue(Name, previousCommand);
            if (previousState is null) approval.DeleteValue(Name, false);
            else approval.SetValue(Name, previousState);
        }
    }
}
