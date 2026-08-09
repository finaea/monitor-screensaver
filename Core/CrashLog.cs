using System.IO;

namespace MonitorDim.Core;

/// <summary>
/// Writes anything that would otherwise vanish to %APPDATA%\MonitorDim\error.log.
///
/// Swallowing exceptions silently (which an earlier version did) turns a crash into
/// "the window just didn't appear", so every catch site that intentionally continues
/// should still record why.
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppSettings.Directory, "error.log");

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    public static void Write(string context, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.Directory);

                using var w = new StreamWriter(FilePath, append: true);
                w.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}");

                var e = ex;
                var depth = 0;

                while (e is not null && depth++ < 6)
                {
                    w.WriteLine($"    {e.GetType().FullName}: {e.Message}");

                    foreach (var line in (e.StackTrace ?? string.Empty).Split('\n'))
                        if (!string.IsNullOrWhiteSpace(line))
                            w.WriteLine($"      {line.TrimEnd()}");

                    e = e.InnerException;
                    if (e is not null) w.WriteLine("    --- inner ---");
                }

                w.WriteLine();
            }
        }
        catch
        {
            // logging must never be the thing that kills us
        }
    }

    /// <summary>
    /// Runs a delegate that native code invokes. An exception crossing back into the
    /// unmanaged caller raises STATUS_FATAL_USER_CALLBACK_EXCEPTION (0xC000041D) and
    /// terminates the process immediately — no dispatcher handler ever sees it.
    /// </summary>
    public static void GuardCallback(string context, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Write($"callback: {context}", ex);
        }
    }
}
