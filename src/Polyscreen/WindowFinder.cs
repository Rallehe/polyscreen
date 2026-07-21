namespace Polyscreen;

public record WindowInfo(IntPtr Hwnd, string Title, string Process);

public static class WindowFinder
{
    /// <summary>All visible, unowned, titled top-level windows.</summary>
    public static List<WindowInfo> TopLevelWindows()
    {
        var result = new List<WindowInfo>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (Native.IsWindowVisible(hwnd) &&
                Native.GetWindow(hwnd, Native.GW_OWNER) == IntPtr.Zero)
            {
                var title = Native.GetWindowTitle(hwnd);
                if (title.Length > 0)
                    result.Add(new WindowInfo(hwnd, title, Native.GetProcessName(hwnd)));
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Match by exact process name (with or without .exe) or title substring.</summary>
    public static WindowInfo? Find(string match)
    {
        var name = match.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? match[..^4] : match;
        var windows = TopLevelWindows();
        return windows.FirstOrDefault(w => string.Equals(w.Process, name, StringComparison.OrdinalIgnoreCase))
            ?? windows.FirstOrDefault(w => w.Title.Contains(match, StringComparison.OrdinalIgnoreCase));
    }
}
