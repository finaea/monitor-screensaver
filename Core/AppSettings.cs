using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorDim.Core;

public enum OverlayMode
{
    /// <summary>Fully opaque black. On OLED this drives the pixels to zero emission.</summary>
    TrueBlack,

    /// <summary>Partially opaque black, so the screen stays readable underneath.</summary>
    Dim,
}

public sealed class AppSettings
{
    /// <summary>True black stops burn-in accrual outright; dim only slows it.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OverlayMode Mode { get; set; } = OverlayMode.TrueBlack;

    /// <summary>How dark the Dim overlay is, in percent. 100 would equal true black.</summary>
    public int DimPercent { get; set; } = 75;

    /// <summary>Overlay alpha actually applied to the layered window.</summary>
    [JsonIgnore]
    public byte OverlayAlpha => Mode == OverlayMode.TrueBlack
        ? (byte)255
        : (byte)Math.Clamp(DimPercent * 255 / 100, 13, 255);

    public static string Directory => Path.Combine(
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

    // ---- Beyond Windows (clearly-labelled extras) -----------------------------

    /// <summary>Windows does not do this by itself; it protects exclusive-fullscreen swapchains.</summary>
    public bool NeverBlankDuringFullscreen { get; set; } = true;

    // ---- Startup / lifecycle --------------------------------------------------

    public bool StartWithWindows { get; set; }

    /// <summary>Start elevated (scheduled task) so the requester name list works from boot.</summary>
    public bool StartElevated { get; set; }

    public static AppSettings Load()
    {
        try
        {
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
        ManagedDisplayIds = ManagedDisplayIds.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        return this;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
