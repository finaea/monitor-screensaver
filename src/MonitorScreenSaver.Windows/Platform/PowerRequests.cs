using System.Diagnostics;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace MonitorScreenSaver.Core;

// RequesterKind / PowerRequester / ExecutionState / PowerSnapshot live in
// MonitorScreenSaver.Core (PowerModels.cs). The aggregate ExecutionState reader
// lives in Platform/WindowsPlatform.cs (WindowsExecutionSource).

/// <summary>
/// Per-caller attribution via <c>powercfg /requests</c>. Requires elevation — the
/// underlying native info class is admin-only — so this degrades gracefully to the
/// aggregate <see cref="ExecutionState"/> when we are running as a normal user.
/// </summary>
public static class PowerRequestList
{
    private static readonly string[] SectionNames =
    [
        "DISPLAY", "SYSTEM", "AWAYMODE", "EXECUTION", "PERFBOOST", "ACTIVELOCKSCREEN"
    ];

    private static readonly Regex CallerLine = new(@"^\[(PROCESS|SERVICE|DRIVER)\]\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public static async Task<PowerSnapshot> QueryAsync(CancellationToken token = default)
    {
        if (!IsElevated)
            return new PowerSnapshot(false, "Requires administrator rights.", []);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = "/requests",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return new PowerSnapshot(false, "Could not start powercfg.exe.", []);

            var stdout = await proc.StandardOutput.ReadToEndAsync(token).ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync(token).ConfigureAwait(false);
            await proc.WaitForExitAsync(token).ConfigureAwait(false);

            var text = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;

            if (text.Contains("administrator", StringComparison.OrdinalIgnoreCase))
                return new PowerSnapshot(false, "Requires administrator rights.", []);

            return new PowerSnapshot(true, null, Parse(text));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PowerSnapshot(false, ex.Message, []);
        }
    }

    /// <summary>
    /// Parses the sectioned powercfg output:
    /// <code>
    /// DISPLAY:
    /// [PROCESS] \Device\HarddiskVolume4\...\chrome.exe
    /// Video Wake Lock
    ///
    /// SYSTEM:
    /// None.
    /// </code>
    /// Tolerant of unknown sections and missing reason lines.
    /// </summary>
    public static IReadOnlyList<PowerRequester> Parse(string text)
    {
        var results = new List<PowerRequester>();
        if (string.IsNullOrWhiteSpace(text)) return results;

        var section = "UNKNOWN";
        PowerRequester? pending = null;

        void FlushPending()
        {
            if (pending is not null) results.Add(pending);
            pending = null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');

            if (line.Length == 0)
            {
                FlushPending();
                continue;
            }

            if (line.EndsWith(':') && SectionNames.Contains(line.TrimEnd(':'), StringComparer.OrdinalIgnoreCase))
            {
                FlushPending();
                section = line.TrimEnd(':').ToUpperInvariant();
                continue;
            }

            if (line.Equals("None.", StringComparison.OrdinalIgnoreCase))
            {
                FlushPending();
                continue;
            }

            var m = CallerLine.Match(line);
            if (m.Success)
            {
                FlushPending();
                var kind = m.Groups[1].Value.ToUpperInvariant() switch
                {
                    "PROCESS" => RequesterKind.Process,
                    "SERVICE" => RequesterKind.Service,
                    "DRIVER" => RequesterKind.Driver,
                    _ => RequesterKind.Unknown,
                };
                pending = new PowerRequester(kind, m.Groups[2].Value.Trim(), null, section);
                continue;
            }

            // A bare line following a caller is that caller's stated reason.
            if (pending is not null)
            {
                pending = pending with { Reason = line };
                FlushPending();
            }
        }

        FlushPending();
        return results;
    }

    /// <summary>
    /// Relaunch ourselves elevated so the per-caller list becomes available.
    /// The child is told it is a relaunch so it waits for us to release the
    /// single-instance mutex instead of assuming another copy is already running.
    /// </summary>
    /// <param name="error">null if the user simply declined the UAC prompt.</param>
    public static bool TryRelaunchElevated(out Exception? error)
    {
        error = null;

        try
        {
            var exe = Environment.ProcessPath;

            if (string.IsNullOrEmpty(exe))
            {
                error = new InvalidOperationException("Could not determine the executable path.");
                return false;
            }

            var started = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--relaunch",
                UseShellExecute = true,
                Verb = "runas",
            });

            return started is not null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — the user dismissed the UAC prompt. Not an error worth shouting about.
            return false;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }
}
