using System.Diagnostics;
using System.Security.Principal;

namespace PCStatus;

/// <summary>
/// Small diagnostic log at %LOCALAPPDATA%\PCStatus\pcstatus.log (trimmed at startup when over 512 KB).
/// Records start/exit, errors, lock/unlock, sleep/wake and GPU changes, so an unexpected exit can be explained:
/// a clean exit or an exception is logged, while an external kill leaves only the last heartbeat.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string? FilePath = Init();

    public static string? Path => FilePath;

    private static string? Init()
    {
        try
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCStatus");
            Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "pcstatus.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 512 * 1024)
            {
                // Keep the most recent half.
                string text = File.ReadAllText(path);
                File.WriteAllText(path, text[(text.Length / 2)..]);
            }
            return path;
        }
        catch
        {
            return null;
        }
    }

    public static void Write(string message)
    {
        if (FilePath == null) return;
        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public static void Error(string where, Exception ex) => Write($"ERROR in {where}: {ex}");

    public static void Startup()
    {
        bool elevated;
        try { elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { elevated = false; }
        string version = typeof(Log).Assembly.GetName().Version?.ToString() ?? "?";
        Write($"START v{version} elevated={elevated} session={Process.GetCurrentProcess().SessionId} exe={Environment.ProcessPath}");
    }
}
