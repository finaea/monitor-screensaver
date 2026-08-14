using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorScreenSaver.Core;

public enum OverlayMode
{
    /// <summary>Fully opaque black. On OLED this drives the pixels to zero emission.</summary>
    TrueBlack,

    /// <summary>Partially opaque black, so the screen stays readable underneath.</summary>
    Dim,

    /// <summary>
    /// A looping muted video instead of black. Still protects against burn-in — motion
    /// spreads the wear — just less effectively than true black, and more the darker
    /// and busier the clip is.
    /// </summary>
    Video,
}

public enum VideoStretch
{
    /// <summary>Keep aspect ratio, letterbox with black bars (Stretch.Uniform).</summary>
    Fit,

    /// <summary>Keep aspect ratio, crop to cover the whole screen (Stretch.UniformToFill).</summary>
    Fill,

    /// <summary>Ignore aspect ratio, distort to the screen (Stretch.Fill).</summary>
    Stretch,
}

/// <summary>
/// The overlay appearance for one display (or for all of them, when per-display
/// configuration is off). A mutable record: value equality is what the overlay
/// rebuild logic keys on.
/// </summary>
public sealed record MonitorConfig
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OverlayMode Mode { get; set; } = OverlayMode.TrueBlack;

    /// <summary>How dark the Dim overlay is, in percent. 100 would equal true black.</summary>
    public int DimPercent { get; set; } = 75;

    /// <summary>Absolute path of the video played in Video mode.</summary>
    public string? VideoPath { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoStretch VideoStretch { get; set; } = VideoStretch.Fit;

    /// <summary>Overlay alpha actually applied. Video is always an opaque window.</summary>
    [JsonIgnore]
    public byte Alpha => Mode == OverlayMode.Dim
        ? (byte)Math.Clamp(DimPercent * 255 / 100, 13, 255)
        : (byte)255;

    /// <summary>Dim at 100% collapses to an ordinary opaque window, same as true black.</summary>
    [JsonIgnore]
    public bool Translucent => Mode == OverlayMode.Dim && Alpha < 255;

    public MonitorConfig Clamped()
    {
        DimPercent = Math.Clamp(DimPercent, 5, 100);
        if (string.IsNullOrWhiteSpace(VideoPath)) VideoPath = null;
        return this;
    }

    /// <summary>Short human summary for the display list ("dim 75%", "video · fit").</summary>
    [JsonIgnore]
    public string Summary => Mode switch
    {
        OverlayMode.Dim => $"dim {DimPercent}%",
        OverlayMode.Video => VideoPath is null
            ? "video · no file"
            : $"video · {Path.GetFileName(VideoPath)} · {VideoStretch.ToString().ToLowerInvariant()}",
        _ => "true black",
    };
}

public sealed class AppSettings
{
    // ---- Overlay appearance ----------------------------------------------------
    //
    // Mode/DimPercent stay as root JSON properties for compatibility with settings
    // files written before per-display configuration existed. Together with
    // VideoPath/VideoStretch they form the "all displays" config; PerMonitor holds
    // per-display overrides keyed by StableId, used only when PerMonitorConfig is on.

    /// <summary>True black stops burn-in accrual outright; dim only slows it.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OverlayMode Mode { get; set; } = OverlayMode.TrueBlack;

    /// <summary>How dark the Dim overlay is, in percent. 100 would equal true black.</summary>
    public int DimPercent { get; set; } = 75;

