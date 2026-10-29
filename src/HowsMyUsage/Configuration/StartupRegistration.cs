using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Usage;

static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string Name = "UsageWidget";
    static string UserId => WindowsIdentity.GetCurrent().User!.Value;
    internal static string TaskName => "HowsMyUsage-" + UserId;
    static string Executable => Path.Combine(AppContext.BaseDirectory, "Usage.exe");

    sealed class Scheduler : IDisposable
    {
        readonly dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        public dynamic Root { get; }
        public Scheduler() { service.Connect(); Root = service.GetFolder("\\"); }
        public dynamic? Task
        {
            get
            {
                try { return Root.GetTask(TaskName); }
                catch (Exception error) when ((uint)error.HResult is 0x80070002 or 0x8004130F) { return null; }
            }
        }
        public dynamic NewTask() => service.NewTask(0);
        public void Dispose() { Marshal.ReleaseComObject(Root); Marshal.ReleaseComObject(service); }
    }

    public static bool Enabled
    {
        get
        {
            try
            {
                using var scheduler = new Scheduler();
                dynamic? task = scheduler.Task;
                if (task != null)
                    return task.Enabled && string.Equals((string)task.Definition.Actions.Item(1).Path, Executable, StringComparison.OrdinalIgnoreCase);
            }
            catch (COMException) { }
            return LegacyEnabled();
        }
    }

    static bool LegacyEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(Name) is not string command) return false;
        var script = Path.Combine(AppContext.BaseDirectory, "startup.ps1");
        if (!command.Equals($"\"{Executable}\" --startup", StringComparison.OrdinalIgnoreCase) &&
            !command.Contains($"-File \"{script}\"", StringComparison.OrdinalIgnoreCase)) return false;
        using var approval = Registry.CurrentUser.OpenSubKey(ApprovalKey);
        return approval?.GetValue(Name) is not byte[] state || state.Length == 0 || state[0] is not (3 or 7);
    }

    public static void SetEnabled(bool enabled)
    {
        using var scheduler = new Scheduler();
        if (enabled)
        {
            dynamic definition = scheduler.NewTask();
            definition.RegistrationInfo.Description = "Show HowsMyUsage when this user signs in.";
            definition.Principal.UserId = UserId;
            definition.Principal.LogonType = 3; // Current interactive user, no saved password.
            definition.Principal.RunLevel = 0;
            definition.Settings.StartWhenAvailable = true;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = "PT0S";
            definition.Settings.RestartInterval = "PT1M";
            definition.Settings.RestartCount = 3;
            definition.Settings.MultipleInstances = 2;
            dynamic trigger = definition.Triggers.Create(9); // Logon, only this user.
            trigger.UserId = UserId;
            dynamic action = definition.Actions.Create(0);
            action.Path = Executable;
            action.Arguments = "--startup";
            action.WorkingDirectory = AppContext.BaseDirectory;
            scheduler.Root.RegisterTaskDefinition(TaskName, definition, 6, UserId, null, 3, null);
        }
        else if (scheduler.Task != null) scheduler.Root.DeleteTask(TaskName, 0);
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        run.DeleteValue(Name, false);
        using var approval = Registry.CurrentUser.OpenSubKey(ApprovalKey, true);
        approval?.DeleteValue(Name, false);
        using var preferences = Registry.CurrentUser.CreateSubKey(@"Software\HowsMyUsage");
        preferences.SetValue("StartWithWindows", enabled ? 1 : 0, RegistryValueKind.DWord);
    }

    public static void Check()
    {
        using var scheduler = new Scheduler();
        dynamic? previousTask = scheduler.Task;
        string? previousXml = previousTask?.Xml;
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        using var approval = Registry.CurrentUser.CreateSubKey(ApprovalKey);
        var previousCommand = run.GetValue(Name);
        var previousState = approval.GetValue(Name);
        using var preferences = Registry.CurrentUser.CreateSubKey(@"Software\HowsMyUsage");
        var previousPreference = preferences.GetValue("StartWithWindows");
        try
        {
            SetEnabled(false);
            if (Enabled) throw new Exception("Removed startup must be disabled.");
            SetEnabled(true);
            if (!Enabled) throw new Exception("Logon task registration failed.");
            dynamic task = scheduler.Task!;
            if ((string)task.Definition.Actions.Item(1).Arguments != "--startup" ||
                (int)task.Definition.Principal.LogonType != 3 ||
                (int)task.Definition.Triggers.Item(1).Type != 9 ||
                (bool)task.Definition.Settings.DisallowStartIfOnBatteries)
                throw new Exception("Incorrect startup task settings.");
            task.Enabled = false;
            if (Enabled) throw new Exception("Disabled task must appear unchecked.");
            SetEnabled(true);
            if (!Enabled) throw new Exception("Startup reactivation failed.");
        }
        finally
        {
            if (scheduler.Task != null) scheduler.Root.DeleteTask(TaskName, 0);
            if (previousXml != null) scheduler.Root.RegisterTask(TaskName, previousXml, 6, UserId, null, 3, null);
            if (previousCommand is null) run.DeleteValue(Name, false); else run.SetValue(Name, previousCommand);
            if (previousState is null) approval.DeleteValue(Name, false); else approval.SetValue(Name, previousState);
            if (previousPreference is null) preferences.DeleteValue("StartWithWindows", false);
            else preferences.SetValue("StartWithWindows", previousPreference);
        }
    }
}
