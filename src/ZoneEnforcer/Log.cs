namespace ZoneEnforcer;

/// <summary>Tiny file logger: %APPDATA%\ZoneEnforcer\log.txt.</summary>
public static class Log
{
    private static readonly object Lock = new();
    private static readonly string LogPath = Path.Combine(Config.ConfigDir, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Config.ConfigDir);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1_000_000)
                    File.Delete(LogPath);
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
