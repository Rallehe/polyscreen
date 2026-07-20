using Microsoft.Win32;

namespace Polyscreen;

/// <summary>Autostart via the per-user Run key â€” no admin rights needed.</summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Polyscreen";

    private static string CurrentCommand => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) != null;
        }
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, CurrentCommand);
        Log.Write($"startup enabled: {CurrentCommand}");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Write("startup disabled");
    }

    /// <summary>If autostart points at an old exe location, quietly repoint it at this one.
    /// Also migrates the legacy "ZoneEnforcer" entry from before the app was renamed.</summary>
    public static void HealPathIfStale()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key == null) return;
        if (key.GetValue("ZoneEnforcer") != null)
        {
            key.DeleteValue("ZoneEnforcer", throwOnMissingValue: false);
            key.SetValue(ValueName, CurrentCommand);
            Log.Write($"startup entry migrated from ZoneEnforcer: {CurrentCommand}");
        }
        if (key.GetValue(ValueName) is string existing && existing != CurrentCommand)
        {
            key.SetValue(ValueName, CurrentCommand);
            Log.Write($"startup path updated: {existing} -> {CurrentCommand}");
        }
    }
}
