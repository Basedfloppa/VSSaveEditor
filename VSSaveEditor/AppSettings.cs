using System.Text.Json;

namespace VSSaveEditor;

/// <summary>
///     Display settings persisted between runs (JSON in %AppData%/VSSaveEditor/settings.json).
///     Loaded at startup and written on every change (and on window close).
/// </summary>
public sealed class AppSettings
{
    private static readonly object WriteGate = new();
    private static Task _writeChain = Task.CompletedTask;
    public int RenderMode { get; set; } = TerrainView.ModeHeight;
    public int LodIndex { get; set; } = TerrainView.LodAuto;
    public bool ShowChunkGrid { get; set; } = true;
    public bool ShowHoverTooltip { get; set; } // tooltip over the map (off by default)
    public bool ShowDataTabs { get; set; } = true; // bottom panel (Chunks/Entities): show/hide
    public bool SurfaceMode { get; set; } = true;
    public int? LayerY { get; set; } // last selected Y layer (null = surface mode)

    // ---- block overlay filter (search for ores/rocks across the whole world) ----
    public string[] OverlayCodes { get; set; } = Array.Empty<string>(); // selected block codes
    public bool OverlayDim { get; set; } = true; // dim the base map under the overlay
    public bool OverlayEnabled { get; set; } // restore the overlay after a restart

    // ---- map export to PNG/JPEG/WebP ----
    public int ExportScale { get; set; } = 1; // pixels per block (1 = native quality)
    public int ExportFormat { get; set; } // 0 = PNG, 1 = JPEG, 2 = WebP (lossless)
    public int JpegQuality { get; set; } = 90; // JPEG quality (30..100)

    public int
        ExportFilterMode
    {
        get;
        set;
    } // MapFilterRender: 0 = dimmed map + highlight, 1 = full map, 2 = matches only (transparent), 3 = matches only (dark background)

    public bool ExportTransparentBg { get; set; } // transparent background instead of the editor background colour (areas without data)
    public int ExportRegionMode { get; set; } // 0 = whole world, 1 = selected chunks only

    // ---- last dialog folders: no need to open the same directory from scratch every time ----
    public string? LastOpenDir { get; set; } // last opened/loaded .vcdbs
    public string? LastSaveDir { get; set; } // "Save a copy…"
    public string? LastExportDir { get; set; } // map export to an image

    /// <summary>Recently opened saves, newest first (MRU, see RecentFileList).</summary>
    public string[] RecentFiles { get; set; } = Array.Empty<string>();

    // ---- window layout ----
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public double PanelRatio { get; set; } = 0.4; // bottom share (data panel) of the free height

    /// <summary>Disk path of the settings file.</summary>
    public static string SettingsPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VSSaveEditor");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch
        {
            /* corrupt/unavailable file - fall back to defaults */
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            // Serialization runs on the calling (UI) thread: the settings object is not thread-safe and the JSON is small.
            EnqueueWrite(JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            /* not fatal */
        }
    }

    /// <summary>
    ///     The file write goes to the background (P1-15), and the task chain keeps the "last write
    ///     wins" order: parallel File.WriteAllText calls could complete in the reverse order.
    ///     TaskScheduler.Default is explicit - from the UI thread TaskScheduler.Current would be the UI dispatcher.
    /// </summary>
    private static void EnqueueWrite(string json)
    {
        var path = SettingsPath;
        lock (WriteGate)
        {
            _writeChain = _writeChain.ContinueWith(_ =>
            {
                try
                {
                    File.WriteAllText(path, json);
                }
                catch
                {
                    /* not fatal */
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>
    ///     Waits for background writes - called on window close so the latest
    ///     settings are not lost together with the process.
    /// </summary>
    public static void Flush()
    {
        Task pending;
        lock (WriteGate)
        {
            pending = _writeChain;
        }

        try
        {
            pending.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            /* not fatal */
        }
    }
}