    /// <summary>Video played in Video mode, when configuration is shared.</summary>
    public string? VideoPath { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoStretch VideoStretch { get; set; } = VideoStretch.Fit;

    /// <summary>When true, each display uses its own <see cref="PerMonitor"/> entry.</summary>
    public bool PerMonitorConfig { get; set; }

    /// <summary>Per-display overrides, keyed by the display's stable hardware id.</summary>
    public Dictionary<string, MonitorConfig> PerMonitor { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The config that applies to every display when per-display is off.</summary>
    public MonitorConfig GlobalConfig() => new()
    {
        Mode = Mode,
        DimPercent = DimPercent,
        VideoPath = VideoPath,
        VideoStretch = VideoStretch,
    };

    public void ApplyGlobal(MonitorConfig cfg)
    {
        Mode = cfg.Mode;
        DimPercent = cfg.DimPercent;
        VideoPath = cfg.VideoPath;
        VideoStretch = cfg.VideoStretch;
    }

    /// <summary>The effective config for one display.</summary>
    public MonitorConfig ConfigFor(string stableId) =>
        PerMonitorConfig && PerMonitor.TryGetValue(stableId, out var o) ? o : GlobalConfig();

    /// <summary>The override for one display, created from the global config on first use.</summary>
    public MonitorConfig OverrideFor(string stableId)
    {
        if (!PerMonitor.TryGetValue(stableId, out var o))
        {
            o = GlobalConfig();
            PerMonitor[stableId] = o;
        }

        return o;
    }

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorScreenSaver");

    /// <summary>Settings home before the rename; migrated from on first run.</summary>
    public static string LegacyDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorDim");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    /// <summary>Idle seconds before a managed display is blanked. Default matches the usual 5 min.</summary>
    public int IdleTimeoutSeconds { get; set; } = 300;

    /// <summary>Engine tick. 250 ms keeps wake latency imperceptible at negligible cost.</summary>
    public int PollIntervalMs { get; set; } = 250;

    /// <summary>Stable ids of displays this app is allowed to blank.</summary>
    public List<string> ManagedDisplayIds { get; set; } = [];

    // ---- Category 1: things Windows treats as user activity -------------------

    /// <summary>Keyboard and mouse via GetLastInputInfo. Always on; Windows always honours it.</summary>
    [JsonIgnore]
    public bool TrackInput => true;

    /// <summary>
    /// Windows lists "changing window focus" as an activity that resets the idle timer,
    /// and GetLastInputInfo does not report it. Hooked separately.
    /// </summary>
    public bool TrackForegroundChanges { get; set; } = true;

    // ---- Category 2: explicit display power requests --------------------------

    /// <summary>
    /// Honour ES_DISPLAY_REQUIRED held by any process (Parsec, Steam, OBS, video players).
    /// This is what makes the app match Windows rather than just guess.
    /// </summary>
    public bool HonourDisplayRequests { get; set; } = true;

    /// <summary>
    /// Short names (parsec.exe) whose DISPLAY power requests are ignored. Attribution
    /// comes from powercfg /requests, which needs elevation — without admin rights the
    /// aggregate flag cannot be blamed on anyone, so the blacklist has no effect.
    /// </summary>
    public List<string> BlacklistedRequesters { get; set; } = [];

    public bool IsBlacklisted(string shortName) =>
        BlacklistedRequesters.Any(b => string.Equals(b, shortName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when attribution is available and every current DISPLAY holder is
    /// blacklisted — i.e. the aggregate ES_DISPLAY_REQUIRED flag should be ignored.
    /// </summary>
    public bool BlacklistCovers(PowerSnapshot snapshot)
    {
        if (BlacklistedRequesters.Count == 0 || !snapshot.Available) return false;

        var any = false;
        foreach (var r in snapshot.Display)
        {
            any = true;
            if (!IsBlacklisted(r.ShortName)) return false;
        }

        return any;
    }

    // ---- Beyond Windows (clearly-labelled extras) -----------------------------

    /// <summary>Windows does not do this by itself; it protects exclusive-fullscreen swapchains.</summary>
    public bool NeverBlankDuringFullscreen { get; set; } = true;

    /// <summary>
    /// Treat audible audio as activity, the way oled_aegis treats media. Off by default:
    /// someone listening to music typically *wants* the screens blanked.
    /// </summary>
    public bool NeverBlankDuringAudio { get; set; }

    // ---- Startup / lifecycle --------------------------------------------------

    public bool StartWithWindows { get; set; }

    /// <summary>Start elevated (scheduled task) so the requester name list works from boot.</summary>
    public bool StartElevated { get; set; }

    public static AppSettings Load()
    {
        try
        {
            MigrateLegacyFile();

            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null) return loaded.Sanitised();
            }
        }
        catch
        {
            // corrupt or unreadable settings should never stop the app from running
        }

        return new AppSettings();
    }

    /// <summary>Carries settings over from %APPDATA%\MonitorDim after the rename.</summary>
    private static void MigrateLegacyFile()
    {
        try
        {
            var legacy = Path.Combine(LegacyDirectory, "settings.json");
            if (File.Exists(FilePath) || !File.Exists(legacy)) return;

            System.IO.Directory.CreateDirectory(Directory);
            File.Copy(legacy, FilePath);
        }
        catch
        {
            // best effort; a fresh default is acceptable
        }
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Sanitised(), JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // non-fatal
        }
    }

    private AppSettings Sanitised()
    {
        IdleTimeoutSeconds = Math.Clamp(IdleTimeoutSeconds, 10, 24 * 60 * 60);
        PollIntervalMs = Math.Clamp(PollIntervalMs, 100, 5000);
        DimPercent = Math.Clamp(DimPercent, 5, 100);
        if (string.IsNullOrWhiteSpace(VideoPath)) VideoPath = null;
        ManagedDisplayIds = ManagedDisplayIds.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();

        BlacklistedRequesters = BlacklistedRequesters
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        PerMonitor = PerMonitor
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Clamped(), StringComparer.OrdinalIgnoreCase);

        return this;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
