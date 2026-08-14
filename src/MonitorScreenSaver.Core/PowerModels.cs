namespace MonitorScreenSaver.Core;

// Platform-neutral power-request models. Windows fills these from
// SystemExecutionState + powercfg /requests; macOS from IOKit power assertions.

public enum RequesterKind { Process, Service, Driver, Unknown }

public sealed record PowerRequester(RequesterKind Kind, string Caller, string? Reason, string RequestType)
{
    /// <summary>Just the exe/service name, without the \Device\HarddiskVolumeN\... prefix.</summary>
    public string ShortName
    {
        get
        {
            var c = Caller;
            var slash = c.LastIndexOf('\\');
            return slash >= 0 && slash < c.Length - 1 ? c[(slash + 1)..] : c;
        }
    }
}

/// <summary>
/// Aggregate execution state — the part that works without any special rights on
/// either platform. Raw carries the platform's native bits for diagnostics; the
/// three booleans are the contract.
/// </summary>
public readonly record struct ExecutionState(bool DisplayRequired, bool SystemRequired, bool UserPresent, uint Raw);

/// <summary>
/// Per-caller attribution snapshot. On Windows this needs elevation (powercfg
/// /requests is admin-only) and degrades to Available=false without it; on macOS
/// attribution is always available.
/// </summary>
public sealed record PowerSnapshot(bool Available, string? Unavailable, IReadOnlyList<PowerRequester> Requesters)
{
    public IEnumerable<PowerRequester> Display =>
        Requesters.Where(r => r.RequestType.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase));
}
