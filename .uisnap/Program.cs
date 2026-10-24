using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VSCore;
using VSSaveEditor;
using VSSaveEditor.Components;
using VSSaveEditor.Components.Tabs;

internal static class Harness
{
    // Where to put screenshots: by default into the repository's artifacts/ (next to the sources),
    // overridden by the UISNAP_OUT environment variable.
    private static readonly string OutDir = Environment.GetEnvironmentVariable("UISNAP_OUT")
        ?? System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/screens/ui"));

    private static string SavePath;

    [STAThread]
    private static void Main(string[] args)
    {
        // A run is usually redirected to a file: without AutoFlush the progress of a long probe is not visible until the very end
        Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        System.IO.Directory.CreateDirectory(OutDir);
        var mode = args.Length > 0 ? args[0] : "layout";
        var save = args.Length > 1
            ? args[1]
            : "/home/user/.config/VintagestoryData/Saves/zorolins freestyle place.vcdbs";
        SavePath = save;

        if (mode == "scan")
        {
            ScanProbe(save);
            return;
        }

        if (mode == "doms")
        {
            DomainProbe(save);
            return;
        }

        if (mode == "entitydump")
        {
            EntityDump(save, args.Length > 2 ? args[2] : "moonlight");
            return;
        }

        if (mode == "celldiff")
        {
            CellDiff(save, args.Length > 2 ? args[2] : throw new ArgumentException("celldiff <orig> <copy>"));
            return;
        }
        if (mode == "areafilter")
        {
            AreaFilterProbe(save, args.Length > 2 ? args[2] : "rock-andesite");
            return;
        }


        var builder = AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
        builder.SetupWithoutStarting();

        Console.WriteLine("=== contrast (theme brushes vs window background) ===");
        ContrastProbe();

        if (mode == "layout")
        {
            LayoutProbe(save, 960, 660);
            LayoutProbe(save, 1280, 800);
            return;
        }

        if (mode == "styles")
        {
            StyleCheck();
            return;
        }

        if (mode == "tabgeom")
        {
            TabGeom(save);
            return;
        }

        if (mode == "listwidth")
        {
            ListWidth(save);
            return;
        }

        if (mode == "chunksearch")
        {
            ChunkSearch(save);
            return;
        }

        if (mode == "entityfind")
        {
            EntityFind(save);
            return;
        }

        if (mode == "itemfind")
        {
            ItemFind(save);
            return;
        }

        if (mode == "input")
        {
            InputProbe(save);
            return;
        }

        if (mode == "menu")
        {
            MenuProbe();
            return;
        }

        if (mode == "e2e")
        {
            E2E(save, args.Length > 2 ? args[2] : "/home/user/.config/VintagestoryData/Saves/zorolins freestyle place.vcdbs");
            return;
        }

        if (mode == "race")
        {
            Race(save, args.Length > 2 ? args[2] : "/home/user/.config/VintagestoryData/Saves/zorolins freestyle place.vcdbs");
            return;
        }

        if (mode == "conflict")
        {
            Conflict(save);
            return;
        }

        if (mode == "conflict2")
        {
            ConflictForced(save);
            return;
        }

        if (mode == "overlay")
        {
            Overlay(save);
            return;
        }

        if (mode == "ui2")
        {
            Ui2(save);
            return;
        }

        if (mode == "zoom")
        {
            Zoom(save);
            return;
        }

        if (mode == "zoomfit")
        {
            ZoomFit(save);
            return;
        }

        if (mode == "exportparity")
        {
            ExportParity(save);
            return;
        }

        if (mode == "selectgesture")
        {
            SelectGesture(save);
            return;
        }

        if (mode == "areas")
        {
            Areas(save);
            return;
        }

        if (mode == "layerslice")
        {
            LayerSlice(save);
            return;
        }

        if (mode == "rendermode")
        {
            RenderModeProbe(save);
            return;
        }

        if (mode == "ylayerscan")
        {
            YLayerScan(save);
            return;
        }

        if (mode == "chiseldump")
        {
            ChiselDump(save);
            return;
        }

        if (mode == "chiselcolor")
        {
            ChiselColor(save);
            return;
        }

        if (mode == "legendtheme")
        {
            LegendTheme(save);
            return;
        }

        if (mode == "darkaudit")
        {
            DarkAudit(save, switchAfterBuild: false);
            return;
        }

        if (mode == "themeswitch")
        {
            DarkAudit(save, switchAfterBuild: true);
            return;
        }

        if (mode == "drawer")
        {
            Drawer(save);
            return;
        }

        if (mode == "i18n")
        {
            I18n(save);
            return;
        }

        if (mode == "statusline")
        {
            StatusLine(save);
            return;
        }

        if (mode == "pointupdate")
        {
            PointUpdate(save);
            return;
        }

        if (mode == "cardlimits")
        {
            CardLimits(save);
            return;
        }

        if (mode == "legend")
        {
            Legend(save);
            return;
        }

        if (mode == "pending")
        {
            Pending(save);
            return;
        }

        if (mode == "diff")
        {
            DiffPreview(save);
            return;
        }

        if (mode == "players")
        {
            Players(save);
            return;
        }

        if (mode == "removeblocks")
        {
            RemoveBlocks(save);
            return;
        }
        if (mode == "replaceblocks")
        {
            ReplaceBlocks(save);
            return;
        }

        if (mode == "deletechunks")
        {
            DeleteChunksProbe(save);
            return;
        }

        if (mode == "ndlremove")
        {
            NdlRemove(save, args.Length > 2 ? args[2] : "ndlwoodentorchholder");
            return;
        }

        if (mode == "recent")
        {
            Recent(save, args.Length > 2 ? args[2] : "/home/user/.config/VintagestoryData/Saves/zorolins freestyle place.vcdbs");
            return;
        }

        UIFlow(save);
    }

    private static void ScanProbe(string save)
    {
        try
        {
            using var f = VSSaveFile.OpenReadOnly(save);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = SaveScanner.Scan(f, null, captureRaw: false);
            sw.Stop();
            Console.WriteLine($"SCAN {System.IO.Path.GetFileName(save)}: {sw.ElapsedMilliseconds} ms | chunks={r.Chunks.Count} ents={r.EntityCount} active={r.Chunks.SelectMany(c => c.Entities).Count(e => e.HasInfo)} be={r.BlockEntityCount} players={r.Players.Count}");
            Console.WriteLine("  top blocks: " + string.Join(", ", r.WorldBlockCodeCounts.OrderByDescending(k => k.Value).Take(6).Select(k => k.Key + ":" + k.Value)));

            // The tab index for "Chunks" and "Entities" is built once per scan (in the window — on publishing
            // the result, that is on the UI thread): both it and the filtering over it are measured here.
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            var cols = ChunkBrowser.Build(r);
            var rows = EntityBrowser.Build(r);
            var build = sw2.ElapsedMilliseconds;
            sw2.Restart();
            var chunkHits = ChunkBrowser.Filter(cols, ChunkQuery.Parse("wolf"));
            var ents = EntityBrowser.Filter(rows, EntityCategory.All, "wolf");
            var filter = sw2.ElapsedMilliseconds;
            sw2.Restart();
            var byContent = ChunkBrowser.Filter(cols, ChunkQuery.Parse("ore-quartz"));
            var byCoords = ChunkBrowser.Filter(cols, ChunkQuery.Parse("100 100"));
            var byCount = ChunkBrowser.Filter(cols, ChunkQuery.Parse("ent>0"));
            var filter2 = sw2.ElapsedMilliseconds;
            Console.WriteLine($"  browser index: build {build} ms (chunk columns={cols.Count}, records={rows.Count});"
                              + $" filter 'wolf' {filter} ms → {chunkHits.Count} chunk(s), {ents.Count} record(s);"
                              + $" chunk filters {filter2} ms → content {byContent.Count}, coords {byCoords.Count}, ent>0 {byCount.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("SCAN FAILED " + save + ": " + ex.Message);
        }
    }

    /// <summary>
    ///     How the rows of the "Entities" tab are named: what parsing an entity code yields on a real save.
    ///     Prints aggregates (names with a junk tail, mod domains, player names) and the rows
    ///     the filter finds — with the same <see cref="EntityBrowser" /> as the tab itself,
    ///     so "moonlight" here answers exactly what the UI would show.
    /// </summary>
    private static void EntityDump(string save, string needle)
    {
        try
        {
            using var f = VSSaveFile.OpenReadOnly(save);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = SaveScanner.Scan(f, null, captureRaw: false);
            sw.Stop();
            var rows = EntityBrowser.Build(r);
            var creatures = rows.Where(x => !x.IsBlockEntity).ToList();
            var modded = rows.Where(x => x.Domain.Length > 0 && x.Domain != "game").ToList();
            var junk = rows.Where(x => x.Name.EndsWith("-") || x.Name.EndsWith(":")).ToList();
            Console.WriteLine($"ENTITYDUMP {System.IO.Path.GetFileName(save)}: scan {sw.ElapsedMilliseconds} ms"
                              + $" | rows={rows.Count} (creatures={creatures.Count}, block entities={rows.Count - creatures.Count})"
                              + $" | custom names={rows.Count(x => x.CustomName.Length > 0)}"
                              + $" | mod domains={modded.Count} | junk names (trailing '-'/':')={junk.Count}");
            Console.WriteLine("  top names: " + string.Join(", ", EntityBrowser.TopNames(rows, 8).Select(t => $"{t.Name}:{t.Count}")));
            if (modded.Count > 0)
                Console.WriteLine("  domains: " + string.Join(", ", modded.GroupBy(x => x.Domain)
                    .OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key}:{g.Count()}")));
            if (junk.Count > 0)
                Console.WriteLine("  junk samples: " + string.Join(" | ", junk.Take(5).Select(x => x.Display)));
            foreach (var row in EntityBrowser.Filter(rows, EntityCategory.All, needle).Take(12))
                Console.WriteLine($"  [{needle}] name='{row.Name}' code='{row.Code}' custom='{row.CustomName}'"
                                  + $" kind='{row.Kind}' domain='{row.Domain}' label='{row.Label}'\n      {row.Display}");
            Console.WriteLine($"  [{needle}] matches={EntityBrowser.Filter(rows, EntityCategory.All, needle).Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("ENTITYDUMP FAILED " + save + ": " + ex.Message);
        }
    }

    /// <summary>How many world blocks belong to mods and to which domains. The scan is the same as the editor's
    /// (a per-cell histogram), so the numbers match the Find blocks tab and the removal confirmation.</summary>
    private static void DomainProbe(string save)
    {
        try
        {
            using var f = VSSaveFile.OpenReadOnly(save);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = SaveScanner.Scan(f, null, captureRaw: false);
            sw.Stop();
            var all = r.WorldBlockCodeCounts;
            var mods = all.Where(kv => kv.Key.Contains(':')).ToList();
            var byDom = mods.GroupBy(kv => kv.Key.Split(':')[0])
                .Select(g => (dom: g.Key, codes: g.Count(), cells: g.Sum(kv => kv.Value)))
                .OrderByDescending(x => x.cells)
                .ToList();
            Console.WriteLine($"DOMAINS {System.IO.Path.GetFileName(save)}: {sw.ElapsedMilliseconds} ms | chunks={r.Chunks.Count} | codes with cells={all.Count} (mod={mods.Count}) | cells solid={r.TotalSolid} fluid={r.TotalFluid}");
            Console.WriteLine($"  mod cells={mods.Sum(kv => kv.Value)} in {byDom.Count} domains; vanilla cells={all.Where(kv => !kv.Key.Contains(':')).Sum(kv => kv.Value)}");
            foreach (var g in byDom)
                Console.WriteLine($"  {g.dom,-28} codes={g.codes,-5} cells={g.cells}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("DOMAINS FAILED " + save + ": " + ex.Message);
        }
    }

    /// <summary>Cell-by-cell comparison of two saves' histograms: which codes changed their cell count after block removal.
    /// Needed to tell "exactly the selection was removed" from "something else shifted".</summary>
    private static void CellDiff(string origPath, string copyPath)
    {
        var ha = ScanHistogram(origPath);
        var hb = ScanHistogram(copyPath);
        long delta = 0, gained = 0, lost = 0;
        int nGain = 0, nLost = 0;
        var rows = new List<(long d, string code, long a, long b)>();
        foreach (var code in ha.Keys.Union(hb.Keys))
        {
            long a = ha.GetValueOrDefault(code), b = hb.GetValueOrDefault(code);
            long d = b - a;
            if (d == 0) continue;
            delta += d;
            rows.Add((d, code, a, b));
            if (d > 0) { gained += d; nGain++; } else { lost += d; nLost++; }
        }
        rows.Sort((x, y) => Math.Abs(y.d).CompareTo(Math.Abs(x.d)));
        Console.WriteLine($"CELLDIFF {System.IO.Path.GetFileName(origPath)} → copy: net={delta:+#;-#;0} | codes lost={nLost} (cells {lost}), codes gained={nGain} (cells +{gained})");
        foreach (var r in rows.Take(20))
            Console.WriteLine($"  {r.d,8:+#;-#;0}  {r.code,-44} {r.a} → {r.b}");
        var gainCodes = rows.Where(r => r.d > 0).ToList();
        Console.WriteLine("  gained codes by domain: " + string.Join(", ",
            gainCodes.GroupBy(r => r.code.Contains(':') ? r.code.Split(':')[0] : "game(vanilla)")
                     .Select(g => $"{g.Key}={g.Sum(r => r.d)}")));
    }

    private static Dictionary<string, long> ScanHistogram(string path)
    {
        using var f = VSSaveFile.OpenReadOnly(path);
        return SaveScanner.Scan(f, null, captureRaw: false).WorldBlockCodeCounts;
    }

    private static void ContrastProbe()
    {
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var bg = variant == ThemeVariant.Light ? Colors.White : Color.FromRgb(0x20, 0x20, 0x20);
            foreach (var key in new[] { "TextPrimaryBrush", "TextSecondaryBrush", "TextAccentBrush" })
            {
                var app = Application.Current;
                if (app != null && app.TryGetResource(key, variant, out var value) && value is ISolidColorBrush b)
                    Console.WriteLine($"  {variant} {key} = {b.Color} vs bg {bg} -> contrast {Contrast(b.Color, bg):0.00}");
                else
                    Console.WriteLine($"  {variant} {key} = MISSING");
            }
        }
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Lum(a), lb = Lum(b);
        var hi = Math.Max(la, lb);
        var lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Lum(Color c)
    {
        double F(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
    }

    /// <summary>
    ///     Geometry of the bottom drawer tabs: for each tab — the size of its root element and a summary
    ///     of the descendants' geometry. This is an A/B measurement for moving the tabs to Components/Tabs: a UserControl wrapper
    ///     must change neither the size nor the position of the content, so the CONTENT ROOT is compared
    ///     (for a wrapped tab — its Content), not the tab element itself in the tree.
    /// </summary>
    /// <summary>
    ///     Checks that the toolbar row style (App.axaml) actually reaches the children of a horizontal
    ///     StackPanel: an Avalonia type selector also matches descendants (Button, TextBox, ComboBox…),
    ///     not just the Control itself. A synthetic panel, no save needed.
    /// </summary>
    private static void StyleCheck()
    {
        (StackPanel Panel, TextBlock Tb, Button Btn, TextBox Box, ComboBox Cb, CheckBox Check, NumericUpDown Num) Row(
            Avalonia.Layout.Orientation o)
        {
            var sp = new StackPanel { Orientation = o, Spacing = 6 };
            var tb = new TextBlock { Text = "t" };
            var btn = new Button { Content = "b" };
            var box = new TextBox { Width = 60 };
            var cb = new ComboBox { Width = 60 };
            var check = new CheckBox { Content = "c" };
            var num = new NumericUpDown { Width = 60 };
            sp.Children.Add(tb);
            sp.Children.Add(btn);
            sp.Children.Add(box);
            sp.Children.Add(cb);
            sp.Children.Add(check);
            sp.Children.Add(num);
            return (sp, tb, btn, box, cb, check, num);
        }

        var h = Row(Avalonia.Layout.Orientation.Horizontal);
        var w = new Window { Content = h.Panel, Width = 420, Height = 200 };
        w.Show();
        Pump(0.6);
        Console.WriteLine($"[styles] horizontal row (expected Center): TextBlock={h.Tb.VerticalAlignment} "
                          + $"TextBox={h.Box.VerticalAlignment} Button={h.Btn.VerticalAlignment} ComboBox={h.Cb.VerticalAlignment} "
                          + $"CheckBox={h.Check.VerticalAlignment} NumericUpDown={h.Num.VerticalAlignment}");

        var v = Row(Avalonia.Layout.Orientation.Vertical);
        var w2 = new Window { Content = v.Panel, Width = 300, Height = 150 };
        w2.Show();
        Pump(0.6);
        Console.WriteLine($"[styles] vertical row (the Orientation condition must not fire): TextBlock={v.Tb.VerticalAlignment} "
                          + $"TextBox={v.Box.VerticalAlignment} ComboBox={v.Cb.VerticalAlignment}");

        // A local value beats the style: this is what the hex field in "Chunk data" relies on
        var local = new TextBox { Width = 60, VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Top };
        h.Panel.Children.Add(local);
        Pump(0.4);
        Console.WriteLine($"[styles] local VerticalContentAlignment=Top vs the style: {local.VerticalContentAlignment} (expected Top)");

        // Theme default for Expander: the bottom drawer has effective H=Left, V=Center (tabgeom measurement),
        // hence both Alignments are set explicitly in the markup. A bare Expander in a Grid shows
        // whether this default comes from the theme or from someone's setter — otherwise the attribute is tempting to "clean up" as redundant.
        var grid = new Grid();
        var bare = new Expander { Header = "h", Content = new TextBlock { Text = "c" } };
        grid.Children.Add(bare);
        var w3 = new Window { Content = grid, Width = 300, Height = 200 };
        w3.Show();
        Pump(0.6);
        Console.WriteLine($"[styles] bare Expander in a Grid (theme default): H={bare.HorizontalAlignment}"
                          + $" V={bare.VerticalAlignment} bounds={Fmt(bare)}");

        CloseNow(w);
        CloseNow(w2);
        w3.Close();
        Pump(0.3);
    }

    /// <summary>Entities/Items/Players: list on the left, card on the right. At start the card is wider than the list,
    /// GridSplitter moves the boundary, the empty state sits in the centre of the panel.</summary>
    private static void ListWidth(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        w.OpenStartupFile(save);
        if (!WaitLoaded(w, SaveToken(save), "Scan complete", 600))
        {
            Console.WriteLine("FAILED: load");
            CloseNow(w);
            Pump(0.5);
            return;
        }
        EnsureDataPanelOpen(w);

        var tabs = w.FindNamed<TabControl>("DataTabs")!;
        foreach (var (tab, listName, hostName, splitterName, hintName, filterName) in new[]
                 {
                     ("Entities", "EntityList", "EntityDetailHost", "EntitySplitter", "EntityEmptyHint", "EntityFilterBox"),
                     ("Items", "ItemList", "ItemDetailHost", "ItemSplitter", "ItemEmptyHint", "ItemFilterBox")
                 })
        {
            var item = tabs.Items.OfType<TabItem>().FirstOrDefault(t => (t.Header as string) == tab);
            if (item is null) { Console.WriteLine($"[listwidth] {tab}: no such tab"); continue; }
            tabs.SelectedItem = item;
            Pump(1.5);
            var list = w.FindNamed<ListBox>(listName);
            var host = w.FindNamed<Grid>(hostName);
            var splitter = w.FindNamed<GridSplitter>(splitterName);
            var hint = w.FindNamed<TextBlock>(hintName);
            var filter = w.FindNamed<TextBox>(filterName);
            if (list is null || host is null || splitter is null || hint is null || filter is null)
            {
                Console.WriteLine($"[listwidth] {tab}: missing controls");
                continue;
            }

            Console.WriteLine($"[listwidth] {tab}: list={list.Bounds.Width:0} px, detail={host.Bounds.Width:0} px, "
                              + $"list<detail={list.Bounds.Width < host.Bounds.Width}, splitter={splitter.IsVisible}, rows={list.ItemCount}");

            filter.Text = "zzz-no-such-record";
            Pump(1.2);
            var hintCentre = hint.TranslatePoint(new Point(hint.Bounds.Width / 2, hint.Bounds.Height / 2), host)
                             ?? new Point(0, 0);
            var hostCentre = new Point(host.Bounds.Width / 2, host.Bounds.Height / 2);
            Console.WriteLine($"  empty: rows={list.ItemCount} hint.visible={hint.IsVisible} "
                              + $"hint centre=({hintCentre.X:0},{hintCentre.Y:0}) "
                              + $"panel centre=({hostCentre.X:0},{hostCentre.Y:0}) "
                              + $"delta=({hintCentre.X - hostCentre.X:0},{hintCentre.Y - hostCentre.Y:0})");
            filter.Text = "";
            Pump(1.2);

            if (list.ItemCount > 0)
            {
                list.SelectedIndex = 0;
                Pump(1.0);
                Console.WriteLine($"  selection: hint.visible={hint.IsVisible}, detail.children={host.Children.Count}");
            }

            var splitterAt = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), w)
                             ?? new Point(0, 0);
            double beforeList = list.Bounds.Width, beforeDetail = host.Bounds.Width;
            w.MouseDown(splitterAt, MouseButton.Left, RawInputModifiers.None);
            w.MouseMove(new Point(splitterAt.X + 60, splitterAt.Y), RawInputModifiers.None);
            w.MouseUp(new Point(splitterAt.X + 60, splitterAt.Y), MouseButton.Left, RawInputModifiers.None);
            Pump(0.8);
            Console.WriteLine($"  resize: list {beforeList:0}→{list.Bounds.Width:0} px, "
                              + $"detail {beforeDetail:0}→{host.Bounds.Width:0} px, "
                              + $"changed={Math.Abs(list.Bounds.Width - beforeList) > 1}");
        }

        Shot(w, "listwidth");
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    private static void TabGeom(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.5);
        w.OpenStartupFile(save);
        Pump(60.0);
        var panel = w.FindNamed<Expander>("DataPanel")!;
        panel.IsExpanded = true;
        Pump(2.0);
        var tabs = w.FindNamed<TabControl>("DataTabs")!;
        Console.WriteLine($"[tabgeom] TabControl={Fmt(tabs)} items={tabs.ItemCount}");
        foreach (var item in tabs.Items.OfType<TabItem>())
        {
            tabs.SelectedItem = item;
            Pump(1.2);
            var host = Descendants(tabs).OfType<ContentPresenter>()
                .FirstOrDefault(cp => (cp.Name ?? "").Contains("SelectedContent"));
            var content = host?.Child as Control;
            var inner = content is UserControl uc ? uc.Content as Control : content;
            var kids = inner is null ? new List<Control>() : Descendants(inner).OfType<Control>().ToList();
            var sum = kids.Aggregate((X: 0.0, Y: 0.0, W: 0.0, H: 0.0),
                (a, k) => (a.X + k.Bounds.X, a.Y + k.Bounds.Y, a.W + k.Bounds.Width, a.H + k.Bounds.Height));
            Console.WriteLine($"[tabgeom] {item.Header,-18} host={Fmt(host)} content={content?.GetType().Name ?? "(none)"} "
                              + $"root={inner?.GetType().Name ?? "(none)"} {Fmt(inner)} kids={kids.Count} "
                              + $"sum=({sum.X:0},{sum.Y:0},{sum.W:0},{sum.H:0}) sha={GeomHash(kids)}");
            // The level chain from the Expander down to the tab content — once, it is the same for all tabs
            if (inner != null && ReferenceEquals(item, tabs.Items.OfType<TabItem>().First()))
                Console.WriteLine($"[tabgeom] path: {TabPath(inner, panel)}");
        }

        // Stretching the drawer with the splitter (SetPanelRatio — exactly what DragCompleted does): the tab
        // header and the content must stay pinned to the TOP of the drawer at any height.
        // Measures the Expander position inside the drawer: for a pinned block it equals the frame's BorderThickness,
        // more — the block drifted to the vertical centre. The measurement runs TWICE in ONE process (the current
        // V=Stretch and the previous V=Center), so the A/B depends neither on settings nor on the build.
        var drawer = (Control)panel.GetVisualParent()!;
        var setRatio = w.GetType().GetMethod("SetPanelRatio",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        double inset = (drawer as Border)?.BorderThickness.Top ?? 0;
        foreach (var (label, align) in new (string, Avalonia.Layout.VerticalAlignment)[]
                 {
                     ("V=Stretch (current)", Avalonia.Layout.VerticalAlignment.Stretch),
                     ("V=Center (was)", Avalonia.Layout.VerticalAlignment.Center)
                 })
        {
            panel.VerticalAlignment = align;
            var gaps = new List<string>();
            foreach (var ratio in new[] { 0.25, 0.45, 0.75 })
            {
                setRatio.Invoke(w, new object[] { ratio });
                Pump(1.2);
                var first = tabs.Items.OfType<TabItem>().First();
                tabs.SelectedItem = first;
                Pump(0.8);
                var host = Descendants(tabs).OfType<ContentPresenter>()
                    .FirstOrDefault(cp => (cp.Name ?? "").Contains("SelectedContent"));
                var top = panel.TranslatePoint(new Point(0, 0), drawer)?.Y ?? double.NaN;
                var stripY = first.TranslatePoint(new Point(0, 0), tabs)?.Y ?? double.NaN;
                gaps.Add((top - inset).ToString("0.#"));
                Console.WriteLine($"[tabgeom] stretch {label} ratio={ratio:0.00} drawer={drawer.Bounds.Height:0}"
                                  + $" expander={panel.Bounds.Height:0} top(in drawer)={top:0.#} tab header Y={stripY:0.#}"
                                  + $" content={Fmt(host)}");
                // A frame at the tallest drawer height: the difference between "pinned" and "drifted to the centre" is visible to the eye
                if (ratio > 0.7)
                    Shot(w, align == Avalonia.Layout.VerticalAlignment.Stretch
                        ? "tabtop_01_stretch_tall"
                        : "tabtop_02_center_tall");
            }

            Console.WriteLine($"[tabgeom] stretch SUMMARY {label}: gap to the top of the drawer = {string.Join("/", gaps)} px"
                              + $" (frame {inset:0.#} px subtracted; 0 — the block is pinned to the top)");
        }

        panel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        CloseNow(w);
        Pump(0.5);
    }

    /// <summary>
    ///     A geometry fingerprint of the subtree: type, name and Bounds of each node in traversal order. Comparing
    ///     the "before/after" hashes catches a shift of any control that the sum= summary might miss.
    /// </summary>
    private static string GeomHash(IEnumerable<Control> nodes)
    {
        var sb = new StringBuilder();
        foreach (var k in nodes)
        {
            sb.Append(k.GetType().Name).Append('|').Append(k.Name ?? string.Empty).Append('|')
                .Append(N(k.Bounds.X)).Append(',').Append(N(k.Bounds.Y)).Append(',')
                .Append(N(k.Bounds.Width)).Append(',').Append(N(k.Bounds.Height)).Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    ///     The chain from the tab content up to the Expander. "*" — a level created by the theme template
    ///     (TemplatedParent != null); the rest are written in the markup. This is exactly the answer to
    ///     "how deep the nesting is": you can see how many levels are added by the theme, not by our XAML.
    /// </summary>
    private static string TabPath(Control inner, Control stop)
    {
        var chain = new List<string>();
        for (Visual cur = inner; cur != null; cur = cur.GetVisualParent())
        {
            var name = cur is Control c && !string.IsNullOrEmpty(c.Name) ? c.Name! : cur.GetType().Name;
            var head = cur is Control { TemplatedParent: not null } ? name + "*" : name;
            chain.Add($"{head}[{Align(cur)}]");
            if (ReferenceEquals(cur, stop)) break;
        }

        chain.Reverse();
        return string.Join(" > ", chain);
    }

    /// <summary>
    ///     The actual (not default) alignment values of the node: you can see at which level of the chain
    ///     the container stops stretching its content — that is how you find "who is to blame" for
    ///     the tab not taking the full width.
    /// </summary>
    private static string Align(Visual v)
    {
        if (v is not Control c) return string.Empty;
        var parts = new List<string>();
        foreach (var (prop, label) in new[]
                 {
                     ("HorizontalAlignment", "H"), ("VerticalAlignment", "V"),
                     ("HorizontalContentAlignment", "HC"), ("VerticalContentAlignment", "VC")
                 })
        {
            try
            {
                if (c.GetType().GetProperty(prop)?.GetValue(c) is { } value) parts.Add($"{label}={value}");
            }
            catch
            {
                /* this type has no such property — nothing to print */
            }
        }

        return string.Join(",", parts);
    }

    private static void LayoutProbe(string save, int width, int height)
    {
        Console.WriteLine($"=== layout probe {width}x{height} ===");
        var w = new MainWindow { Width = width, Height = height };
        w.Show();
        Pump(1.5);
        Report(w, "empty");
        w.OpenStartupFile(save);
        Pump(60.0);
        Report(w, "loaded");
        Shot(w, $"layout_{width}x{height}_light");
        var dp = w.FindNamed<Expander>("DataPanel");
        if (dp != null)
        {
            dp.IsExpanded = false;
            Pump(2.0);
            Report(w, "collapsed");
            Shot(w, $"layout_{width}x{height}_collapsed");
        }
        CloseNow(w);
        Pump(0.5);
    }

    private static void Report(Window w, string phase)
    {
        Console.WriteLine($"  [{phase}] map={Fmt(w.FindNamed<Control>("Map"))} dataPanel={Fmt(w.FindNamed<Expander>("DataPanel"))} tabs={Fmt(w.FindNamed<TabControl>("DataTabs"))}");
        Console.WriteLine($"  [{phase}] statusLine={Fmt(w.FindNamed<TextBlock>("AppStatusText"))} line=\"{Cut(StatusLineOf(w))}\" progress={Fmt(w.FindNamed<ProgressBar>("LoadProgress"))} progressLabel={Fmt(w.FindNamed<TextBlock>("ProgressLabel"))}");
        var rows = new List<string>();
        foreach (var sp in Descendants(w).OfType<StackPanel>())
        {
            if (sp.Children.Count < 3 || sp.Bounds.Width < 2) continue;
            double sum = sp.Children.Sum(c => c.Bounds.Width) + (sp.Children.Count - 1) * sp.Spacing;
            if (sum > sp.Bounds.Width + 2)
                rows.Add($"overflow row(sum={sum:0} panel={sp.Bounds.Width:0}, n={sp.Children.Count}) @{sp.Bounds.Top:0},{sp.Bounds.Left:0}");
        }
        Console.WriteLine($"  [{phase}] overflowing rows: {(rows.Count == 0 ? "none" : string.Join("; ", rows))}");
    }

    /// <summary>
    ///     Looks up a control by name in the window AND in nested name scopes. After moving the bottom
    ///     drawer tabs to Components/Tabs their controls belong to the name scope of their own tab, not the window,
    ///     so window.FindControl does not see them. The window scope is checked first (it has the menu, the map,
    ///     the status line and the drawer header) — the behaviour of earlier probes is unchanged.
    /// </summary>
    private static T FindNamed<T>(this Control root, string name) where T : Control
    {
        if (root.FindControl<T>(name) is { } direct) return direct;
        foreach (var scope in root.GetLogicalDescendants().OfType<Control>())
            if (scope.FindControl<T>(name) is { } nested)
                return nested;
        foreach (var scope in root.GetVisualDescendants().OfType<Control>())
            if (scope.FindControl<T>(name) is { } nested)
                return nested;
        return null;
    }

    private static string Fmt(Control c) => c == null ? "(missing)" : $"{c.Bounds.Width:0}x{c.Bounds.Height:0} @{c.Bounds.Left:0},{c.Bounds.Top:0} vis={c.IsVisible}";

    private static void UIFlow(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.5);
        Shot(w, "01_initial_light");
        w.RequestedThemeVariant = ThemeVariant.Dark;
        Pump(1.5);
        Shot(w, "02_initial_dark");
        w.RequestedThemeVariant = ThemeVariant.Light;
        Pump(0.5);
        w.OpenStartupFile(save);
        Pump(90.0);
        Shot(w, "03_loaded_light");
        Console.WriteLine("status: " + TextOf(w, "StatusText"));
        Console.WriteLine("scan: " + TextOf(w, "ScanStatusText"));
        SetText(w, "ChunkSearchBox", "wolf");
        ClickButton(w, "Search");
        Pump(5.0);
        Shot(w, "04_search_wolf");
        Console.WriteLine("chunk search: " + (w.FindNamed<TextBlock>("ChunkSummaryLabel")?.Text ?? "(none)"));
        var results = w.FindNamed<ComboBox>("ChunkPick");
        if (results is { ItemCount: > 0 })
        {
            results.SelectedIndex = 0;
            Pump(5.0);
        }
        Shot(w, "05_chunk_data");
        Console.WriteLine("chunk status: " + TextOf(w, "ChunkStatusText"));
        var tabs = w.FindNamed<TabControl>("DataTabs");
        if (tabs != null)
        {
            for (int i = 0; i < tabs.ItemCount; i++)
            {
                tabs.SelectedIndex = i;
                Pump(1.5);
                Shot(w, "07_tab" + i);
            }
            tabs.SelectedIndex = 0;
            Pump(1.0);
        }
        w.RequestedThemeVariant = ThemeVariant.Dark;
        Pump(3.0);
        Shot(w, "08_loaded_dark");
        w.RequestedThemeVariant = ThemeVariant.Light;
        Pump(1.0);
        Console.WriteLine("DONE");
    }

    private static void InputProbe(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        w.OpenStartupFile(save);
        var deadline = DateTime.UtcNow.AddSeconds(600);
        while (DateTime.UtcNow < deadline && !TextOf(w, "StatusText").StartsWith("opened:")) Pump(1.0);
        Console.WriteLine("status: " + TextOf(w, "StatusText"));
        while (DateTime.UtcNow < deadline && !TextOf(w, "ScanStatusText").StartsWith("Scan complete")) Pump(1.0);
        Console.WriteLine("scan: " + TextOf(w, "ScanStatusText"));

        var map = w.FindNamed<Control>("Map");
        Console.WriteLine("map before: " + Fmt(map) + " lod='" + TextOf(w, "LodStatusText") + "'");
        Shot(w, "big_01_loaded");
        ReportToolbar(w);

        var p = new Point(map!.Bounds.Width / 2, map.Bounds.Height / 2);
        for (int i = 0; i < 8; i++)
        {
            w.MouseWheel(p, new Vector(0, 1), RawInputModifiers.None);
            Pump(0.5);
        }
        Console.WriteLine("after wheel x8: lod='" + TextOf(w, "LodStatusText") + "' map=" + Fmt(w.FindNamed<Control>("Map")));
        Shot(w, "big_02_zoom_in");

        w.KeyPress(Key.OemPlus, RawInputModifiers.None);
        Pump(1.0);
        w.KeyPress(Key.OemPlus, RawInputModifiers.None);
        Pump(1.0);
        Console.WriteLine("after '+' x2: lod='" + TextOf(w, "LodStatusText") + "' map=" + Fmt(w.FindNamed<Control>("Map")));
        w.KeyPress(Key.F, RawInputModifiers.None);
        Pump(1.0);
        Console.WriteLine("after 'F': lod='" + TextOf(w, "LodStatusText") + "'");

        var c = new Point(map.Bounds.Width / 2, map.Bounds.Height / 2);
        w.MouseDown(c, MouseButton.Left, RawInputModifiers.None);
        Pump(0.3);
        w.MouseUp(c, MouseButton.Left, RawInputModifiers.None);
        Pump(20.0);
        Console.WriteLine("sel: " + TextOf(w, "SelInfoText"));
        Console.WriteLine("chunk status: " + TextOf(w, "ChunkStatusText"));
        ReportToolbar(w);
        Shot(w, "big_03_chunk_selected");

        var tabs = w.FindNamed<TabControl>("DataTabs");
        if (tabs != null)
        {
            tabs.SelectedIndex = 1;
            Pump(2.0);
            Shot(w, "big_04_dump");
            tabs.SelectedIndex = 3;
            Pump(2.0);
            Shot(w, "big_05_regions");
            tabs.SelectedIndex = 4;
            Pump(2.0);
            Shot(w, "big_06_world");
            tabs.SelectedIndex = 0;
            Pump(2.0);
        }

        w.RequestedThemeVariant = ThemeVariant.Dark;
        Pump(4.0);
        Shot(w, "big_07_chunk_dark");
        w.RequestedThemeVariant = ThemeVariant.Light;
        Pump(1.0);
        Console.WriteLine("DONE");
    }

    private static void ReportToolbar(Window w)
    {
        var line = w.FindNamed<TextBlock>("AppStatusText");
        Console.WriteLine("  AppStatusText=" + Fmt(line) + " chars=" + (line?.Text?.Length ?? 0) + " text=\"" + Cut(line?.Text) + "\""
            + " (window width " + w.Width.ToString("0") + ")");
        Console.WriteLine("  ProgressLabel=" + Fmt(w.FindNamed<TextBlock>("ProgressLabel")));
        Console.WriteLine("  channels: app='" + Cut(TextOf(w, "StatusText")) + "' scan='" + Cut(TextOf(w, "ScanStatusText")) + "' chunk='" + Cut(TextOf(w, "ChunkStatusText")) + "'");
        Console.WriteLine("  tabs=" + Fmt(w.FindNamed<TabControl>("DataTabs")) + " hex=" + Fmt(w.FindNamed<TextBox>("HexEditBox")) + " save=" + Fmt(w.FindNamed<Button>("SaveCopyBtn")));
        var panel = w.FindNamed<StackPanel>("ChunkDataPanel");
        if (panel != null)
            Console.WriteLine("  cards=" + Descendants(panel).OfType<Border>().Count() + " panelH=" + panel.Bounds.Height.ToString("0"));
    }

    private static string Cut(string s) => s == null ? "" : (s.Length > 90 ? s.Substring(0, 90) + "..." : s).Replace("\n", " ");

    private static void MenuProbe()
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.5);
        w.OpenStartupFile(SavePath);
        var deadline = DateTime.UtcNow.AddSeconds(300);
        while (DateTime.UtcNow < deadline && !TextOf(w, "StatusText").StartsWith("opened:")) Pump(1.0);
        Console.WriteLine("loaded: " + TextOf(w, "StatusText"));
        var menu = Descendants(w).OfType<Menu>().FirstOrDefault();
        var settings = menu?.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == "_Settings");
        if (settings == null) { Console.WriteLine("no settings menu"); return; }
        foreach (var item in settings.Items.OfType<MenuItem>())
        {
            Console.WriteLine("item '" + item.Header + "': Items=" + item.Items.Count + " HasSubMenu=" + item.HasSubMenu);
            foreach (var it in item.Items)
                Console.WriteLine("    child: " + it?.GetType().Name + " " + Fmt(it as Control));
        }

        var renderPanel = w.FindNamed<StackPanel>("SettingsRenderPanel");
        var lodPanel = w.FindNamed<StackPanel>("SettingsLodPanel");
        var slider = w.FindNamed<Slider>("LayerSlider");
        var grid = w.FindNamed<CheckBox>("ChunkGridCheck");
        Console.WriteLine("closed: renderPanel=" + Fmt(renderPanel) + " lodPanel=" + Fmt(lodPanel) + " slider=" + Fmt(slider) + " grid=" + Fmt(grid));
        if (renderPanel != null)
            foreach (var ch in renderPanel.Children)
                Console.WriteLine("    radio '" + (ch as RadioButton)?.Content + "' " + Fmt(ch as Control));

        var render = settings.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == "Render mode");
        if (render != null)
        {
            settings.IsSubMenuOpen = true;
            Pump(0.5);
            render.IsSubMenuOpen = true;
            Pump(1.5);
            Console.WriteLine("open: renderPanel=" + Fmt(w.FindNamed<StackPanel>("SettingsRenderPanel")));
            Shot(w, "menu_render_mode");
            var blocks = Descendants(w).OfType<RadioButton>().FirstOrDefault(r => (r.Tag as string) == "1");
            if (blocks != null)
            {
                Console.WriteLine("  radio 'Blocks' found=" + Fmt(blocks) + " checked=" + blocks.IsChecked);
                blocks.IsChecked = true;
                Pump(1.5);
                Console.WriteLine("  after selecting Blocks: layerLabel='" + TextOf(w, "LayerLabel") + "'");
            }
        }
        var layer = settings.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == "Layer Y");
        if (layer != null)
        {
            settings.IsSubMenuOpen = true;
            Pump(0.5);
            layer.IsSubMenuOpen = true;
            Pump(1.5);
            var sl = w.FindNamed<Slider>("LayerSlider");
            Console.WriteLine("open Layer Y: slider=" + Fmt(sl) + " min=" + sl?.Minimum + " max=" + sl?.Maximum + " value=" + sl?.Value);
            if (sl != null)
            {
                sl.Value = Math.Round((sl.Minimum + sl.Maximum) / 2);
                Pump(2.0);
                Console.WriteLine("  after slider=" + sl.Value + ": layerLabel='" + TextOf(w, "LayerLabel") + "'");
            }
            Shot(w, "menu_layer");
        }
        Console.WriteLine("DONE");
    }

    /// <summary>
    ///     Switching the render type: radio in Settings → window _renderMode → map _mode → pixels.
    ///     Hashing only the map (not the whole window) separates "switched, but drew the same" from "did not switch".
    /// </summary>
    private static void RenderModeProbe(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(save);

        if (w.FindNamed<Control>("Map") is not TerrainView map) { Console.WriteLine("FAILED: no map"); return; }
        var panel = w.FindNamed<StackPanel>("SettingsRenderPanel");
        var modeField = typeof(TerrainView).GetField("_mode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var surfaceField = typeof(MainWindow).GetField("_surfaceMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var renderField = typeof(MainWindow).GetField("_renderMode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var end = DateTime.UtcNow.AddSeconds(900);
        while (DateTime.UtcNow < end && (map.Terrain is null || map.IsRebuilding))
        {
            Dispatcher.UIThread.RunJobs();
            try { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } catch { }
            Thread.Sleep(50);
        }
        Pump(1.0);
        Console.WriteLine($"[rendermode] {System.IO.Path.GetFileName(save)}: terrain={(map.Terrain is null ? "NONE" : $"{map.Terrain.Width}x{map.Terrain.Height}")}"
                          + $" status='{Cut(TextOf(w, "StatusText"))}'");

        void Dump(string tag)
        {
            var radios = panel is null
                ? "(no panel)"
                : string.Join(" ", panel.Children.OfType<RadioButton>().Select(r => $"{r.Tag}:{(r.IsChecked == true ? "on" : "off")}"));
            var legend = "";
            if (w.FindNamed<Control>("MapLegendHost") is ContentControl { Content: Control lc })
                legend = Cut(string.Join(" / ", Descendants(lc).OfType<TextBlock>().Select(t => t.Text)
                    .Where(t => !string.IsNullOrWhiteSpace(t)).Take(4)));
            Console.WriteLine($"[rendermode] {tag}");
            Console.WriteLine($"[rendermode]   radio[{radios}] window._renderMode={renderField.GetValue(w)} map._mode={modeField.GetValue(map)}"
                              + $" _surfaceMode={surfaceField.GetValue(w)} layerY={map.LayerY?.ToString() ?? "null"}");
            Console.WriteLine($"[rendermode]   label='{Cut(TextOf(w, "LayerLabel"))}' legend='{legend}'");
            Console.WriteLine($"[rendermode]   mapHash={MapHash(map)}");
        }

        Dump("start");

        // Reference: does the renderer itself distinguish the modes (the same ComputeRegion the map calls).
        var cr = typeof(TerrainView).GetMethod("ComputeRegion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var tm = map.Terrain!;
        foreach (var m in new[] { 0, 1, 2 })
        {
            var px = (byte[])cr.Invoke(map, new object?[]
            {
                tm, 1.0, tm.MinX + tm.Width / 2 - 128, tm.MinZ + tm.Height / 2 - 128, 256, 256, null, m
            })!;
            Console.WriteLine($"[rendermode] ComputeRegion reference mode={m}: hash={Convert.ToHexString(SHA256.HashData(px))[..16]}");
        }

        foreach (var target in new[] { 1, 0, 2, 1, 0 })
        {
            var rb = panel?.Children.OfType<RadioButton>().FirstOrDefault(r => (r.Tag as string) == target.ToString(CultureInfo.InvariantCulture));
            if (rb is null) { Console.WriteLine($"[rendermode] no radio {target}"); continue; }
            rb.IsChecked = true;
            WaitRebuild(map);
            Dump($"click '{rb.Content}' (tag {target})");
        }

        // The LOD radios are wired the same way as the render mode — check them too.
        var lodPanel = w.FindNamed<StackPanel>("SettingsLodPanel");
        foreach (var target in new[] { 3, 0 })
        {
            var rb = lodPanel?.Children.OfType<RadioButton>()
                .FirstOrDefault(r => (r.Tag as string) == target.ToString(CultureInfo.InvariantCulture));
            if (rb is null) continue;
            rb.IsChecked = true;
            WaitRebuild(map);
            Console.WriteLine($"[rendermode] LOD click '{rb.Content}' (tag {target}): map.LodIndex={map.LodIndex}"
                              + $" lod='{Cut(TextOf(w, "LodStatusText"))}' mapHash={MapHash(map)}");
        }

        // The Y slice draws real blocks. Check whether it mutes a mode switch: the radio changes, the map does not.
        if (w.FindNamed<Slider>("LayerSlider") is { } slider)
        {
            slider.Value = Math.Round(slider.Minimum + (slider.Maximum - slider.Minimum) * 0.25);
            WaitRebuild(map);
            Dump($"slice Y={slider.Value}");
            foreach (var target in new[] { 0, 1 })
            {
                var rb = panel?.Children.OfType<RadioButton>().FirstOrDefault(r => (r.Tag as string) == target.ToString(CultureInfo.InvariantCulture));
                if (rb is null) continue;
                rb.IsChecked = true;
                WaitRebuild(map);
                Dump($"mode {target} DURING slice Y={slider.Value}");
            }

            // The main consequence: choosing a mode during a slice must reach the map and survive
            // the return to the surface, otherwise the "Surface" button leaves the previous mode under a new label.
            if (w.FindNamed<Button>("SurfaceBtn") is { } surface)
            {
                surface.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(6.0);
                Dump($"after 'Surface' (expected mode {renderField.GetValue(w)})");
            }
        }

        CloseNow(w);
        Pump(0.3);
        Console.WriteLine("DONE");
    }

    /// <summary>Waits for the frame rebuild to finish. A deferred slice (DispatcherTimer) does not start immediately,
    /// so first we wait for the busy flag to appear, and only then for it to clear.</summary>
    private static void WaitRebuild(TerrainView map)
    {
        var end = DateTime.UtcNow.AddSeconds(180);
        var start = DateTime.UtcNow;
        var seenBusy = false;
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            try { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } catch { }
            if (map.IsRebuilding) seenBusy = true;
            else if (seenBusy) break;
            else if ((DateTime.UtcNow - start).TotalSeconds > 3) break; // the rebuild never started at all
            Thread.Sleep(50);
        }
        Pump(2.0);
    }

    /// <summary>Hash of the frame the map is showing right now: its own WriteableBitmap, not the
    /// window composition (that lags by one pass and is only good as a rough indicator).</summary>
    private static string MapHash(TerrainView map)
    {
        Dispatcher.UIThread.RunJobs();
        if (typeof(TerrainView).GetField("_bmp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(map)
            is not Avalonia.Media.Imaging.WriteableBitmap bmp) return "(no frame)";
        using var fb = bmp.Lock();
        var len = fb.RowBytes * fb.Size.Height;
        var buf = new byte[len];
        System.Runtime.InteropServices.Marshal.Copy(fb.Address, buf, 0, len);
        return Convert.ToHexString(SHA256.HashData(buf))[..16];
    }

    private static bool WaitFor(Window w, string ctrl, string prefix, double maxSeconds)
    {
        var end = DateTime.UtcNow.AddSeconds(maxSeconds);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            try { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } catch { }
            if (TextOf(w, ctrl).StartsWith(prefix)) return true;
            Thread.Sleep(25);
        }
        return false;
    }

    private static int CountPending(string chunkStatus)
    {
        var m = System.Text.RegularExpressions.Regex.Match(chunkStatus, @"(\d+) chunk\(s\)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    private static bool AnswerDialog(Window w, bool yes)
    {
        Dispatcher.UIThread.RunJobs();
        var dlg = w.OwnedWindows.FirstOrDefault();
        if (dlg is null) return false;
        var btn = Descendants(dlg).OfType<Button>().FirstOrDefault(b => (b.Content as string) == (yes ? "Yes" : "No"));
        if (btn is null) { dlg.Close(); Pump(0.5); return false; }
        btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(1.0);
        return true;
    }

    private static void ClickButtonIn(Visual root, string content)
    {
        var btn = Descendants(root).OfType<Button>().FirstOrDefault(b => (b.Content as string) == content);
        if (btn is null) { Console.WriteLine("    (button not found: " + content + ")"); return; }
        btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Ticks the checkboxes on the visible rows of the block list ("Find blocks"). There are no ready-made categories
    /// in the UI, so both the probe and the user select codes the same way — by ticking in the list,
    /// and the search box only narrows the list. Returns the number of newly ticked rows.</summary>
    private static int TickFilterRows(Window w, Func<string, bool> accept = null)
    {
        var panel = w.FindNamed<StackPanel>("FilterListPanel");
        if (panel is null) { Console.WriteLine("    (no FilterListPanel)"); return 0; }
        int n = 0;
        foreach (var cb in Descendants(panel).OfType<CheckBox>())
        {
            if (cb.Tag is not string code) continue;
            if (accept is not null && !accept(code)) continue;
            if (cb.IsChecked == true) continue;
            cb.IsChecked = true;
            n++;
        }
        Pump(1.0);
        return n;
    }

    /// <summary>Buttons inside a collapsed Expander do not make it into the visual tree, so a probe
    /// that clicks them by name must expand the panel: its state is remembered in settings.json
    /// and depends on which probes have already toggled it in this profile.</summary>
    private static void EnsureDataPanelOpen(Window w)
    {
        var dp = w.FindNamed<Expander>("DataPanel");
        if (dp is null) return;
        if (!dp.IsExpanded) dp.IsExpanded = true;
        Pump(1.5);
        Console.WriteLine("  [setup] DataPanel expanded=" + dp.IsExpanded);
    }


    /// <summary>Selects a bottom drawer tab by its header.</summary>
    private static void SelectDataTab(Window w, string header)
    {
        var tabs = w.FindNamed<TabControl>("DataTabs");
        if (tabs is null) { Console.WriteLine("  (no DataTabs)"); return; }
        for (var i = 0; i < tabs.ItemCount; i++)
            if ((tabs.Items[i] as TabItem)?.Header as string == header)
            {
                tabs.SelectedIndex = i;
                break;
            }

        Pump(1.5);
    }

    /// <summary>Counters from a chunk list row: "15991, 15983  (entities 12, be 45)".</summary>
    private static (int Cx, int Cz, int Ent, int Be)? PickRow(string? item)
    {
        var m = System.Text.RegularExpressions.Regex.Match(item ?? "", @"^(\d+), (\d+)\s+\(entities (\d+), be (\d+)\)");
        return m.Success
            ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value))
            : null;
    }

    /// <summary>
    ///     The "Chunks" tab: searching chunks with different query terms and jumping to the selected chunk.
    ///     A ComboBox with ALL world coordinates used to be here — there was no way to search in it.
    /// </summary>
    private static void ChunkSearch(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(save);
        if (!WaitLoaded(w, SaveToken(save), "Scan complete", 900)) { Console.WriteLine("FAILED: load"); return; }
        EnsureDataPanelOpen(w);
        SelectDataTab(w, "Chunks");

        var pick = w.FindNamed<ComboBox>("ChunkPick")!;
        var summary = w.FindNamed<TextBlock>("ChunkSummaryLabel")!;
        var tab = w.FindNamed<ChunkDataTab>("ChunkDataView")!;
        var searchBox = w.FindNamed<TextBox>("ChunkSearchBox")!;
        var xBox = w.FindNamed<TextBox>("ChunkXBox")!;
        var zBox = w.FindNamed<TextBox>("ChunkZBox")!;

        List<(int Cx, int Cz, int Ent, int Be)> Rows() =>
            pick.Items.Cast<string?>().Select(PickRow).Where(r => r is not null).Select(r => r!.Value).ToList();

        // 1) empty query: only chunks with records, the most "populated" first
        var def = Rows();
        var emptyInList = def.Count(r => r.Ent + r.Be == 0);
        var pairs = def.Select(r => (r.Ent, r.Be)).ToList();
        var desc = pairs.SequenceEqual(pairs.OrderByDescending(x => x.Ent).ThenByDescending(x => x.Be));
        Console.WriteLine($"[default] items={pick.ItemCount} parsed={def.Count} summary='{Cut(summary.Text)}'");
        Console.WriteLine($"  first={(pick.Items.Count > 0 ? Cut(pick.Items[0] as string) : "(none)")}"
                          + $"  chunks without records={emptyInList} sortedByEntitiesDesc={desc}");
        Shot(w, "chunksearch_default");

        void Search(string query)
        {
            SetText(w, "ChunkSearchBox", query);
            ClickButtonIn(tab, "Search");
            Pump(2.5);
        }

        // 2) coordinates of the found chunk — the query must return exactly that one
        var probe = def.FirstOrDefault(r => r.Ent > 0);
        Search($"{probe.Cx} {probe.Cz}");
        var exact = Rows();
        var exactOk = exact.Count == 1 && exact[0].Cx == probe.Cx && exact[0].Cz == probe.Cz;
        Console.WriteLine($"[coords '{probe.Cx} {probe.Cz}'] items={pick.ItemCount} exact={exactOk}"
                          + $" summary='{Cut(summary.Text)}'");

        // 3) row selection: the coordinates move into the fields, the chunk panel is rebuilt
        var panel = w.FindNamed<StackPanel>("ChunkDataPanel")!;
        var panelBefore = panel.Children.Count;
        pick.SelectedIndex = 0;
        Pump(2.5);
        var pickedOk = xBox.Text == probe.Cx.ToString() && zBox.Text == probe.Cz.ToString();
        Console.WriteLine($"[pick] X={xBox.Text} Z={zBox.Text} ok={pickedOk} panel {panelBefore}→{panel.Children.Count}"
                          + $" status='{Cut(TextOf(w, "ChunkStatusText"))}'");

        // 4) content: chunks with a wolf / ore / spawner
        Search("wolf");
        Console.WriteLine($"[content 'wolf'] items={pick.ItemCount} summary='{Cut(summary.Text)}'");
        Search("ore-quartz");
        Console.WriteLine($"[content 'ore-quartz'] items={pick.ItemCount} summary='{Cut(summary.Text)}'");
        Search("spawner");
        Console.WriteLine($"[content 'spawner'] items={pick.ItemCount} summary='{Cut(summary.Text)}'");

        // 5) range and counter
        var lo = Math.Max(0, probe.Cx - 2);
        Search($"cx{lo}..{probe.Cx + 2}");
        var rangeRows = Rows();
        var rangeOk = rangeRows.Count > 0 && rangeRows.All(r => r.Cx >= lo && r.Cx <= probe.Cx + 2);
        Console.WriteLine($"[range 'cx{lo}..{probe.Cx + 2}'] items={pick.ItemCount} all in range={rangeOk}"
                          + $" summary='{Cut(summary.Text)}'");
        Search("ent>0");
        var entRows = Rows();
        var entOk = entRows.Count > 0 && entRows.All(r => r.Ent > 0);
        Console.WriteLine($"[count 'ent>0'] items={pick.ItemCount} all entities>0={entOk}");
        // layer Y: for a chunk this is the y-column NUMBER (world Y / 32), not world Y. Check on one
        // and the same column: it has some layers and lacks others, so the term really does filter.
        // Take the range with a margin: on a large save a column has more layers than 0..9.
        var layerCounts = new List<(int Y, int N)>();
        for (var y = 0; y <= 15; y++)
        {
            Search($"cx{probe.Cx} cz{probe.Cz} y{y}");
            layerCounts.Add((y, pick.ItemCount));
        }

        var layerOk = layerCounts.Any(l => l.N == 1) && layerCounts.Any(l => l.N == 0);
        Console.WriteLine($"[layer on column {probe.Cx},{probe.Cz}] "
                          + string.Join(", ", layerCounts.Where(l => l.N > 0).Select(l => $"y{l.Y}:{l.N}"))
                          + $" (empty: {string.Join(",", layerCounts.Where(l => l.N == 0).Select(l => "y" + l.Y))})"
                          + $" distinguishes layers={layerOk}");
        Search("zzz-nothing-here");
        Console.WriteLine($"[no match] items={pick.ItemCount} summary='{Cut(summary.Text)}'");

        // 6) broken counter: the parse error is visible, the list does not fall over
        Search("ent>99999999999999999999");
        var badShown = (summary.Text ?? "").Contains("bad number");
        Console.WriteLine($"[bad query] items={pick.ItemCount} error shown={badShown} summary='{Cut(summary.Text)}'");
        Shot(w, "chunksearch_bad");

        // 7) Clear restores the original list
        ClickButtonIn(tab, "Clear");
        Pump(2.0);
        Console.WriteLine($"[clear] filter='{searchBox.Text}' items={pick.ItemCount} summary='{Cut(summary.Text)}'");
        Shot(w, "chunksearch_clear");

        var ok = pick.ItemCount > 0 && emptyInList == 0 && desc && exactOk && pickedOk && rangeOk && entOk
                 && layerOk && badShown;
        Console.WriteLine(ok
            ? "RESULT: OK — chunk search works (coordinates/range/counter/layer/content/error), selection opens the chunk"
            : $"RESULT: FAIL — items={pick.ItemCount} empty={emptyInList} desc={desc} exact={exactOk}"
              + $" picked={pickedOk} range={rangeOk} ent={entOk} layer={layerOk} bad={badShown}");
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }


    /// <summary>
    ///     The "Items" tab: search across player, creature and block-entity inventories, the stack card
    ///     and the jumps "to the owner" / "to the chunk" / "items of this chunk".
    /// </summary>
    private static void ItemFind(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(save);
        if (!WaitLoaded(w, SaveToken(save), "Scan complete", 900)) { Console.WriteLine("FAILED: load"); return; }
        EnsureDataPanelOpen(w);
        SelectDataTab(w, "Items");

        var list = w.FindNamed<ListBox>("ItemList")!;
        var summary = w.FindNamed<TextBlock>("ItemSummaryLabel")!;
        var filter = w.FindNamed<TextBox>("ItemFilterBox")!;
        var source = w.FindNamed<ComboBox>("ItemSourcePick")!;
        var detail = w.FindNamed<StackPanel>("ItemDetailPanel")!;
        var tab = w.FindNamed<ItemsTab>("ItemsView")!;

        var toolbar = filter.Parent as StackPanel;
        var toolbarRight = toolbar?.Children.OfType<Control>().Select(c => c.Bounds.Right).DefaultIfEmpty(0).Max() ?? 0;
        Console.WriteLine($"[items toolbar] width={toolbar?.Bounds.Width:0} maxRight={toolbarRight:0} rightEdge={toolbar?.Bounds.Width:0}");

        var rows = list.Items.Cast<ItemStackRow>().ToList();
        Console.WriteLine($"[items] rows={rows.Count} summary='{Cut(summary.Text)}'"
                          + $" player={rows.Count(r => r.IsPlayer)} entity={rows.Count(r => r.IsEntity)} block={rows.Count(r => r.IsBlockEntity)}");
        Console.WriteLine("  first: " + string.Join(" | ", rows.Take(3).Select(r => Cut(r.Display))));
        if (rows.Count < 3) { Console.WriteLine("FAILED: too few inventory rows"); CloseNow(w); Pump(0.5); return; }
        Shot(w, "itemfind_list");

        // 1) search by item code
        filter.Text = "ore-quartz";
        Pump(2.0);
        var quartz = list.Items.Cast<ItemStackRow>().ToList();
        var quartzOk = quartz.Count > 0 && quartz.All(r => r.Code.Contains("ore-quartz", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"[item ore-quartz] rows={quartz.Count} allMatch={quartzOk} summary='{Cut(summary.Text)}'");
        list.SelectedIndex = 0;
        Pump(1.5);
        var detailText = string.Join(" | ", Descendants(detail).OfType<TextBlock>().Select(t => Cut(t.Text)).Take(8));
        Console.WriteLine("  detail: " + detailText);
        Shot(w, "itemfind_detail");

        // 2) jump to the chunk of the selected stack
        var selected = list.SelectedItem as ItemStackRow;
        ClickButtonIn(detail, "Show chunk on map");
        Pump(2.0);
        var xBox = w.FindNamed<TextBox>("ChunkXBox")!;
        var zBox = w.FindNamed<TextBox>("ChunkZBox")!;
        var jumpOk = selected is not null && xBox.Text == selected.Inventory.ChunkX.ToString()
                                          && zBox.Text == selected.Inventory.ChunkZ.ToString();
        Console.WriteLine($"[item jump] target={(selected is null ? "-" : $"{selected.Inventory.ChunkX},{selected.Inventory.ChunkZ}")}"
                          + $" → X={xBox.Text} Z={zBox.Text} ok={jumpOk}");

        // 3) search by owning player + the Players source
        SelectDataTab(w, "Items");
        filter.Text = "owner:zorolin";
        source.SelectedIndex = 1; // Players
        Pump(2.0);
        var playerRows = list.Items.Cast<ItemStackRow>().ToList();
        var playerOk = playerRows.Count > 0 && playerRows.All(r => r.IsPlayer);
        Console.WriteLine($"[owner:zorolin] rows={playerRows.Count} allPlayers={playerOk} summary='{Cut(summary.Text)}'");
        if (playerRows.Count > 0)
        {
            list.SelectedIndex = 0;
            Pump(1.0);
            ClickButtonIn(detail, "Open owner");
            Pump(2.0);
            var tabs = w.FindNamed<TabControl>("DataTabs")!;
            var playerFilter = w.FindNamed<TextBox>("PlayerFilterBox")!;
            Console.WriteLine($"[open owner] tab={(tabs.SelectedItem as TabItem)?.Header} playerFilter='{Cut(playerFilter.Text)}'");
            Shot(w, "itemfind_owner_player");

            // 4) from the player card — "Find items" back to Items with the inventory key
            var playerDetail = w.FindNamed<StackPanel>("PlayerDetailPanel")!;
            var find = Descendants(playerDetail).OfType<Button>().FirstOrDefault(b => (b.Content as string) == "Find items");
            if (find is not null)
            {
                find.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(2.0);
                Console.WriteLine($"[player → items] tab={(tabs.SelectedItem as TabItem)?.Header} filter='{Cut(filter.Text)}'");
            }
        }

        // 5) from an Entities record — "Find items" with that record's key:
        SelectDataTab(w, "Entities");
        var entityList = w.FindNamed<ListBox>("EntityList")!;
        var entityDetail = w.FindNamed<StackPanel>("EntityDetailPanel")!;
        var withInv = entityList.Items.Cast<EntityRow>().FirstOrDefault(r => r.ItemCount > 0);
        Console.WriteLine($"[entities with inventory] {(withInv is null ? "-" : Cut(withInv.Display))}");
        if (withInv is not null)
        {
            entityList.SelectedItem = withInv;
            Pump(2.0);
            var find = Descendants(entityDetail).OfType<Button>().FirstOrDefault(b => (b.Content as string) == "Find items");
            if (find is not null)
            {
                find.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(2.0);
                var tabs = w.FindNamed<TabControl>("DataTabs")!;
                Console.WriteLine($"[entity → items] tab={(tabs.SelectedItem as TabItem)?.Header} filter='{Cut(filter.Text)}' source={source.SelectedIndex} rows={list.ItemCount}");
            }
        }

        // 5b) container owner: Open owner leads to Entities and shows the same record
        SelectDataTab(w, "Items");
        filter.Text = "owner:chest";
        source.SelectedIndex = 3; // Containers
        Pump(2.0);
        var blockRows = list.Items.Cast<ItemStackRow>().ToList();
        Console.WriteLine($"[owner:chest] rows={blockRows.Count} allContainers={blockRows.All(r => r.IsBlockEntity)}");
        if (blockRows.Count > 0)
        {
            list.SelectedIndex = 0;
            Pump(1.0);
            ClickButtonIn(detail, "Open owner");
            Pump(2.5);
            var tabs = w.FindNamed<TabControl>("DataTabs")!;
            var blockDetail = w.FindNamed<StackPanel>("EntityDetailPanel")!;
            var hasFind = Descendants(blockDetail).OfType<Button>().Any(b => (b.Content as string) == "Find items");
            Console.WriteLine($"[block → entities] tab={(tabs.SelectedItem as TabItem)?.Header} findItems={hasFind}");
        }

        // 5c) from the chunk card: "Find items" on a container
        SelectDataTab(w, "Chunks");
        var pick = w.FindNamed<ComboBox>("ChunkPick")!;
        var chunkPanel = w.FindNamed<StackPanel>("ChunkDataPanel")!;
        var targetIndex = -1;
        for (var i = 0; i < pick.ItemCount; i++)
        {
            var parsed = PickRow(pick.Items[i] as string);
            if (parsed is { Be: > 0 }) { targetIndex = i; break; }
        }
        Console.WriteLine($"[chunk card] rows={pick.ItemCount} target={targetIndex}");
        if (targetIndex >= 0)
        {
            pick.SelectedIndex = targetIndex;
            Pump(2.5);
            var find = Descendants(chunkPanel).OfType<Button>().FirstOrDefault(b => (b.Content as string) == "Find items");
            if (find is not null)
            {
                find.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(2.0);
                var tabs = w.FindNamed<TabControl>("DataTabs")!;
                Console.WriteLine($"[chunk → items] tab={(tabs.SelectedItem as TabItem)?.Header} filter='{Cut(filter.Text)}' rows={list.ItemCount}");
            }
            else Console.WriteLine("[chunk card] Find items not found");
        }

        // 6) "This chunk": items of the current chunk
        SelectDataTab(w, "Items");
        ClickButtonIn(tab, "Clear");
        Pump(1.0);
        ClickButtonIn(tab, "This chunk");
        Pump(2.0);
        var chunkRows = list.Items.Cast<ItemStackRow>().ToList();
        var chunkOk = filter.Text.StartsWith("chunk:", StringComparison.Ordinal)
                      && chunkRows.All(r => r.Inventory.HasChunk
                                            && chunkRows.All(x => x.Inventory.ChunkX == chunkRows[0].Inventory.ChunkX
                                                                  && x.Inventory.ChunkZ == chunkRows[0].Inventory.ChunkZ));
        Console.WriteLine($"[this chunk] filter='{Cut(filter.Text)}' rows={chunkRows.Count} sameChunk={chunkOk}"
                          + $" summary='{Cut(summary.Text)}'");

        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>
    ///     The "Entities" tab: a flat list of creatures and block entities instead of a text dump —
    ///     filter, category, "frequent names", record card and editing of its parameters up to a COPY of the save
    ///     (both for creatures and for block entities: field 6 and field 8).
    /// </summary>
    private static void EntityFind(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(save);
        if (!WaitLoaded(w, SaveToken(save), "Scan complete", 900)) { Console.WriteLine("FAILED: load"); return; }
        EnsureDataPanelOpen(w);
        SelectDataTab(w, "Entities");

        var list = w.FindNamed<ListBox>("EntityList")!;
        var summary = w.FindNamed<TextBlock>("EntitySummaryLabel")!;
        var chips = w.FindNamed<WrapPanel>("EntityChipsPanel")!;
        var filter = w.FindNamed<TextBox>("EntityFilterBox")!;
        var cat = w.FindNamed<ComboBox>("EntityCategoryPick")!;
        var detail = w.FindNamed<StackPanel>("EntityDetailPanel")!;
        var tab = w.FindNamed<EntitiesTab>("EntitiesView")!;
        var edits = EditsOf(w);

        var rows = list.Items.Cast<EntityRow>().ToList();
        Console.WriteLine($"[entities] rows={rows.Count} (creatures={rows.Count(r => !r.IsBlockEntity)},"
                          + $" block entities={rows.Count(r => r.IsBlockEntity)}) chips={chips.Children.Count}"
                          + $" summary='{Cut(summary.Text)}'");
        Console.WriteLine("  first: " + string.Join(" | ", rows.Take(3).Select(r => Cut(r.Display))));
        Shot(w, "entityfind_list");

        // 1) "frequent names": a click inserts the name into the filter
        var chip = chips.Children.OfType<Button>().FirstOrDefault();
        var chipName = System.Text.RegularExpressions.Regex.Replace(chip?.Content as string ?? "", @"\s×\d+$", "");
        var chipFull = ToolTip.GetTip(chip) as string ?? chipName;
        chip?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(1.5);
        var chipRows = list.Items.Cast<EntityRow>().ToList();
        var chipOk = chipRows.Count > 0 && chipRows.All(r => r.Matches(chipFull)) && filter.Text == chipFull;
        Console.WriteLine($"[chip '{Cut(chipFull)}'] filter='{Cut(filter.Text)}' rows={chipRows.Count}"
                          + $" allMatch={chipOk} summary='{Cut(summary.Text)}'");

        // 2) category narrows the list exactly by record type
        cat.SelectedIndex = 1; // Entities
        Pump(1.5);
        var onlyCreatures = list.Items.Cast<EntityRow>().ToList();
        cat.SelectedIndex = 2; // Block entities
        Pump(1.5);
        var onlyBlocks = list.Items.Cast<EntityRow>().ToList();
        Console.WriteLine($"[category] entities={onlyCreatures.Count} (all creatures={onlyCreatures.All(r => !r.IsBlockEntity)})"
                          + $", block entities={onlyBlocks.Count} (all BE={onlyBlocks.All(r => r.IsBlockEntity)})");

        // 3) creature: row selection → card → editing a field AND coordinates → pending → COPY
        ClickButtonIn(tab, "Clear");
        Pump(1.0);
        cat.SelectedIndex = 1; // creatures only
        Pump(1.5);
        var sessionEntity = false;
        var valueOk = false;
        var posOk = false;
        var inCopy = false;
        var entityRow = list.Items.Cast<EntityRow>().FirstOrDefault();
        Console.WriteLine($"[entities only] rows={list.ItemCount} first={(entityRow is null ? "-" : Cut(entityRow.Display))}");
        if (entityRow is not null)
        {
            list.SelectedIndex = 0;
            Pump(2.0);
            var card = Descendants(detail).OfType<EntityCard>().FirstOrDefault();
            var model = card?.FindNamed<Border>("Root")?.DataContext;
            Console.WriteLine($"  detail: card={card is not null} model={model?.GetType().Name}"
                              + $" title='{Prop(model, "Title")}' coords='{Prop(model, "Coordinates")}'"
                              + $" pos='{Prop(model, "PositionText")}' canEdit={Prop(model, "CanEdit")}"
                              + $" hint='{Prop(model, "EditHint")}'");
            Shot(w, "entityfind_detail");

            var editBtn = Descendants(detail).OfType<Button>().FirstOrDefault(b => (b.Content as string) == "Edit fields");
            if (editBtn is not null)
            {
                editBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(2.0);
                var dlg = w.OwnedWindows.FirstOrDefault();
                if (dlg is not null)
                {
                    var pairs = RowPairs(dlg);
                    Console.WriteLine($"  dialog='{dlg.Title}' rows={pairs.Count}: "
                                      + string.Join(", ", pairs.Take(8).Select(p => p.Label)));
                    var num = pairs.FirstOrDefault(p => p.Label != "pos.x"
                        && double.TryParse(p.Box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
                    var posX = pairs.FirstOrDefault(p => p.Label == "pos.x");
                    var changed = num.Box is null ? null : num.Label;
                    var oldX = posX.Box is null ? 0 : double.Parse(posX.Box.Text!, CultureInfo.InvariantCulture);
                    if (num.Box is not null) num.Box.Text = "4242";
                    if (posX.Box is not null) posX.Box.Text = (oldX + 2.0).ToString(CultureInfo.InvariantCulture);
                    ClickButtonIn(dlg, "Apply");
                    Pump(2.5);
                    Console.WriteLine($"  after Apply: dialog closed={w.OwnedWindows.Count == 0}"
                                      + $" modelError='{Prop(model, "EditError")}' pending={Prop(model, "IsPending")}");

                    if (changed is not null && edits.TryGetPayloadEdits(entityRow.Position, out var map) && map is not null
                        && map.TryGetValue((6, entityRow.Occurrence), out var payload))
                    {
                        sessionEntity = true;
                        var rec = ChunkDataParser.ParseEntity(payload);
                        valueOk = rec.Fields.FirstOrDefault(f => f.DisplayPath == changed)?.ValueText == "4242";
                        var (ccx, ccy, ccz) = ChunkPosHelper.DecodeIndex((ulong)entityRow.Position);
                        var copies = EntityPositionCodec.Locate(payload, ccx * 32, ccy * 32, ccz * 32);
                        posOk = copies.Count > 0 && copies.All(c => EntityPositionCodec.Same(c.X, oldX + 2.0));

                        var sav = ((IEntityEditHost)w).Save!;
                        var chunk = EditSession.Materialize(sav, edits.Snapshot()).FirstOrDefault(c => c.table == "chunk");
                        if (chunk.data.Length > 0)
                        {
                            var ent = ChunkDataParser.Parse(chunk.data, sav.BlockRegistry, entityRow.Position)
                                .Entities[entityRow.Occurrence];
                            inCopy = ent.Fields.FirstOrDefault(f => f.DisplayPath == changed)?.ValueText == "4242";
                        }

                        Console.WriteLine($"  session entity[{entityRow.Occurrence}] {changed}=4242 -> {valueOk};"
                                          + $" pos.x {oldX} -> {oldX + 2.0} in {copies.Count} copy(ies) -> {posOk};"
                                          + $" in copy={inCopy}");
                    }
                }
            }
        }

        // 4) block entity: the same edit path, but field 8 (their position is in the regular posx/posy/posz fields)
        cat.SelectedIndex = 2;
        Pump(2.0);
        var sessionBe = false;
        var beValueOk = false;
        var beRow = list.Items.Cast<EntityRow>().FirstOrDefault();
        Console.WriteLine($"[block entities only] rows={list.ItemCount} first={(beRow is null ? "-" : Cut(beRow.Display))}");
        if (beRow is not null)
        {
            list.SelectedIndex = 0;
            Pump(2.0);
            var editBtn = Descendants(detail).OfType<Button>().FirstOrDefault(b => (b.Content as string) == "Edit fields");
            if (editBtn is not null)
            {
                editBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(2.0);
                var dlg = w.OwnedWindows.FirstOrDefault();
                if (dlg is not null)
                {
                    var pairs = RowPairs(dlg);
                    Console.WriteLine($"  BE dialog='{dlg.Title}' rows={pairs.Count}: "
                                      + string.Join(", ", pairs.Take(8).Select(p => p.Label)));
                    var num = pairs.FirstOrDefault(p =>
                        double.TryParse(p.Box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
                    var changed = num.Box is not null ? num.Label : pairs.Count > 0 ? pairs[0].Label : null;
                    var newValue = num.Box is not null ? "77" : "probe8";
                    if (changed is not null)
                    {
                        pairs.First(p => p.Label == changed).Box.Text = newValue;
                        ClickButtonIn(dlg, "Apply");
                        Pump(2.5);
                        if (edits.TryGetPayloadEdits(beRow.Position, out var map) && map is not null
                            && map.TryGetValue((8, beRow.Occurrence), out var payload))
                        {
                            sessionBe = true;
                            var rec = ChunkDataParser.ParseBlockEntity(payload);
                            beValueOk = rec.Fields.FirstOrDefault(f => f.DisplayPath == changed)?.ValueText == newValue;
                            Console.WriteLine($"  session block entity[{beRow.Occurrence}] field8 {changed}={newValue}"
                                              + $" -> {beValueOk}");
                        }
                    }
                }
            }
        }

        // 5) jump to the record's chunk from the detail card
        cat.SelectedIndex = 1; // Entities
        ClickButtonIn(tab, "Clear");
        Pump(1.5);
        list.SelectedIndex = 0;
        Pump(1.5);
        var target = list.SelectedItem as EntityRow;
        ClickButtonIn(detail, "Show chunk on map");
        Pump(2.5);
        var xBox = w.FindNamed<TextBox>("ChunkXBox")!;
        var zBox = w.FindNamed<TextBox>("ChunkZBox")!;
        var jumpOk = target is not null && xBox.Text == target.ChunkX.ToString() && zBox.Text == target.ChunkZ.ToString();
        Console.WriteLine($"[jump] target={(target is null ? "-" : $"{target.ChunkX},{target.ChunkZ}")}"
                          + $" → X={xBox.Text} Z={zBox.Text} ok={jumpOk}"
                          + $" status='{Cut(TextOf(w, "ChunkStatusText"))}'");
        Shot(w, "entityfind_jump");

        // 6) record name: search by the name the player gave. The regression this is here for:
        //    a horse with the nametag "Moonlight" had a truncated inventory key `slot-` in its row name.
        ClickButtonIn(tab, "Clear");
        Pump(1.0);
        filter.Text = "moonlight";
        Pump(2.5);
        var named = list.Items.Cast<EntityRow>().ToList();
        var nameOk = true;
        if (named.Count == 0)
        {
            Console.WriteLine("[named] no record matching 'moonlight' in this save");
        }
        else
        {
            var row = named[0];
            list.SelectedIndex = 0;
            Pump(2.0);
            var card = Descendants(detail).OfType<EntityCard>().FirstOrDefault();
            var model = card?.FindNamed<Border>("Root")?.DataContext;
            nameOk = row.CustomName.Length > 0 && row.Label == row.CustomName
                     && !row.Name.EndsWith("-") && !row.Name.EndsWith(":")
                     && Prop(model, "Title") == row.Label && Prop(model, "CodeText") == row.Code;
            Console.WriteLine($"[named] matches={named.Count} name='{row.Name}' code='{row.Code}'"
                              + $" custom='{row.CustomName}' kind='{row.Kind}' domain='{row.Domain}'"
                              + $" cardTitle='{Prop(model, "Title")}' codeText='{Prop(model, "CodeText")}'"
                              + $" ok={nameOk}");
            Console.WriteLine($"      {Cut(row.Display)}");
            Shot(w, "entityfind_named");
        }

        var ok = rows.Count > 0 && chipOk && onlyCreatures.All(r => !r.IsBlockEntity)
                 && onlyBlocks.All(r => r.IsBlockEntity) && sessionEntity && valueOk && posOk && inCopy
                 && sessionBe && beValueOk && jumpOk && nameOk;
        Console.WriteLine(ok
            ? "RESULT: OK — record search/filter, creature edit (field+position) and block entity (field 8) reached the copy"
            : $"RESULT: FAIL — rows={rows.Count} chip={chipOk} entity={sessionEntity}/{valueOk}/{posOk}/{inCopy}"
              + $" blockEntity={sessionBe}/{beValueOk} jump={jumpOk}");
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    private static List<(string Label, TextBox Box)> RowPairs(Visual root)
        => Descendants(root).OfType<StackPanel>()
            .Where(sp => sp.Children.Count == 2 && sp.Children[0] is TextBlock && sp.Children[1] is TextBox)
            .Select(sp => (((TextBlock)sp.Children[0]).Text ?? "", (TextBox)sp.Children[1]))
            .ToList();

    private static object? Prop(object? model, string name) => model?.GetType().GetProperty(name)?.GetValue(model);

    private static string Describe(ListBox list, string tag)
    {
        var sv = Descendants(list).OfType<ScrollViewer>().FirstOrDefault();
        var cards = Descendants(list).OfType<EntityCard>().ToList();
        return $"  [{tag}] scrollY={sv?.Offset.Y:0.##}/{sv?.Extent.Height:0.##} viewport={sv?.Viewport.Height:0.##}"
               + $" realizedCards={cards.Count} heights=[{string.Join(",", cards.Select(c => c.Bounds.Height.ToString("0")))}]";
    }

    /// <summary>Scenario B5/P4p: open a second save EXACTLY while the first one is scanning.</summary>
    private static void Race(string bigSave, string smallSave)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        Console.WriteLine("[race] opening the large save and waiting for the SCAN to start (not for loading to finish)");
        ResetStatus(w);
        w.OpenStartupFile(bigSave);
        var end = DateTime.UtcNow.AddSeconds(600);
        bool inScan = false;
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            try { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } catch { }
            var st = TextOf(w, "StatusText");
            var sc = TextOf(w, "ScanStatusText");
            if (st.StartsWith("opened:") && sc.StartsWith("Scanning")) { inScan = true; break; }
            Thread.Sleep(30);
        }
        Console.WriteLine("[race] terrain loaded, scan running=" + inScan + " scan='" + Cut(TextOf(w, "ScanStatusText")) + "'");
        if (!inScan) { Console.WriteLine("[race] FAILED: did not catch the scan moment"); return; }

        Console.WriteLine("[race] opening the small save RIGHT IN THE MIDDLE of the large save's scan");
        ResetStatus(w);
        w.OpenStartupFile(smallSave);
        var ok = WaitLoaded(w, SaveToken(smallSave), "Scan complete", 300);
        Console.WriteLine("[race] small save loaded=" + ok + " status='" + Cut(TextOf(w, "StatusText")) + "'");
        Pump(30.0);   // longer than the large save's scan takes (~10 s) — a stale scan must publish nothing
        var sc2 = TextOf(w, "ScanStatusText");
        Console.WriteLine("[race] after 30 s scan='" + Cut(sc2) + "' (expected: data of the SMALL save)");
        var data = ScanOf(w).Data;
        Console.WriteLine("[race] scan.Data.Chunks=" + (data?.Chunks.Count ?? -1) + " (small = 10848, large = 68340)");
        Console.WriteLine("[race] scan.IsRunning=" + ScanOf(w).IsRunning + " _retiredSaves=" + ((System.Collections.ICollection?)PrivField(w, "_retiredSaves"))?.Count);
        Shot(w, "race_after_reload_during_scan");
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>Scenario B4: a conflict between a hex edit and a field edit of the same position.</summary>
    private static void Conflict(string bigSave)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(bigSave);
        if (!WaitLoaded(w, SaveToken(bigSave), "Scan complete", 900)) { Console.WriteLine("FAILED: load"); return; }
        var pick = w.FindNamed<ComboBox>("ChunkPick")!;
        var panel = w.FindNamed<StackPanel>("ChunkDataPanel")!;

        // Looking for a chunk that has block entities EXACTLY in the first y-column:
        // only then do the field edit and the hex edit (always on the first column) point to the same position.
        EnsureDataPanelOpen(w);
        int target = -1;
        int scanned = 0;
        for (int i = 0; i < pick.ItemCount && i < 60 && target < 0; i++)
        {
            var label = pick.Items[i] as string ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(label, @"be (\d+)");
            if (!m.Success || m.Groups[1].Value == "0") continue;
            scanned++;
            pick.SelectedIndex = i;
            Pump(0.6);
            var firstSection = panel.Children.OfType<StackPanel>().FirstOrDefault();
            var editBtn = firstSection is null ? null : Descendants(firstSection).OfType<Button>().FirstOrDefault(b => (b.Content as string) == "Edit fields");
            if (editBtn is not null) { target = i; Console.WriteLine("  found a chunk with BE in the first column: '" + label + "' (index " + i + ", checked " + scanned + ")"); break; }
        }
        if (target < 0) { Console.WriteLine("  found no chunk with BE in the first column among " + scanned + " checked — the scenario is not reproducible on this save"); CloseNow(w); Pump(0.5); Console.WriteLine("DONE"); return; }

        pick.SelectedIndex = target;
        Pump(2.0);

        // 1) hex edit (position = the first column)
        ClickButtonIn(w, "Apply hex");
        Pump(2.0);
        long hexPos = -1;
        foreach (var k in EditsOf(w).WholeChunkPositions) hexPos = k;
        Console.WriteLine("  hex applied: hex=" + EditsOf(w).WholeChunkEditCount + " pos=" + hexPos);
        if (hexPos < 0) { Console.WriteLine("  FAILED: hex not applied (ParseHex?)"); CloseNow(w); Pump(0.5); Console.WriteLine("DONE"); return; }

        // 2) field edit of the same position → expect a dialog.
        // Not every y-column has a card with fields, so we search all sections and prefer
        // the one whose position matches the hex target (otherwise there is no conflict by definition).
        long hexTarget = EditsOf(w).HexTargetPosition;
        long otherPos = -1;
        // section snapshot: clicking a card button repaints the panel (P1-3), so it cannot be enumerated on the fly
        foreach (var sec in panel.Children.OfType<StackPanel>().ToList())
        {
            var btn = Descendants(sec).OfType<Button>().FirstOrDefault(b => (b.Content as string) == "Edit fields");
            if (btn is null) continue;
            btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Pump(1.2);
            var p2 = EditingPos(w) ?? -1;
            if (p2 == hexTarget) { otherPos = p2; break; }
            if (otherPos < 0) otherPos = p2;
            ClickButtonIn(w, "Cancel");
            Pump(0.6);
        }
        if (otherPos < 0) { Console.WriteLine("  this chunk has no card with fields — the scenario is not reproducible"); CloseNow(w); Pump(0.5); Console.WriteLine("DONE"); return; }
        if (otherPos != hexTarget)
        {
            Console.WriteLine("  hex target pos=" + hexTarget + " ('" + EditsOf(w).HexTargetLabel + "'), but the editable card pos="
                + otherPos + " — on this chunk the conflict is unreachable naturally (it is checked deterministically in conflict2)");
            CloseNow(w); Pump(0.5); Console.WriteLine("DONE"); return;
        }
        ClickButtonIn(w, "Apply");
        Pump(2.0);
        var hasDialog = w.OwnedWindows.Count > 0;
        Console.WriteLine("  conflict dialog='" + hasDialog + "' status='" + Cut(TextOf(w, "ChunkStatusText")) + "'");
        Shot(w, "conflict_dialog");
        if (!hasDialog) { CloseNow(w); Pump(0.5); Console.WriteLine("DONE"); return; }

        AnswerDialog(w, false);
        Pump(1.5);
        Console.WriteLine("  'No' -> hex=" + EditsOf(w).WholeChunkEditCount + " field=" + EditsOf(w).PayloadEditCount + " status='" + Cut(TextOf(w, "ChunkStatusText")) + "'");

        ClickButtonIn(w, "Apply");
        Pump(1.5);
        var has2 = w.OwnedWindows.Count > 0;
        AnswerDialog(w, true);
        Pump(2.0);
        Console.WriteLine("  'Yes' (dialog again='" + has2 + "') -> hex=" + EditsOf(w).WholeChunkEditCount + " field=" + EditsOf(w).PayloadEditCount + " status='" + Cut(TextOf(w, "ChunkStatusText")) + "'");
        Shot(w, "conflict_yes");
        Console.WriteLine("  expectation: after 'No' hex stays, field is not added; after 'Yes' hex is reset, field is added");
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>Overlay block filter: list from the save, ore selection, build, comparison with the scan histogram.</summary>
    private static void Overlay(string bigSave)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(bigSave);
        if (!WaitLoaded(w, SaveToken(bigSave), "Scan complete", 900)) { Console.WriteLine("FAILED: load"); return; }

        var tabs = w.FindNamed<TabControl>("DataTabs")!;
        int tabIndex = -1;
        EnsureDataPanelOpen(w);

        for (int i = 0; i < tabs.ItemCount; i++)
            if ((tabs.Items[i] as TabItem)?.Header as string == "Find blocks") tabIndex = i;
        Console.WriteLine("[overlay] Find blocks tab: index=" + tabIndex + " of " + tabs.ItemCount);
        if (tabIndex < 0) { Console.WriteLine("FAILED: no such tab"); return; }
        tabs.SelectedIndex = tabIndex;
        Pump(2.0);
        Console.WriteLine("[overlay] rows in the block list: " + (w.FindNamed<StackPanel>("FilterListPanel")?.Children.Count ?? -1));

        // There are no categories ("Ores" and others) in the UI any more: the set is assembled by searching the list + checkboxes.
        // The list is limited to the top-250 by frequency, so below we compare against the codes ACTUALLY selected,
        // not against all ore-* from the histogram.
        ClickButtonIn(w, "Clear");
        Pump(0.8);
        w.FindNamed<TextBox>("FilterSearchBox")!.Text = "ore-";
        Pump(2.5);
        int oreRows = TickFilterRows(w, c => c.StartsWith("ore-", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine("[overlay] search 'ore-' + checkboxes: rows ticked=" + oreRows
            + ", total selected=" + CountOf(PrivField(w, "_filterCodes")));
        Console.WriteLine("[overlay] after selection: " + Cut(TextOf(w, "FilterStatusText")));

        var t0 = DateTime.UtcNow;
        ClickButtonIn(w, "Show on map");
        var end = DateTime.UtcNow.AddSeconds(900);
        while (DateTime.UtcNow < end && PrivField(w, "_filterMap") is null) Pump(1.0);
        var ms = (DateTime.UtcNow - t0).TotalMilliseconds;
        var map = PrivField(w, "_filterMap");
        Console.WriteLine("[overlay] map built=" + (map is not null) + " in ~" + ms.ToString("0") + " ms");
        Console.WriteLine("[overlay] status: " + Cut(TextOf(w, "FilterStatusText")));
        Shot(w, "overlay_01_ores_tab");

        var scan = ScanOf(w).Data;
        if (scan is not null && map is not null)
        {
            var selected = (HashSet<string>)PrivField(w, "_filterCodes")!;
            var hist = (System.Collections.IDictionary)scan.WorldBlockCodeCounts;
            long sum = 0; int codes = 0;
            foreach (System.Collections.DictionaryEntry kv in hist!)
            {
                if (!selected.Contains((string)kv.Key)) continue;
                sum += (long)kv.Value; codes++;
            }
            var matchCells = (long)map.GetType().GetField("MatchCells")!.GetValue(map)!;
            var hitCols = (int)map.GetType().GetField("HitColumns")!.GetValue(map)!;
            Console.WriteLine($"[overlay] CROSS-CHECK: selected codes in the histogram={codes} of {selected.Count}, cells={sum:N0}, MatchCells={matchCells:N0}, columns={hitCols:N0}");
            Console.WriteLine("[overlay] expectation: histogram cells == MatchCells (1:1)");
        }

        var dim = w.FindNamed<CheckBox>("FilterDimCheck")!;
        dim.IsChecked = false; Pump(3.0); Shot(w, "overlay_02_only_matches");
        dim.IsChecked = true; Pump(3.0); Shot(w, "overlay_03_dimmed");
        Console.WriteLine("[overlay] toggling Dim the rest — no errors");
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>New UI: splitter/settings/compact cards/hotkeys/navigation over matches.</summary>
    private static void Ui2(string bigSave)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(bigSave);
        if (!WaitLoaded(w, SaveToken(bigSave), "Scan complete", 900)) { Console.WriteLine("FAILED: load"); return; }

        string Fmt2(Control? c) => c is null ? "(none)" : $"{c.Bounds.Width:0}x{c.Bounds.Height:0}";
        Console.WriteLine("[ui2] window=" + w.Width + "x" + w.Height
            + " map=" + Fmt2(w.FindNamed<Control>("Map"))
            + " panel=" + Fmt2(w.FindNamed<Expander>("DataPanel"))
            + " splitter=" + Fmt2(w.FindNamed<GridSplitter>("PanelSplitter")));
        Shot(w, "ui2_01_layout_1280x800");
        var dp = w.FindNamed<Expander>("DataPanel")!;
        dp.IsExpanded = false; Pump(1.5);
        Console.WriteLine("[ui2] panel collapsed: map=" + Fmt2(w.FindNamed<Control>("Map")) + " panel=" + Fmt2(dp)
            + " splitter visible=" + (w.FindNamed<GridSplitter>("PanelSplitter")?.IsVisible));
        Shot(w, "ui2_06_collapsed_panel");
        dp.IsExpanded = true; Pump(1.5);

        // Settings tab: settings are no longer in menu popups
        var tabs = w.FindNamed<TabControl>("DataTabs")!;
        int settingsIdx = -1;
        for (int i = 0; i < tabs.ItemCount; i++)
            if ((tabs.Items[i] as TabItem)?.Header as string == "Settings") settingsIdx = i;
        tabs.SelectedIndex = settingsIdx;
        Pump(1.0);
        var rp = w.FindNamed<StackPanel>("SettingsRenderPanel");
        var sl = w.FindNamed<Slider>("LayerSlider");
        Console.WriteLine("[ui2] Settings: tab=" + settingsIdx + " modes=" + (rp?.Children.Count ?? -1)
            + " slider=" + Fmt2(sl) + " (min=" + sl?.Minimum + " max=" + sl?.Maximum + ")");
        Shot(w, "ui2_02_settings_tab");

        // compact cards: panel height for a chunk with block entities
        tabs.SelectedIndex = 0;
        Pump(0.5);
        var pick = w.FindNamed<ComboBox>("ChunkPick")!;
        for (int i = 0; i < pick.ItemCount && i < 20; i++)
        {
            if ((pick.Items[i] as string ?? "").Contains("be 0")) continue;
            pick.SelectedIndex = i; Pump(2.0);
            var panel = w.FindNamed<StackPanel>("ChunkDataPanel")!;
            Console.WriteLine("[ui2] chunk '" + pick.Items[i] + "' → " + Cut(TextOf(w, "ChunkStatusText"))
                + " | card panel height=" + panel.Bounds.Height.ToString("0") + " px (was ~14749)");
            Shot(w, "ui2_03_compact_cards");
            break;
        }

        // hotkeys
        var lod0 = TextOf(w, "LodStatusText");
        w.KeyPress(Key.OemPlus, RawInputModifiers.None); Pump(1.0);
        w.KeyPress(Key.OemPlus, RawInputModifiers.None); Pump(1.0);
        var lod1 = TextOf(w, "LodStatusText");
        w.KeyPress(Key.F, RawInputModifiers.None); Pump(1.5);
        var lod2 = TextOf(w, "LodStatusText");
        Console.WriteLine("[ui2] keys: before='" + Cut(lod0) + "' after +2='" + Cut(lod1) + "' after F='" + Cut(lod2) + "'");

        // overlay for a specific ore + navigation over matches
        int findIdx = -1;
        for (int i = 0; i < tabs.ItemCount; i++)
            if ((tabs.Items[i] as TabItem)?.Header as string == "Find blocks") findIdx = i;
        tabs.SelectedIndex = findIdx; Pump(1.0);
        w.FindNamed<TextBox>("FilterSearchBox")!.Text = "cassiterite";
        Pump(1.5);
        var listPanel = w.FindNamed<StackPanel>("FilterListPanel")!;
        var cb = Descendants(listPanel).OfType<CheckBox>().FirstOrDefault(c => (c.Tag as string)?.Contains("cassiterite") == true);
        Console.WriteLine("[ui2] code found in the list: " + (cb?.Tag as string ?? "(none)") + " rows=" + listPanel.Children.Count);
        if (cb is not null) { cb.IsChecked = true; Pump(1.0); }
        ClickButtonIn(w, "Show on map");
        var end = DateTime.UtcNow.AddSeconds(600);
        while (DateTime.UtcNow < end && PrivField(w, "_filterMap") is null) Pump(1.0);
        Console.WriteLine("[ui2] overlay: " + Cut(TextOf(w, "FilterStatusText")));
        Shot(w, "ui2_04_overlay_single_ore");
        for (int i = 0; i < 3; i++)
        {
            ClickButtonIn(w, "▶");
            Pump(1.5);
            Console.WriteLine("[ui2] next " + (i + 1) + ": " + Cut(TextOf(w, "FilterStatusText")));
        }
        ClickButtonIn(w, "◀"); Pump(1.5);
        Console.WriteLine("[ui2] prev: " + Cut(TextOf(w, "FilterStatusText")));
        Shot(w, "ui2_05_overlay_nav");
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>Zoom regression test: during a series of wheel steps the world point in the centre must not "jump".</summary>
    private static void Zoom(string bigSave)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(bigSave);
        if (!WaitLoaded(w, SaveToken(bigSave), "Scan complete", 900)) { Console.WriteLine("FAILED: load"); return; }
        Pump(2.0);

        var map = w.FindNamed<Control>("Map")!;
        // IMPORTANT: the wheel coordinates in the test are in the control's own coordinate space, otherwise the zoom honestly
        // shifts towards the cursor and the centre legitimately "drifts"
        var centreLocal = new Point(map.Bounds.Width / 2, map.Bounds.Height / 2);
        var centre = map.TranslatePoint(centreLocal, w) ?? centreLocal;
        (double wx, double wz) Centre()
        {
            var m = map.GetType().GetMethod("DisplayCenterWorld")!;
            var r = m.Invoke(map, null)!;
            var t = ((double, double))r;
            return (t.Item1, t.Item2);
        }

        var before = Centre();
        Console.WriteLine($"[zoom] centre before zoom: {before.wx:0.0}, {before.wz:0.0} lod='{Cut(TextOf(w, "LodStatusText"))}'");
        double maxJump = 0;
        for (int i = 0; i < 8; i++)
        {
            w.MouseWheel(centre, new Vector(0, 1), RawInputModifiers.None);
            Pump(0.05);   // shorter than a region rebuild: this was exactly the interval where the jump used to happen
            var now = Centre();
            double jump = Math.Max(Math.Abs(now.wx - before.wx), Math.Abs(now.wz - before.wz));
            maxJump = Math.Max(maxJump, jump);
            var fw = PrivField(map, "_wanted");
            var ffx = PrivField(map, "_focusX"); var ffz = PrivField(map, "_focusZ");
            var fr = PrivField(map, "_frame");
            string fs = "-";
            if (fr is not null)
            {
                var t = fr.GetType();
                fs = $"ppb={t.GetField("Ppb")!.GetValue(fr)} f.FocusX={t.GetField("FocusX")!.GetValue(fr)} f.Left={t.GetField("Left")!.GetValue(fr)} f.Zoom={t.GetField("Zoom")!.GetValue(fr):0.###}";
            }
            Console.WriteLine($"[zoom] step {i + 1}: centre={now.wx:0.0},{now.wz:0.0} jump={jump:0.0} | _wanted={fw:0.###} _focus={ffx:0.0},{ffz:0.0} | frame: {fs}");
        }
        Pump(12.0);   // let the background rebuilds finish
        var settled = Centre();
        double finalJump = Math.Max(Math.Abs(settled.wx - before.wx), Math.Abs(settled.wz - before.wz));
        Console.WriteLine($"[zoom] after stabilisation: centre={settled.wx:0.0},{settled.wz:0.0} lod='{Cut(TextOf(w, "LodStatusText"))}' jump={finalJump:0.0}");
        Console.WriteLine($"[zoom] MAXIMUM CENTRE JUMP OVER THE SERIES = {maxJump:0.0} blocks (expected ~0), final = {finalJump:0.0}");
        Shot(w, "zoom_after_wheel_burst");
        // zooming back out — no jumps either
        for (int i = 0; i < 6; i++) { w.MouseWheel(centre, new Vector(0, -1), RawInputModifiers.None); Pump(0.05); }
        Pump(8.0);
        var back = Centre();
        Console.WriteLine($"[zoom] after zooming back out: centre={back.wx:0.0},{back.wz:0.0} lod='{Cut(TextOf(w, "LodStatusText"))}'");
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>Zoom when the world/chunks do NOT fill the screen: we count bitmap rebuilds
    /// (the LodStatusChanged event) and the centre offset. The target case is a small world in a large window.</summary>
    /// <summary>P1-21: windowed filter scan for each area separately — the overlay must not
    /// "see" foreign chunks (and must not read them). Prints matches by code in each area.</summary>
    private static void AreaFilterProbe(string save, string code)
    {
        using var f = VSSaveFile.OpenReadOnly(save);
        var ids = f.BlockRegistry.IdsOf(code);
        var areas = ChunkAreas.Build(f);
        Console.WriteLine($"[areafilter] code '{code}' → id [{string.Join(", ", ids)}]; areas {areas.Areas.Count}");
        foreach (var a in areas.Areas)
        {
            var sw = Stopwatch.StartNew();
            var tm = TerrainMap.BuildInArea(f, a);
            var tMap = sw.ElapsedMilliseconds;
            sw.Restart();
            var fm = BlockFilterScanner.Build(f, tm, ids);
            Console.WriteLine($"[areafilter] #{a.Index + 1}{(a.IsMain ? " (main)" : "")}: map columns={tm.PresentChunks.Count}, " +
                              $"matches={fm.MatchCells}, columns with matches={fm.HitColumns}, " +
                              $"map {tMap} ms, filter {sw.ElapsedMilliseconds} ms");
        }

        // for control — the same without a window (the old behaviour): the common rectangle of the two areas
        try
        {
            var all = TerrainMap.BuildInArea(f, null);
            Console.WriteLine($"[areafilter] without a window: map {all.Width}×{all.Height} built (unexpected)");
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine("[areafilter] without a window: " + ex.Message);
        }
    }

    /// <summary>
    ///     P1-21: a save with chunks at two ends of the world. The map is built for ONE area (the starting one —
    ///     where the world centre is), the far group is not drawn until the user scrolls to it;
    ///     on scroll the window builds its map and publishes it without resetting the zoom.
    /// </summary>
    private static void Areas(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        var sw = Stopwatch.StartNew();
        w.OpenStartupFile(save);
        // Wait exactly for the MAP, not for a full data scan: areas and the window build do not depend on the scan,
        // and a 420-MB save scan takes minutes (its progress is printed separately without blocking the check).
        var map = (TerrainView)w.FindNamed<Control>("Map")!;
        var deadline = DateTime.UtcNow.AddSeconds(600);
        while (DateTime.UtcNow < deadline && map.Terrain is null) Pump(0.3);
        if (map.Terrain is null) { Console.WriteLine("FAILED: the map was not built"); CloseNow(w); return; }
        var loadMs = sw.ElapsedMilliseconds;

        var tm = map.Terrain!;
        var areas = map.Areas!;
        Console.WriteLine($"[areas] map built in {loadMs} ms (data scan runs in parallel: '{Cut(TextOf(w, "ScanStatusText"))}')");
        Console.WriteLine($"[areas] areas={areas.Areas.Count}, columns={areas.TotalColumns}, rows={areas.TotalRows}, " +
                          $"world={(areas.WorldWidth is int ww ? ww + "×" + areas.WorldLength : "?")}");
        foreach (var a in areas.Areas)
            Console.WriteLine($"[areas]   #{a.Index + 1}{(a.IsMain ? " (main)" : "")}: chunk {a.MinCx}..{a.MaxCx} × {a.MinCz}..{a.MaxCz}, " +
                              $"columns {a.Columns}, blocks X {a.MinX}..{a.MaxX} Z {a.MinZ}..{a.MaxZ}, bbox {a.BlockCells:N0} columns");
        Console.WriteLine($"[areas] panning domain: X {areas.WorldBounds.MinX}..{areas.WorldBounds.MaxX}, " +
                          $"Z {areas.WorldBounds.MinZ}..{areas.WorldBounds.MaxZ}");
        Console.WriteLine("[areas] World tab: " + Cut((w.FindNamed<TextBlock>("MetaText")?.Text ?? "").Replace("\n", " · ")));
        Console.WriteLine($"[areas] map: {tm.Width}×{tm.Height} blocks, X {tm.MinX}..{tm.MinX + tm.Width - 1}, " +
                          $"Z {tm.MinZ}..{tm.MinZ + tm.Height - 1}, columns {tm.PresentChunks.Count}, area={AreaName(tm)}");
        int foreign = tm.Area is null ? 0 : tm.PresentChunks.Count(c => !tm.Area.ContainsChunk(c.cx, c.cz));
        Console.WriteLine($"[areas] columns of a foreign area in the map: {foreign}");
        var ws = areas.WorldWidth is int ww2 && areas.WorldLength is int ll2 ? (ww2, ll2) : ((int, int)?)null;
        bool mainAtCentre = ws is { } s2 && tm.Area is not null && tm.Area.ContainsBlock(s2.Item1 / 2.0, s2.Item2 / 2.0);
        Console.WriteLine($"[areas] starting area contains the world centre: " +
                          $"{(ws is null ? "world unknown (the largest one was taken)" : mainAtCentre ? "yes" : "NO")}");

        // "before": how many rows the whole save reads and whether it fits into one map
        long allRows, windowRows, allMs, windowMs;
        using (var f = VSSaveFile.OpenReadOnly(save))
        {
            var t0 = Stopwatch.StartNew();
            allRows = f.ChunkRowsWithData.Count;      // previous behaviour: the blob of the WHOLE save into memory
            allMs = t0.ElapsedMilliseconds;
            var t1 = Stopwatch.StartNew();
            windowRows = f.ChunkRowsInWindow(tm.Area).Count;
            windowMs = t1.ElapsedMilliseconds;
        }
        int unionW = areas.Areas.Max(a => a.MaxX) - areas.Areas.Min(a => a.MinX) + 1;
        int unionH = areas.Areas.Max(a => a.MaxZ) - areas.Areas.Min(a => a.MinZ) + 1;
        string unionVerdict;
        try { TerrainMap.EnsureAreaFits(unionW, unionH); unionVerdict = "fits"; }
        catch (InvalidOperationException ex) { unionVerdict = "does NOT fit: " + Cut(ex.Message); }
        Console.WriteLine($"[areas] total rows={allRows} ({allMs} ms, blobs of all areas), " +
                          $"in window={windowRows} ({windowMs} ms); common rectangle {unionW}×{unionH} — {unionVerdict}");

        if (areas.Areas.Count < 2)
        {
            Console.WriteLine("[areas] one area — nothing to switch or scroll to (behaviour as before)");
            Console.WriteLine("DONE");
            CloseNow(w);
            return;
        }

        // scroll to the far area: the window must build its map and NOT reset the zoom
        var far = areas.Areas.First(a => !ReferenceEquals(a, tm.Area));
        int requested = 0;
        map.AreaRequested += _ => requested++;
        map.CentreOn(far.CentreX, far.CentreZ, 1.0);
        var zoomAtRequest = map.PixelsPerBlock;   // before loading: keepView must preserve exactly it
        Pump(1.0);
        var end = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < end && !ReferenceEquals(map.Terrain?.Area, far)) Pump(0.3);
        Pump(1.5);
        Console.WriteLine($"[areas] scrolled to #{far.Index + 1}: AreaRequested={requested}, map area={AreaName(map.Terrain!)}, " +
                          $"columns={map.Terrain!.PresentChunks.Count}");
        Console.WriteLine($"[areas] zoom: after CentreOn={zoomAtRequest:0.###} px/block, after loading={map.PixelsPerBlock:0.###} " +
                          $"(keepView must not reset it to 'fit the area' = {Math.Min(1262.0 / far.WidthBlocks, 704.0 / far.HeightBlocks):0.###})");
        Console.WriteLine($"[areas] status: {Cut(TextOf(w, "StatusText"))}");
        Shot(w, "areas_far");

        // the void between areas: the map is not rebuilt, and the hint says where the nearest area is
        double gapX = (areas.Areas.Min(a => a.MaxX) + areas.Areas.Max(a => a.MinX)) / 2.0;
        double gapZ = (areas.Areas.Min(a => a.MaxZ) + areas.Areas.Max(a => a.MinZ)) / 2.0;
        map.CentreOn(gapX, gapZ, TerrainView.MinWanted);
        Pump(3.0);
        var centre = map.TranslatePoint(new Point(map.Bounds.Width / 2, map.Bounds.Height / 2), w) ?? new Point(10, 10);
        w.MouseMove(centre, RawInputModifiers.None);
        Pump(1.0);
        Console.WriteLine($"[areas] void X~{gapX:0} Z~{gapZ:0}: map area={AreaName(map.Terrain!)}, AreaRequested={requested}, " +
                          $"hover='{Cut(TextOf(w, "InfoText"))}'");
        Shot(w, "areas_void");

        // The overlay filter is bound to the AREA GRID: after switching it must rebuild,
        // and not silently disappear (SetTerrain resets the match map). A scan is needed — wait for it.
        // WaitLoaded is not suitable here: the App channel is already busy with area loading messages,
        // and it needs "opened: …" — so wait specifically for the scan channel
        var scanEnd = DateTime.UtcNow.AddSeconds(900);
        while (DateTime.UtcNow < scanEnd && !TextOf(w, "ScanStatusText").Contains("Scan complete") &&
               !TextOf(w, "ScanStatusText").StartsWith("Scan failed")) Pump(0.5);
        if (!TextOf(w, "ScanStatusText").Contains("Scan complete"))
        {
            Console.WriteLine("[areas] the scan never finished — the overlay after switching was not checked ('" +
                              Cut(TextOf(w, "ScanStatusText")) + "')");
        }
        else
        {
            EnsureDataPanelOpen(w);
            var tabs = w.FindNamed<TabControl>("DataTabs")!;
            for (int i = 0; i < tabs.ItemCount; i++)
                if ((tabs.Items[i] as TabItem)?.Header as string == "Find blocks") { tabs.SelectedIndex = i; break; }
            Pump(1.5);
            ClickButtonIn(w, "Clear");
            Pump(0.5);
            var panel = w.FindNamed<StackPanel>("FilterListPanel");
            var firstCb = panel is null ? null : Descendants(panel).OfType<CheckBox>().FirstOrDefault();
            if (firstCb is not null)
            {
                firstCb.IsChecked = true;
                Pump(1.0);
                Console.WriteLine($"[areas] overlay: selected code '{firstCb.Tag}'");
                ClickButtonIn(w, "Show on map");
                var ovEnd = DateTime.UtcNow.AddSeconds(600);
                while (DateTime.UtcNow < ovEnd && map.Filter is null) Pump(0.5);
                Console.WriteLine($"[areas] overlay on {AreaName(map.Terrain!)}: match map={DescribeGrid(map.Filter, map.Terrain!)}");
                // back to the first area — the overlay must rebuild for its grid
                var mainArea = areas.Main!;
                map.CentreOn(mainArea.CentreX, mainArea.CentreZ, 1.0);
                var backEnd = DateTime.UtcNow.AddSeconds(300);
                while (DateTime.UtcNow < backEnd && !ReferenceEquals(map.Terrain?.Area, mainArea)) Pump(0.3);
                var ovBack = DateTime.UtcNow.AddSeconds(600);
                while (DateTime.UtcNow < ovBack && map.Filter is null) Pump(0.5);
                Pump(1.0);
                Console.WriteLine($"[areas] back in {AreaName(map.Terrain!)}: AreaRequested={requested}, " +
                                  $"match map={DescribeGrid(map.Filter, map.Terrain!)}");
            }
        }

        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>Does the match map grid coincide with the terrain map grid (the overlay landed 1:1).</summary>
    private static string DescribeGrid(BlockFilterMap? filter, TerrainMap tm)
    {
        if (filter is null) return "NO";
        bool same = filter.MinX == tm.MinX && filter.MinZ == tm.MinZ && filter.Width == tm.Width && filter.Height == tm.Height;
        return $"{filter.Width}×{filter.Height} @ ({filter.MinX},{filter.MinZ}), columns {filter.HitColumns}, " +
               $"grid {(same ? "coincides with the map" : "does NOT coincide")}";
    }

    private static string AreaName(TerrainMap tm)
        => tm.Area is null ? "-" : $"{tm.Area.Index + 1} ({tm.Area.Columns:N0} chunks)";

    private static void ZoomFit(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(save);
        if (!WaitLoaded(w, SaveToken(save), "Scan complete", 900)) { Console.WriteLine("FAILED: load"); return; }
        Pump(2.0);

        var map = (TerrainView)w.FindNamed<Control>("Map")!;
        int rebuilds = 0;
        map.LodStatusChanged += _ => rebuilds++;
        map.FitToArea();          // the whole world in the window: it is certainly smaller than the screen along one axis
        Pump(3.0);

        var centreLocal = new Point(map.Bounds.Width / 2, map.Bounds.Height / 2);
        var centre = map.TranslatePoint(centreLocal, w) ?? centreLocal;
        Console.WriteLine($"[zoomfit] viewport={map.Bounds.Width:0}x{map.Bounds.Height:0} | lod='{Cut(TextOf(w, "LodStatusText"))}'");

        foreach (var dir in new[] { -1.0, 1.0 })   // zoom out first, then zoom in
        {
            rebuilds = 0;
            var before = map.DisplayCenterWorld();
            double maxJump = 0;
            for (int i = 0; i < 8; i++)
            {
                w.MouseWheel(centre, new Vector(0, dir), RawInputModifiers.None);
                Pump(0.05);   // shorter than a rebuild: this is exactly where it used to "stick" and jump
                var now = map.DisplayCenterWorld();
                maxJump = Math.Max(maxJump, Math.Max(Math.Abs(now.wx - before.wx), Math.Abs(now.wz - before.wz)));
            }
            Pump(6.0);
            Console.WriteLine($"[zoomfit] dir={(dir > 0 ? "in" : "out")}: rebuilds over 8 steps={rebuilds}, max centre shift={maxJump:0.0} blocks.");
        }
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>Pixel-by-pixel comparison: the on-screen render (TerrainView.ComputeRegion) vs the export (MapImageRenderer).
    /// Prints the first mismatches and their nature (colour / alpha / "no data").</summary>
    private static void ExportParity(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(save);
        if (!WaitLoaded(w, SaveToken(save), "Scan complete", 900)) { Console.WriteLine("FAILED: load"); return; }
        Pump(2.0);

        var map = (TerrainView)w.FindNamed<Control>("Map")!;
        var tm = map.Terrain!;
        using var f = VSSaveFile.OpenReadOnly(save);
        var reg = tm.Registry;

        var ids = tm.TopBlock.Where(b => b != 0).Distinct().Select(b => (int)b).Take(6).ToList();
        var fm = BlockFilterScanner.Build(f, tm, ids);
        var colors = new Dictionary<int, Avalonia.Media.Color>();
        foreach (var id in fm.TopBlock.Where(b => b != 0).Distinct())
            colors[id] = TerrainView.HighlightColor(reg?.CodeOf(id), id);

        // Compare the VISIBLE pixels: the editor buffer (BGRA) is composited over the map background — the same
        // way the control's Render does it (BgBrush). Both filter modes are compared with the export.
        var empty = (R: (byte)22, G: (byte)24, B: (byte)26);
        foreach (var (mode, dim) in new[]
        {
            (MapFilterRender.OverDimmedMap, true),
            (MapFilterRender.MatchesOnlyDark, false)
        })
        {
            map.SetFilterOverlay(fm, colors, dim);
            Pump(0.5);
            var cr = typeof(TerrainView).GetMethod("ComputeRegion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var editorBgra = (byte[])cr.Invoke(map, new object?[] { tm, 1.0, tm.MinX, tm.MinZ, tm.Width, tm.Height, null, TerrainView.ModeHeight })!;
            var (ew, eh, rgba) = MapImageRenderer.Render(tm, new MapImageOptions
            {
                Scale = 1, Mode = TerrainColorizer.ModeHeight, Filter = fm,
                FilterRender = mode, TransparentBackground = false
            });

            int colorDiff = 0, diffR = 0, diffG = 0, diffB = 0;
            string first = "-";
            for (int i = 0; i < ew * eh; i++)
            {
                byte eb = editorBgra[i * 4], eg = editorBgra[i * 4 + 1], er = editorBgra[i * 4 + 2], ea = editorBgra[i * 4 + 3];
                if (ea == 0) { er = empty.R; eg = empty.G; eb = empty.B; }   // map background under a transparent pixel
                byte rr = rgba[i * 4], rg = rgba[i * 4 + 1], rb = rgba[i * 4 + 2];
                if (er != rr || eg != rg || eb != rb)
                {
                    colorDiff++;
                    if (er != rr) diffR++; if (eg != rg) diffG++; if (eb != rb) diffB++;
                    if (first == "-") first = $"#{i} editor=({er},{eg},{eb}) export=({rr},{rg},{rb})";
                }
            }
            Console.WriteLine($"[exportparity] mode={mode}: pixels={ew * eh} differences={colorDiff} (R{diffR}/G{diffG}/B{diffB})");
            Console.WriteLine($"[exportparity]   first difference: {first}");
        }
        CloseNow(w);
        Pump(0.3);
        Console.WriteLine("DONE");
    }

    /// <summary>The chunk with the largest number of existing neighbours (3×3) — the window around it is dense with data.</summary>
    private static (int cx, int cz) DenseAnchor(TerrainMap tm)
    {
        var set = tm.PresentChunks;
        (int cx, int cz) best = default; int bestScore = -1;
        foreach (var (cx, cz) in set)
        {
            int s = 0;
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (set.Contains((cx + dx, cz + dz))) s++;
            if (s > bestScore) { bestScore = s; best = (cx, cz); }
        }
        return best;
    }

    /// <summary>Slice diagnostics: how many columns of the area actually have a block at each Y and
    /// how many chunks of the corresponding cy exist in the save at all (a TryBlockAt check against the raw data).</summary>
    private static void YLayerScan(string save)
    {
        using var f = VSSaveFile.OpenReadOnly(save);
        var tm = TerrainMap.Build(f);
        Console.WriteLine($"[ylayerscan] {System.IO.Path.GetFileName(save)}: world {tm.Width}×{tm.Height} Y data {tm.MinWorldY}..{tm.MaxWorldY} surface {tm.MinSurface}..{tm.MaxSurface}");

        int rw = Math.Min(256, tm.Width), rh = Math.Min(256, tm.Height);
        var (acx, acz) = DenseAnchor(tm);
        int left = Math.Clamp(acx * 32 + 16 - rw / 2, tm.MinX, tm.MinX + tm.Width - rw);
        int top = Math.Clamp(acz * 32 + 16 - rh / 2, tm.MinZ, tm.MinZ + tm.Height - rh);
        Console.WriteLine($"[ylayerscan] window {rw}×{rh} @ ({left},{top}), dense chunk ({acx},{acz})");

        foreach (int y in new[] { 0, 3, 8, 16, 24, 32, 48, 64, 96, 110, 128, 160, 192, 224, 250, 256, 257, 288 })
        {
            int solid = 0, nodata = 0, missing = 0;
            var seen = new HashSet<long>();
            for (int z = 0; z < rh; z++)
                for (int x = 0; x < rw; x++)
                {
                    int wx = left + x, wz = top + z;
                    long key = (long)ChunkPosHelper.ToIndex(wx >> 5, y >> 5, wz >> 5);
                    if (seen.Add(key) && f.ReadChunk(key) is null) missing++;
                    if (!tm.TryBlockAt(wx, y, wz, out var id)) { nodata++; continue; }
                    if (id != 0) solid++;
                }
            Console.WriteLine($"  Y={y,3} (cy={y >> 5}, ly={y & 31}): solid={solid,6}/{rw * rh}  nodata={nodata,6}  chunks missing={missing}/{seen.Count}");
        }

        // Profile by local Y for those cy chunks of the dense column that actually exist in the save
        if (tm.Voxels is not null)
            for (int cy = 0; cy <= 8; cy++)
            {
                var b = tm.Voxels.GetChunkBlocks(acx, cy, acz);
                if (b is null) { Console.WriteLine($"  chunk ({acx},{cy},{acz}): no data"); continue; }
                var per = new int[32];
                for (int y = 0; y < 32; y++)
                {
                    int c = 0;
                    for (int x = 0; x < 32; x++) for (int z = 0; z < 32; z++) if (b[x + z * 32 + y * 1024] != 0) c++;
                    per[y] = c;
                }
                Console.WriteLine($"  chunk ({acx},{cy},{acz}) blocks by local Y: " + string.Join(",", per));
            }
        Console.WriteLine("DONE");
    }

    /// <summary>How chiselled blocks are actually coloured on the map: how many columns are chiselled, how many of them
    /// have a material found and how many of those get a grey tone (saturation &lt; threshold).</summary>
    private static void ChiselColor(string save)
    {
        using var f = VSSaveFile.OpenReadOnly(save);
        var tm = TerrainMap.Build(f);
        var reg = tm.Registry;
        double range = Math.Max(1, tm.MaxSurface - tm.MinSurface);
        int chiselCols = 0, withMat = 0, noMat = 0, grey = 0, colour = 0;
        int shown = 0, covered = 0, tinted = 0;
        var byCode = new Dictionary<string, (int n, int grey, byte r, byte g, byte b)>();

        for (int i = 0; i < tm.SurfaceY.Length; i++)
        {
            if (tm.SurfaceY[i] <= 0) continue;
            int id = tm.TopBlock[i];
            var code = reg?.CodeOf(id);
            shown++;
            if (BlockColors.TryGet(code, out _)) covered++;
            if (BlockColorPalette.IsShaderTinted(code)) tinted++;
            if (!BlockColorPalette.IsChiseled(code)) continue;
            chiselCols++;
            int wx = tm.MinX + i % tm.Width, wz = tm.MinZ + i / tm.Width;
            int matId = tm.ChiselMaterial.TryGetValue(TerrainMap.PackColumn(wx, wz), out var m) ? m : 0;
            if (matId <= 0) noMat++; else withMat++;
            var (r, g, b) = TerrainColorizer.ColumnColor(tm, i, tm.SurfaceY[i], null, TerrainColorizer.ModeBlocks, range);
            int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            bool isGrey = mx - mn < 14;
            if (isGrey) grey++; else colour++;
            var mcode = matId > 0 ? (reg?.CodeOf(matId) ?? $"id:{matId}") : "(no material)";
            var cur = byCode.TryGetValue(mcode, out var v) ? v : (n: 0, grey: 0, r: (byte)0, g: (byte)0, b: (byte)0);
            byCode[mcode] = (cur.n + 1, cur.grey + (isGrey ? 1 : 0), r, g, b);
        }

        Console.WriteLine($"[chiselcolor] {System.IO.Path.GetFileName(save)}: chiselled columns={chiselCols}, with material={withMat}, WITHOUT material={noMat}");
        Console.WriteLine($"[chiselcolor] tone: grey={grey} ({100.0 * grey / Math.Max(1, chiselCols):0.0}%), coloured={colour}");
        Console.WriteLine($"[chiselcolor] asset table: loaded={BlockColors.Loaded}, codes={BlockColors.Count}");
        Console.WriteLine("  material → colour (top-20 by frequency):");
        foreach (var kv in byCode.OrderByDescending(k => k.Value.n).Take(20))
            Console.WriteLine($"    {kv.Key,-34} n={kv.Value.n,6} grey={kv.Value.grey,6}  rgb=({kv.Value.r},{kv.Value.g},{kv.Value.b})");
        int distinctGrey = byCode.Where(k => k.Value.grey * 2 > k.Value.n).Select(k => $"({k.Value.r},{k.Value.g},{k.Value.b})").Distinct().Count();
        Console.WriteLine($"  distinct grey tones among predominantly grey materials: {distinctGrey}");
        Console.WriteLine($"[chiselcolor] surface coverage by the asset table: {covered}/{shown} ({100.0 * covered / Math.Max(1, shown):0.0}%), shader-tinted={tinted}");
        Console.WriteLine("DONE");
    }

    /// <summary>Chiselled block diagnostics: how many block entities were found, what is in the materials field and
    /// how those ids resolve to codes (dumps of the first payloads + statistics).</summary>
    private static void ChiselDump(string save)
    {
        using var f = VSSaveFile.OpenReadOnly(save);
        var reg = f.BlockRegistry;
        var rows = f.ChunkRowsWithData;
        var needles = new[]
        {
            System.Text.Encoding.ASCII.GetBytes("MicroBlock"),
            System.Text.Encoding.ASCII.GetBytes("Chisel")
        };
        int found = 0, noRead = 0, empty = 0, zeroFirst = 0, unresolved = 0, resolved = 0, dumped = 0;
        var codeHist = new Dictionary<string, int>();
        var rawHist = new Dictionary<int, int>();
        var lenHist = new Dictionary<int, int>();
        var metaFirst = new Dictionary<string, int>();
        var nameHist = new Dictionary<string, int>();

        foreach (var ch in rows)
        {
            var blob = ch.Data;
            foreach (var needle in needles)
            {
                int from = 0;
                while (from <= blob.Length - needle.Length)
                {
                    int rel = blob.AsSpan(from).IndexOf(needle);
                    if (rel < 0) break;
                    int namePos = from + rel;
                    from = namePos + 1;
                    int payloadStart = namePos - 1;
                    if (payloadStart < 3 || blob[payloadStart] != (byte)needle.Length) continue;
                    int vEnd = payloadStart - 1, vStart = vEnd;
                    while (vStart > 0 && (blob[vStart - 1] & 0x80) != 0) vStart--;
                    long len = 0; int shift = 0;
                    for (int k = vStart; k <= vEnd; k++, shift += 7) len |= (long)(blob[k] & 0x7F) << shift;
                    if (vStart < 1 || blob[vStart - 1] != 0x42) continue;
                    long end = payloadStart + len;
                    if (len < needle.Length + 1 || end > blob.Length) continue;
                    found++;
                    var payload = new byte[(int)len];
                    Array.Copy(blob, payloadStart, payload, 0, (int)len);
                    if (!ChiselMaterials.TryRead(payload, reg, out _, out _, out _, out var mats)) noRead++;
                    else if (mats.Length == 0 || mats[0] <= 0) { if (mats.Length == 0) empty++; else zeroFirst++; }
                    else
                    {
                        var code = reg.CodeOf(mats[0]);
                        if (code is null) { unresolved++; rawHist[mats[0]] = rawHist.TryGetValue(mats[0], out var c) ? c + 1 : 1; }
                        else { resolved++; codeHist[code] = codeHist.TryGetValue(code, out var c) ? c + 1 : 1; }
                    }
                    if (mats.Length > 0)
                    {
                        lenHist[mats.Length] = lenHist.TryGetValue(mats.Length, out var lc) ? lc + 1 : 1;
                        var mc = reg.CodeOf(mats[0]);
                        if (mc is not null && mc.StartsWith("meta", StringComparison.OrdinalIgnoreCase))
                            metaFirst[mc] = metaFirst.TryGetValue(mc, out var mcCount) ? mcCount + 1 : 1;
                    }
                    var bn = ReadLpString(payload, "blockName");
                    if (!string.IsNullOrEmpty(bn)) nameHist[bn] = nameHist.TryGetValue(bn, out var nc) ? nc + 1 : 1;
                    if (dumped < 3) { dumped++; DumpBe(payload, needle, reg); }
                    from = (int)end;
                }
            }
        }
        Console.WriteLine($"[chiseldump] {System.IO.Path.GetFileName(save)}: BE={found} | unparsed={noRead} empty={empty} mats[0]<=0={zeroFirst} | code found={resolved} missing={unresolved}");
        Console.WriteLine("  top codes: " + string.Join(", ", codeHist.OrderByDescending(k => k.Value).Take(15).Select(k => $"{k.Key}:{k.Value}")));
        Console.WriteLine("  ids without a code: " + string.Join(", ", rawHist.OrderByDescending(k => k.Value).Take(10).Select(k => $"{k.Key}:{k.Value}")));
        Console.WriteLine($"  registry: ids={reg.Count}, example CodeOf(1)={reg.CodeOf(1) ?? "null"}");
        Console.WriteLine("  materials length: " + string.Join(", ", lenHist.OrderBy(k => k.Key).Select(k => $"{k.Key}→{k.Value}")));
        Console.WriteLine("  meta-* in place of the material: " + string.Join(", ", metaFirst.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Key}:{k.Value}")));
        Console.WriteLine("  blockName (non-empty " + nameHist.Values.Sum() + "): " + string.Join(", ", nameHist.OrderByDescending(k => k.Value).Take(10).Select(k => $"{k.Key}:{k.Value}")));
        Console.WriteLine("DONE");
    }

    /// <summary>LP field string with the given name (type-byte + [len][key] + [len][value]).</summary>
    private static string? ReadLpString(byte[] b, string key)
    {
        for (int i = 2; i + 1 < b.Length; i++)
        {
            if (b[i - 1] != (byte)key.Length || b[i - 2] != 0x05) continue;
            bool ok = true;
            for (int j = 0; j < key.Length && ok; j++) if (b[i + j] != (byte)key[j]) ok = false;
            if (!ok) continue;
            int p = i + key.Length;
            if (p >= b.Length) return null;
            int len = b[p++];
            if (len == 0 || p + len > b.Length) return null;
            return System.Text.Encoding.UTF8.GetString(b, p, len);
        }
        return null;
    }

    private static void DumpBe(byte[] payload, byte[] typeName, BlockRegistry reg)
    {
        Console.WriteLine($"--- BE {System.Text.Encoding.ASCII.GetString(typeName)} len={payload.Length} ---");
        var sb = new System.Text.StringBuilder();
        foreach (var b in payload) sb.Append(b >= 32 && b < 127 ? (char)b : '.');
        Console.WriteLine("  ascii: " + sb);
        for (int o = 0; o < payload.Length && o < 320; o += 32)
            Console.WriteLine($"  {o,4}: " + string.Join(" ", payload.Skip(o).Take(32).Select(b => b.ToString("x2"))));
        var keys = new List<string>();
        for (int i = 2; i < payload.Length; i++)
        {
            int len = payload[i - 1];
            if (len is < 3 or > 24 || i + len > payload.Length) continue;
            bool ok = true;
            for (int j = 0; j < len; j++) { byte c = payload[i + j]; if (c < 32 || c > 126) { ok = false; break; } }
            if (ok) keys.Add($"f{payload[i - 2]:x2}:" + System.Text.Encoding.ASCII.GetString(payload, i, len));
        }
        Console.WriteLine("  keys: " + string.Join(", ", keys));
        if (ChiselMaterials.TryRead(payload, reg, out int wx, out int wy, out int wz, out var mats))
            Console.WriteLine($"  TryRead: pos=({wx},{wy},{wz}) materials=[{string.Join(",", mats.Take(6))}] codes=[{string.Join(",", mats.Take(6).Select(m => reg.CodeOf(m) ?? "null"))}]");
        else
            Console.WriteLine("  TryRead: false");
    }

    /// <summary>Y slice (the "Y layer" slider): real blocks at a level, editor/export parity,
    /// chunk cache reuse and screenshots.</summary>
    private static void LayerSlice(string save)
    {
        var token = System.IO.Path.GetFileNameWithoutExtension(save).Split(' ')[0];
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(save);
        if (!WaitLoaded(w, token, "Scan complete", 1200)) { Console.WriteLine("FAILED: load"); return; }

        var map = (TerrainView)w.FindNamed<Control>("Map")!;
        var tm = map.Terrain!;
        var slider = w.FindNamed<Slider>("LayerSlider")!;
        slider.Minimum = tm.MinWorldY; slider.Maximum = tm.MaxWorldY;   // same as in LoadSaveAsync
        Console.WriteLine($"[layerslice] {System.IO.Path.GetFileName(save)}: world {tm.Width}×{tm.Height} | Y data {tm.MinWorldY}..{tm.MaxWorldY} | surface {tm.MinSurface}..{tm.MaxSurface}");
        Console.WriteLine($"[layerslice] slider {slider.Minimum}..{slider.Maximum} | voxels={(tm.Voxels is null ? "none" : "present")}");
        if (tm.Voxels is null) { Console.WriteLine("FAILED: no voxel source"); return; }

        int rw = Math.Min(256, tm.Width), rh = Math.Min(256, tm.Height);
        // take the window around the DENSEST chunk ("freestyle place" has most of the bbox empty)
        var (acx, acz) = DenseAnchor(tm);
        int left = Math.Clamp(acx * 32 + 16 - rw / 2, tm.MinX, tm.MinX + tm.Width - rw);
        int top = Math.Clamp(acz * 32 + 16 - rh / 2, tm.MinZ, tm.MinZ + tm.Height - rh);
        // Y inside the rock of the same chunk: just below its surface (guaranteed to have blocks)
        int acol = tm.ColumnIndex(acx * 32 + 16, acz * 32 + 16);
        int y = Math.Clamp(acol >= 0 && tm.SurfaceY[acol] > 0 ? tm.SurfaceY[acol] - 1 : tm.MinSurface,
                           tm.MinWorldY, tm.MaxWorldY);
        Console.WriteLine($"[layerslice] window {rw}×{rh} @ ({left},{top}), dense chunk ({acx},{acz}), column surface {tm.SurfaceY[acol]}");

        var cr = typeof(TerrainView).GetMethod("ComputeRegion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        map.FitToArea();
        Pump(1.0);
        slider.Value = y;
        Pump(2.5);
        Console.WriteLine($"[layerslice] layer label: '{TextOf(w, "LayerLabel")}'");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var editorBgra = (byte[])cr.Invoke(map, new object?[] { tm, 1.0, left, top, rw, rh, y, TerrainView.ModeBlocks })!;
        sw.Stop();
        var (ew, eh, rgba) = MapImageRenderer.Render(tm, new MapImageOptions
        {
            Scale = 1, Mode = TerrainColorizer.ModeBlocks, LayerY = y, TransparentBackground = false,
            Region = new MapRegion(left, top, left + rw - 1, top + rh - 1)
        });

        // The editor draws a transparent background under the air (like the map's BgBrush), the export uses EmptyColor: compare the same way.
        var empty = (R: (byte)22, G: (byte)24, B: (byte)26);
        int diff = 0, solid = 0, air = 0; string first = "-";
        var distinct = new HashSet<int>();
        for (int i = 0; i < ew * eh; i++)
        {
            byte eb = editorBgra[i * 4], eg = editorBgra[i * 4 + 1], er = editorBgra[i * 4 + 2], ea = editorBgra[i * 4 + 3];
            if (ea == 0) { air++; er = empty.R; eg = empty.G; eb = empty.B; }
            else { solid++; distinct.Add((er << 16) | (eg << 8) | eb); }
            byte rr = rgba[i * 4], rg = rgba[i * 4 + 1], rb = rgba[i * 4 + 2];
            if (er != rr || eg != rg || eb != rb)
            {
                diff++;
                if (first == "-") first = $"#{i} editor=({er},{eg},{eb}) export=({rr},{rg},{rb})";
            }
        }
        Console.WriteLine($"[layerslice] region {rw}×{rh} at Y={y}: render {sw.ElapsedMilliseconds} ms | solid pixels={solid} air={air} distinct colours={distinct.Count}");
        Console.WriteLine($"[layerslice] editor/export parity: differences={diff}/{ew * eh} ({first})");

        // Control of the "air = background" semantics: there must be no blocks above the highest terrain
        int above = tm.MaxSurface + 4;
        var aboveBgra = (byte[])cr.Invoke(map, new object?[] { tm, 1.0, left, top, rw, rh, above, TerrainView.ModeBlocks })!;
        int aboveSolid = 0;
        for (int i = 0; i < rw * rh; i++) if (aboveBgra[i * 4 + 3] != 0) aboveSolid++;
        Console.WriteLine($"[layerslice] above the terrain (Y={above}): solid pixels={aboveSolid}/{rw * rh} (expected 0)");

        var codes = new Dictionary<string, int>();
        var codeRgb = new Dictionary<string, (byte r, byte g, byte b)>();
        for (int z = 0; z < rh; z++)
            for (int x = 0; x < rw; x++)
            {
                if (!tm.TryBlockAt(left + x, y, top + z, out var id) || id == 0) continue;
                var code = tm.Registry?.CodeOf(id) ?? ("id:" + id);
                codes[code] = codes.TryGetValue(code, out var c) ? c + 1 : 1;
                if (!codeRgb.ContainsKey(code))
                    codeRgb[code] = BlockColorPalette.ColorFor(code, id);
            }
        Console.WriteLine("[layerslice] blocks at the level: " + string.Join(", ", codes.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Key}:{k.Value}")));
        Console.WriteLine("[layerslice] darkest: " + string.Join(", ", codeRgb
            .OrderBy(k => k.Value.r + k.Value.g + k.Value.b).Take(5)
            .Select(k => $"{k.Key}=({k.Value.r},{k.Value.g},{k.Value.b}) n={codes[k.Key]}")));
        Console.WriteLine("[layerslice] asset table coverage in the window: " +
            codeRgb.Count(k => BlockColors.TryGet(k.Key, out _)) + "/" + codeRgb.Count);

        // Cache: the neighbouring Y in the same 32-block band (same cy) must not decode the chunks again
        if (tm.Voxels is SaveVoxelSource vs)
        {
            long before = vs.DecodedChunks;
            cr.Invoke(map, new object?[] { tm, 1.0, left, top, rw, rh, y + 1, TerrainView.ModeBlocks });
            Console.WriteLine($"[layerslice] Y={y} -> Y={y + 1}: new decodings {vs.DecodedChunks - before}, cache {vs.CachedChunks} chunks (~{vs.CachedChunks * 64 / 1024} MB)");
        }

        Shot(w, "slice_layer");
        // Close-up: at high zoom you can see individual blocks/caves rather than a "two-colour mask"
        map.ZoomBy(6.0);
        Pump(2.0);
        Shot(w, "slice_zoom");
        map.FitToArea();
        Pump(1.0);
        // Back to the surface: the same region must become an ordinary map again
        w.FindNamed<Button>("SurfaceBtn")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(1.5);
        Console.WriteLine($"[layerslice] after Surface: LayerY={map.LayerY?.ToString() ?? "null"}, label '{TextOf(w, "LayerLabel")}'");
        Shot(w, "slice_surface");

        CloseNow(w);
        Pump(0.3);
        Console.WriteLine("DONE");
    }

    /// <summary>Selection gestures: Shift+LMB (drag/click) selects, Shift+RMB (drag/click) deselects.</summary>
    private static void SelectGesture(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(save);
        if (!WaitLoaded(w, SaveToken(save), "Scan complete", 900)) { Console.WriteLine("FAILED: load"); return; }
        Pump(2.0);

        var map = (TerrainView)w.FindNamed<Control>("Map")!;
        map.FitToArea();
        Pump(2.0);

        Point W(double x, double y) => map.TranslatePoint(new Point(x, y), w) ?? new Point(x, y);
        var a = W(map.Bounds.Width * 0.35, map.Bounds.Height * 0.35);
        var b = W(map.Bounds.Width * 0.60, map.Bounds.Height * 0.60);
        var inner = W(map.Bounds.Width * 0.45, map.Bounds.Height * 0.45);

        // Shift+LMB drag — select a region
        w.MouseDown(a, MouseButton.Left, RawInputModifiers.Shift);
        w.MouseMove(b, RawInputModifiers.Shift);
        w.MouseUp(b, MouseButton.Left, RawInputModifiers.Shift);
        Pump(0.4);
        int afterSelect = map.SelectionCount;
        Console.WriteLine($"[selectgesture] Shift+LMB drag -> chunks selected: {afterSelect}");

        // Shift+RMB click on a single chunk. The point is taken as map "percentages" AT THE MOMENT of the click (not before
        // the drag) and we try several points inside the selected rectangle: a particular
        // point may have no chunk under it (a hole in the world), and then there is nothing to deselect — that is not a gesture error.
        var afterDeselectClick = afterSelect;
        foreach (var (fx, fy) in new[] { (0.45, 0.45), (0.50, 0.50), (0.55, 0.55), (0.40, 0.50), (0.50, 0.40) })
        {
            var pt = W(map.Bounds.Width * fx, map.Bounds.Height * fy);
            w.MouseDown(pt, MouseButton.Right, RawInputModifiers.Shift);
            w.MouseUp(pt, MouseButton.Right, RawInputModifiers.Shift);
            Pump(0.5);
            afterDeselectClick = map.SelectionCount;
            Console.WriteLine($"[selectgesture] Shift+RMB click ({fx:0.00},{fy:0.00}) -> chunks left: {afterDeselectClick}"
                              + $" (deselected {afterSelect - afterDeselectClick})");
            if (afterDeselectClick < afterSelect) break;
        }

        // Shift+RMB drag over the inner part — deselect a region
        w.MouseDown(a, MouseButton.Right, RawInputModifiers.Shift);
        w.MouseMove(inner, RawInputModifiers.Shift);
        w.MouseUp(inner, MouseButton.Right, RawInputModifiers.Shift);
        Pump(0.4);
        int afterDeselectRect = map.SelectionCount;
        Console.WriteLine($"[selectgesture] Shift+RMB drag -> chunks left: {afterDeselectRect}");

        Console.WriteLine(afterSelect > 0 && afterDeselectClick < afterSelect && afterDeselectRect < afterDeselectClick
            ? "[selectgesture] OK: Shift+RMB deselects (the inverse action of Shift+LMB)"
            : "[selectgesture] FAILED: the inverse action did not work");
        CloseNow(w);
        Pump(0.3);
        Console.WriteLine("DONE");
    }

    /// <summary>The bottom drawer is an overlay: the map does not change size, the drawer lies ON TOP of it.</summary>
    private static void Drawer(string bigSave)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(bigSave);
        if (!WaitLoaded(w, SaveToken(bigSave), "Scan complete", 900)) { Console.WriteLine("FAILED: load"); return; }
        Pump(2.0);

        var map = w.FindNamed<Control>("Map")!;
        var panel = w.FindNamed<Expander>("DataPanel")!;
        var splitter = w.FindNamed<GridSplitter>("PanelSplitter")!;
        double MapH() => map.Bounds.Height;
        double MapW() => map.Bounds.Width;
        void Report(string phase)
        {
            // vertical overlap: >0 means the drawer really lies over the map
            double overlap = Math.Max(0, Math.Min(map.Bounds.Bottom, panel.Bounds.Bottom) - Math.Max(map.Bounds.Top, panel.Bounds.Top));
            Console.WriteLine($"[drawer] {phase}: map={MapW():0}x{MapH():0} drawer={panel.Bounds.Width:0}x{panel.Bounds.Height:0}"
                + $" splitter visible={splitter.IsVisible} vertical overlap={overlap:0} px");
        }

        var st = PrivField(w, "_settings")!;
        Console.WriteLine("[drawer] settings.ShowDataTabs=" + st.GetType().GetProperty("ShowDataTabs")!.GetValue(st)
            + " panel.IsExpanded=" + panel.IsExpanded
            + " toolbar/status window height=" + w.Height);
        Pump(3.0);
        Console.WriteLine("[drawer] after 3 s: panel.IsExpanded=" + panel.IsExpanded + " drawer=" + panel.Bounds.Height.ToString("0"));
        if (!panel.IsExpanded) { panel.IsExpanded = true; Pump(1.5); }
        Report("expanded");
        double hExpanded = MapH();
        Shot(w, "drawer_01_overlay_expanded");

        panel.IsExpanded = false; Pump(1.5);
        Report("collapsed");
        double hCollapsed = MapH();
        Shot(w, "drawer_02_collapsed");

        panel.IsExpanded = true; Pump(1.5);
        Report("expanded again");

        // emulate dragging the splitter: change the ratio and apply the layout
        var settings = PrivField(w, "_settings")!;
        settings.GetType().GetProperty("PanelRatio")!.SetValue(settings, 0.62);
        w.GetType().GetMethod("ApplyPanelRowLayout", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, null);
        Pump(1.5);
        Report("drawer enlarged (ratio=0.62)");
        double hWide = MapH();
        Shot(w, "drawer_03_tall_drawer");

        Console.WriteLine($"[drawer] SUMMARY: map height expanded={hExpanded:0} collapsed={hCollapsed:0} tall-drawer={hWide:0}"
            + $" → max difference={Math.Max(Math.Abs(hExpanded - hCollapsed), Math.Abs(hExpanded - hWide)):0.0} px (expected ~0)");
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>Dark theme audit: takes a frame and prints the rectangles of all controls,
    /// so that pixels reveal the elements that stayed light in the dark theme.</summary>
    private static void DarkAudit(string bigSave, bool switchAfterBuild)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        if (!switchAfterBuild) { w.RequestedThemeVariant = ThemeVariant.Dark; Pump(1.0); }
        Console.WriteLine("[darkaudit] theme before the UI is built: Requested=" + w.RequestedThemeVariant + " Actual=" + w.ActualThemeVariant);
        ResetStatus(w);
        w.OpenStartupFile(bigSave);
        var token = System.IO.Path.GetFileNameWithoutExtension(bigSave);
        if (!WaitLoaded(w, token, "Scan complete", 900))
        {
            Console.WriteLine("FAILED: load; app='" + Cut(TextOf(w, "StatusText")) + "' scan='" + Cut(TextOf(w, "ScanStatusText")) + "' line='" + Cut(StatusLineOf(w)) + "'");
            return;
        }
        EnsureDataPanelOpen(w);
        Console.WriteLine("[darkaudit] RequestedThemeVariant=" + w.RequestedThemeVariant + " Actual=" + w.ActualThemeVariant);

        void Dump(string shot)
        {
            Shot(w, shot);
            Console.WriteLine("[darkaudit] rects for " + shot);
            foreach (var v in Descendants(w))
            {
                if (v is not Control c || c.Bounds.Width < 8 || c.Bounds.Height < 8) continue;
                var p0 = c.TranslatePoint(new Point(0, 0), w) ?? new Point(-1, -1);
                if (p0.X < 0 || p0.Y < 0 || p0.X >= 1280 || p0.Y >= 800) continue;
                var name = c.Name ?? "";
                var text = c switch
                {
                    TextBlock tb => tb.Text,
                    Button b => b.Content as string,
                    _ => null
                };
                Console.WriteLine($"RECT|{c.GetType().Name}|{name}|{p0.X:0}|{p0.Y:0}|{c.Bounds.Width:0}|{c.Bounds.Height:0}|{Cut(text)}");
            }
        }

        // chunk cards
        var pick = w.FindNamed<ComboBox>("ChunkPick")!;
        // Look for a chunk with creatures first, then with a BlockEntity: cards of both types must be in the frame.
        foreach (var want in new[] { "entities [1-9]", "be [1-9]" })
        {
            var rx = new System.Text.RegularExpressions.Regex(want);
            for (int i = 0; i < pick.ItemCount && i < 60; i++)
            {
                var label = pick.Items[i] as string ?? "";
                if (!rx.IsMatch(label)) continue;
                pick.SelectedIndex = i;
                Pump(1.5);
                var panel = w.FindNamed<StackPanel>("ChunkDataPanel")!;
                if (Descendants(panel).OfType<Button>().Any(b => (b.Content as string) == "Edit fields"))
                {
                    Console.WriteLine("[darkaudit] chunk for the frame: '" + label + "' (looked for " + want + ")");
                    goto picked;
                }
            }
        }
        picked: ;
        Pump(1.0);
        if (switchAfterBuild)
        {
            // the UI is already built in the previous theme: see whether the elements survive a theme change
            w.RequestedThemeVariant = ThemeVariant.Dark;
            Pump(3.0);
            Console.WriteLine("[darkaudit] theme AFTER the switch: Requested=" + w.RequestedThemeVariant + " Actual=" + w.ActualThemeVariant);
            Dump("switch_01_cards_after_theme_change");
        }
        Dump("dark_01_cards");

        // block list and dump
        var tabs = w.FindNamed<TabControl>("DataTabs")!;
        for (int i = 0; i < tabs.ItemCount; i++)
        {
            var header = (tabs.Items[i] as TabItem)?.Header as string;
            if (header is "Find blocks" or "Dump (text)")
            {
                tabs.SelectedIndex = i;
                Pump(2.0);
                Dump("dark_0" + (i + 1) + "_" + (header == "Find blocks" ? "findblocks" : "dump"));
            }
        }

        // hover over the map: tooltip and status line
        var map = w.FindNamed<Control>("Map")!;
        var local = new Point(map.Bounds.Width / 2, map.Bounds.Height / 2);
        var win = map.TranslatePoint(local, w) ?? local;
        w.MouseMove(win, RawInputModifiers.None);
        Pump(1.0);
        Dump(switchAfterBuild ? "switch_02_hover" : "dark_05_hover");

        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    private static bool HasCyr(string? t)
        => !string.IsNullOrEmpty(t) && t.Any(ch => ch >= 0x0400 && ch <= 0x04FF);

    /// <summary>P1-6: the legend explains the colours of the active mode and does not change the map size; the empty
    /// "Chunk data" panel explains what to do.</summary>
    private static void Legend(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(save);
        var token = System.IO.Path.GetFileNameWithoutExtension(save);
        if (!WaitLoaded(w, token.Length > 20 ? token.Substring(0, 18) : token, "Scan complete", 900)) { Console.WriteLine("FAILED: load"); return; }
        EnsureDataPanelOpen(w);

        var map = w.FindNamed<Control>("Map")!;
        var host = w.FindNamed<ContentControl>("MapLegendHost")!;
        string Titles() => string.Join(" / ", Descendants(host).OfType<TextBlock>().Select(t => t.Text));
        int Marks() => Descendants(host).OfType<Border>().Count();
        Console.WriteLine("  legend visible=" + host.IsVisible + ", marks=" + Marks() + ", text='" + Cut(Titles()) + "'");

        double hWith = map.Bounds.Height;
        host.IsVisible = false; Pump(0.6);
        double hWithout = map.Bounds.Height;
        host.IsVisible = true; Pump(0.6);
        Console.WriteLine("  map height with legend=" + hWith.ToString("0") + " without=" + hWithout.ToString("0")
            + " (expected equal: the legend is an overlay)");

        // the mode radios and the layer slider live in the Settings tab — without opening it
        // unselected tabs are not realized in the visual tree
        var tabs = w.FindNamed<TabControl>("DataTabs")!;
        int settingsIdx = -1;
        for (int i = 0; i < tabs.ItemCount; i++)
            if ((tabs.Items[i] as TabItem)?.Header as string == "Settings") settingsIdx = i;
        if (settingsIdx < 0) { Console.WriteLine("FAILED: no Settings tab"); return; }
        tabs.SelectedIndex = settingsIdx;
        Pump(1.5);

        foreach (var (tag, name) in new[] { ("1", "blocks"), ("2", "depth"), ("0", "heightmap") })
        {
            var rb = Descendants(w).OfType<RadioButton>().FirstOrDefault(r => (r.Tag as string) == tag);
            if (rb is null) { Console.WriteLine("  FAILED: no radio button for mode " + tag); return; }
            rb.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Pump(1.5);
            Console.WriteLine("  mode " + name + ": marks=" + Marks() + ", text='" + Cut(Titles()) + "'");
            Shot(w, "legend_" + name);
        }

        var slider = w.FindNamed<Slider>("LayerSlider")!;
        slider.Value = Math.Round((slider.Minimum + slider.Maximum) / 2);
        Pump(2.0);
        Console.WriteLine("  layer Y=" + slider.Value + ": text='" + Cut(Titles()) + "'");
        Shot(w, "legend_layer");

        // restore the settings to their original state: the probe must not leave the app in slice mode
        w.FindNamed<Button>("SurfaceBtn")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Pump(1.0);
        var heightRb = Descendants(w).OfType<RadioButton>().FirstOrDefault(r => (r.Tag as string) == "0");
        if (heightRb is not null) { heightRb.IsChecked = true; Dispatcher.UIThread.RunJobs(); Pump(1.0); }
        Console.WriteLine("  settings restored: surface='" + Cut(TextOf(w, "LayerLabel")) + "'");

        ((TerrainView)map).ClearSelection();
        Pump(1.2);
        var panel = w.FindNamed<StackPanel>("ChunkDataPanel")!;
        Console.WriteLine("  panel empty state: '" + Cut(string.Join(" | ", Descendants(panel).OfType<TextBlock>().Select(t => t.Text))) + "'");
        Shot(w, "legend_empty_state");
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>P1-11: card limits — the panel is built in batches, fields and players expand lazily.</summary>
    private static void CardLimits(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(save);
        var token = System.IO.Path.GetFileNameWithoutExtension(save);
        if (!WaitLoaded(w, token.Length > 20 ? token.Substring(0, 18) : token, "Scan complete", 900))
        { Console.WriteLine("FAILED: load " + save); return; }
        EnsureDataPanelOpen(w);

        var panel = w.FindNamed<StackPanel>("ChunkDataPanel")!;
        var pick = w.FindNamed<ComboBox>("ChunkPick")!;
        int best = -1, bestSecs = 0;
        for (int i = 0; i < pick.ItemCount && i < 40; i++)
        {
            pick.SelectedIndex = i;
            Pump(0.6);
            if (panel.Children.Count > bestSecs) { bestSecs = panel.Children.Count; best = i; }
        }
        if (best < 0) { Console.WriteLine("FAILED: no chunks"); return; }
        pick.SelectedIndex = best;
        Pump(1.5);
        int controls = Descendants(panel).Count();
        Console.WriteLine("  widest chunk#" + best + ": sections=" + panel.Children.Count
            + ", controls=" + controls + ", panel " + panel.Bounds.Height.ToString("0") + " px");
        Console.WriteLine("  players in the save=" + ScanOf(w).Data!.Players.Count);
        // P1-4: players moved from the chunk cards to a separate tab — check it
        var tabs = w.FindNamed<TabControl>("DataTabs")!;
        int playersIdx = -1;
        for (int i = 0; i < tabs.ItemCount; i++)
            if ((tabs.Items[i] as TabItem)?.Header as string == "Players") playersIdx = i;
        if (playersIdx < 0) Console.WriteLine("  FAILED: no Players tab");
        else
        {
            tabs.SelectedIndex = playersIdx;
            Pump(1.5);
            var playerList = w.FindNamed<ListBox>("PlayerList")!;
            var playerDetail = w.FindNamed<StackPanel>("PlayerDetailPanel")!;
            Console.WriteLine("  Players tab: rows in the list=" + playerList.ItemCount
                + ", controls in the detail=" + Descendants(playerDetail).Count()
                + ", counter='" + Cut(TextOf(w, "PlayerCountLabel")) + "'");
            if (playerList.ItemCount > 0) playerList.SelectedIndex = 0;
            Pump(1.2);
            var detailText = string.Join(" | ", Descendants(playerDetail).OfType<TextBlock>().Take(4).Select(t => t.Text));
            Console.WriteLine("  player detail: " + Cut(detailText));
        }

        var exp = Descendants(panel).OfType<Expander>().FirstOrDefault(e => ((e.Header as string) ?? "").StartsWith("show fields"));
        if (exp is null) { Console.WriteLine("  lazy field expansion: nothing to check on this chunk"); }
        else
        {
            int before = Descendants(panel).Count();
            bool lazy = exp.Content is null;
            exp.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Pump(1.0);
            int after = Descendants(panel).Count();
            Console.WriteLine("  field expansion: '" + exp.Header + "' — content empty before the click=" + lazy
                + ", controls " + before + " → " + after + " (+" + (after - before) + ")");
        }

        // paged output: a chunk with >12 BlockEntity must show only 12 cards and a button
        int many = -1;
        for (int i = 0; i < pick.ItemCount && i < 120 && many < 0; i++)
        {
            var m = System.Text.RegularExpressions.Regex.Match(pick.Items[i] as string ?? "", @"be (\d+)");
            if (m.Success && int.Parse(m.Groups[1].Value) > 12) many = i;
        }
        if (many < 0) Console.WriteLine("  paged output: no chunk with >12 BlockEntity in the save — nothing to check");
        else
        {
            pick.SelectedIndex = many;
            Pump(1.5);
            var more = Descendants(panel).OfType<Button>().FirstOrDefault(b => ((b.Content as string) ?? "").Contains("more cards"));
            int beforePag = Descendants(panel).Count();
            int cardsShown = Descendants(panel).OfType<Border>().Count();
            more?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Pump(1.0);
            int afterPag = Descendants(panel).Count();
            Console.WriteLine("  paged output (chunk#" + many + ", " + pick.Items[many] + "): button='" + (more?.Content as string ?? "none")
                + "', card frames=" + cardsShown + ", controls " + beforePag + " → " + afterPag
                + ", button disappeared=" + (more is null || !panel.Children.Any(c => Descendants(c).OfType<Button>().Contains(more))));
        }
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>P1-1: the Undo/Redo strip in the drawer header, the history flyout and "hex + field as one undo step".</summary>
    private static void Pending(string bigSave)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(bigSave);
        var token = System.IO.Path.GetFileNameWithoutExtension(bigSave);
        var loaded = WaitLoaded(w, token, "Scan complete", 600);
        Console.WriteLine("[pending] load=" + loaded + " app='" + Cut(TextOf(w, "StatusText")) + "'");
        if (!loaded) { CloseNow(w); Pump(0.5); Console.WriteLine("DONE"); return; }
        EnsureDataPanelOpen(w);

        var undo = w.FindNamed<Button>("UndoBtn")!;
        var redo = w.FindNamed<Button>("RedoBtn")!;
        var review = w.FindNamed<Button>("ReviewBtn")!;
        var badge = w.FindNamed<TextBlock>("PendingBadgeText")!;
        string Strip() => "undo=" + undo.IsEnabled + " redo=" + redo.IsEnabled + " review=" + review.IsEnabled + " badge=" + badge.Text;
        Console.WriteLine("[pending] start: " + Strip());

        long pos = PickEditableCard(w);
        if (pos < 0) { Console.WriteLine("FAILED: no card to edit"); CloseNow(w); Pump(0.5); Console.WriteLine("DONE"); return; }
        Console.WriteLine("  editable position=" + pos);

        ClickButtonIn(w, "Apply");
        Pump(1.5);
        Console.WriteLine("  1) after Apply: payload=" + EditsOf(w).PayloadEditCount + " hex=" + EditsOf(w).WholeChunkEditCount + " | " + Strip());
        Console.WriteLine("     Undo tooltip: '" + Cut(TooltipOf(undo)) + "'");

        undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Pump(1.0);
        Console.WriteLine("  2) after Undo: payload=" + EditsOf(w).PayloadEditCount + " | " + Strip()
            + " | status='" + Cut(TextOf(w, "ChunkStatusText")) + "'");
        Console.WriteLine("     Redo tooltip: '" + Cut(TooltipOf(redo)) + "'");

        w.FindNamed<Control>("Map")!.Focus();
        w.KeyPress(Key.Y, RawInputModifiers.Control);
        Pump(1.0);
        Console.WriteLine("  3) after Ctrl+Y: payload=" + EditsOf(w).PayloadEditCount + " | " + Strip());
        w.KeyPress(Key.Z, RawInputModifiers.Control);
        Pump(1.0);
        Console.WriteLine("  4) after Ctrl+Z: payload=" + EditsOf(w).PayloadEditCount + " | " + Strip());
        w.KeyPress(Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift);
        Pump(1.0);
        Console.WriteLine("  5) after Ctrl+Shift+Z: payload=" + EditsOf(w).PayloadEditCount + " | " + Strip());

        var hb = w.FindNamed<Button>("HistoryBtn")!;
        hb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Pump(0.8);
        var content = ((hb.Flyout as Flyout)?.Content as ScrollViewer)?.Content as StackPanel;
        var lines = content is null ? new List<string>() : Descendants(content).OfType<TextBlock>().Select(t => t.Text).ToList();
        Console.WriteLine("  6) history: rows=" + lines.Count + " -> " + Cut(string.Join(" / ", lines.Take(4))));
        Shot(w, "pending_history");

        // a hex edit drops the field edits of the same position — and that is ONE undo step
        EditsOf(w).HexTargetPosition = pos;
        SetText(w, "HexEditBox", "0a 14 1e 28");
        ClickButtonIn(w, "Apply hex");
        Pump(1.5);
        AnswerDialog(w, true);
        Pump(1.5);
        Console.WriteLine("  7) after Apply hex: hex=" + EditsOf(w).WholeChunkEditCount + " field=" + EditsOf(w).PayloadEditCount + " | " + Strip());
        undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Pump(1.0);
        Console.WriteLine("  8) one Undo: hex=" + EditsOf(w).WholeChunkEditCount + " field=" + EditsOf(w).PayloadEditCount
            + " (expected hex=0, field=1 — the field edits came back) | " + Strip());

        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>P1-2: the review dialog shows "before → after", the filters work, nothing is written without the button.</summary>
    private static void DiffPreview(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(save);
        var token = System.IO.Path.GetFileNameWithoutExtension(save);
        if (!WaitLoaded(w, token, "Scan complete", 900)) { Console.WriteLine("FAILED: load"); return; }
        EnsureDataPanelOpen(w);

        long pos = PickEditableCard(w);
        if (pos < 0) { Console.WriteLine("FAILED: no card to edit"); CloseNow(w); Pump(0.5); Console.WriteLine("DONE"); return; }
        // A real edit: try to change the first numeric field (otherwise Apply would write the same bytes).
        var numeric = Descendants(w.FindNamed<StackPanel>("ChunkDataPanel")).OfType<TextBox>()
            .FirstOrDefault(tb => int.TryParse(tb.Text, out _));
        if (numeric is not null && int.TryParse(numeric.Text, out var nv)) numeric.Text = (nv + 1).ToString();
        ClickButtonIn(w, "Apply");
        Pump(1.5);
        Console.WriteLine("[diff] pending edits: chunks=" + EditsOf(w).PayloadEditCount + " players=" + EditsOf(w).PlayerCount);

        ClickButtonIn(w, "Review & apply…");
        var end = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < end && w.OwnedWindows.Count == 0) Pump(0.5);
        var dlg = w.OwnedWindows.FirstOrDefault();
        if (dlg is null) { Console.WriteLine("FAILED: the review dialog did not open"); CloseNow(w); Pump(0.5); Console.WriteLine("DONE"); return; }
        Pump(1.0);
        var summary = string.Join(" | ", Descendants(dlg).OfType<TextBlock>().Take(3).Select(t => t.Text));
        Console.WriteLine("  title: '" + dlg.Title + "'");
        Console.WriteLine("  summary: " + Cut(summary));
        var cards = Descendants(dlg).OfType<Border>().Where(b => b.Child is StackPanel).ToList();
        var cardText = cards.Count == 0 ? "(none)" : string.Join(" / ", Descendants(cards[0]).OfType<TextBlock>().Take(6).Select(t => t.Text));
        Console.WriteLine("  cards=" + cards.Count + ", first: " + Cut(cardText));
        var countLine = Descendants(dlg).OfType<TextBlock>().FirstOrDefault(t => (t.Text ?? "").Contains("difference(s) shown"));
        Console.WriteLine("  counter: '" + Cut(countLine?.Text) + "'");
        Shot(w, "diff_review");            // the owner window (the dialog does not get into the frame — it is a separate top-level)
        Shot(dlg, "diff_review_dialog");   // the dialog itself: its tree is rendered separately

        var filter = Descendants(dlg).OfType<TextBox>().FirstOrDefault(t => (t.Watermark as string)?.StartsWith("filter:") == true);
        if (filter is not null)
        {
            filter.Text = "zzz-no-such-value";
            Pump(1.0);
            var none = Descendants(dlg).OfType<TextBlock>().Any(t => (t.Text ?? "").Contains("No change matches the filter"));
            Console.WriteLine("  filter miss: empty=" + none);
            filter.Text = "";
            Pump(0.8);
        }
        var kind = Descendants(dlg).OfType<ComboBox>().FirstOrDefault();
        if (kind is not null)
        {
            kind.SelectedIndex = 3;   // Players
            Pump(1.0);
            var none = Descendants(dlg).OfType<TextBlock>().Any(t => (t.Text ?? "").Contains("No change matches the filter"));
            Console.WriteLine("  'Players' filter while editing a chunk: empty=" + none);
            kind.SelectedIndex = 1;   // Chunk fields
            Pump(1.0);
            var fields = Descendants(dlg).OfType<Border>().Count(b => b.Child is StackPanel);
            Console.WriteLine("  'Chunk fields' filter: cards=" + fields);
        }
        var write = Descendants(dlg).OfType<Button>().FirstOrDefault(b => (b.Content as string)?.StartsWith("Apply to working copy") == true);
        Console.WriteLine("  apply button present=" + (write is not null) + " enabled=" + (write?.IsEnabled));

        // "Apply" writes to the working copy instead of creating a new copy from the original.
        byte[]? before = null;
        using (var orig = VSSaveFile.OpenReadOnly(save)) before = orig.ReadChunk(pos);
        write?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var endApply = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < endApply && !TextOf(w, "ChunkStatusText").Contains("Applied")) Pump(0.5);
        var work = SaveOf(w);
        var after = work.ReadChunk(pos);
        Console.WriteLine("  after Apply: applied=" + EditsOf(w).AppliedCount + " pending=" + (EditsOf(w).ChunkCount + EditsOf(w).PlayerCount)
            + " status='" + Cut(TextOf(w, "ChunkStatusText")) + "'");
        Console.WriteLine("  working copy='" + work.Path + "' source='" + work.SourcePath + "'");
        Console.WriteLine("  blob changed in the working copy=" + (before is not null && after is not null && !before.SequenceEqual(after)));
        using (var orig = VSSaveFile.OpenReadOnly(save))
            Console.WriteLine("  original unchanged=" + (before is not null && orig.ReadChunk(pos)!.SequenceEqual(before)));

        var hb = w.FindNamed<Button>("HistoryBtn")!;
        hb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Pump(0.8);
        var hcontent = ((hb.Flyout as Flyout)?.Content as ScrollViewer)?.Content as StackPanel;
        var hlines = hcontent is null ? new List<string>() : Descendants(hcontent).OfType<TextBlock>().Select(t => t.Text).ToList();
        Console.WriteLine("  history after applying: rows=" + hlines.Count
            + " applied-heading=" + hlines.Any(l => (l ?? "").Contains("Applied to the working copy"))
            + " -> " + Cut(string.Join(" / ", hlines.Take(4))));

        // Save a copy writes a separate file NEXT TO the open save (not in /tmp) and does not touch the original.
        var dest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(save))!,
            "vssaveeditor-probe (edited).vcdbs");
        try
        {
            var savedPath = work.SaveCopyTo(dest);
            Console.WriteLine("  Save a copy: '" + savedPath + "' exists=" + System.IO.File.Exists(savedPath)
                + " wal=" + System.IO.File.Exists(savedPath + "-wal"));
            using (var sc = VSSaveFile.OpenReadOnly(savedPath))
                Console.WriteLine("  in the saved copy the blob is applied="
                                  + (after is not null && sc.ReadChunk(pos)!.SequenceEqual(after)));
            using (var orig = VSSaveFile.OpenReadOnly(save))
                Console.WriteLine("  original after Save a copy is unchanged="
                                  + (before is not null && orig.ReadChunk(pos)!.SequenceEqual(before)));
        }
        finally
        {
            try { System.IO.File.Delete(dest); } catch { }
            try { System.IO.File.Delete(dest + "-wal"); } catch { }
            try { System.IO.File.Delete(dest + "-shm"); } catch { }
        }

        ClickButtonIn(dlg, "Close");
        Pump(0.8);
        Console.WriteLine("  after Close: pending=" + EditsOf(w).HasPending + ", applied=" + EditsOf(w).AppliedCount
            + ", dialog windows=" + w.OwnedWindows.Count);

        // Exiting with unapplied/unsaved edits must ask (the working copy is one-shot).
        w.Close();
        Pump(1.0);
        var closeDlg = w.OwnedWindows.FirstOrDefault();
        Console.WriteLine("  exit dialog: '" + (closeDlg?.Title ?? "none") + "'");
        AnswerDialog(w, false); // "No" — do not save, next it will ask about losing the edits
        Pump(1.0);
        var discardDlg = w.OwnedWindows.FirstOrDefault();
        Console.WriteLine("  loss dialog: '" + (discardDlg?.Title ?? "none") + "'");
        AnswerDialog(w, false); // "No" — do not discard, the window stays
        Pump(1.0);
        Console.WriteLine("  window still open=" + w.IsVisible + ", edits still there=" + EditsOf(w).AppliedCount);
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>P1-4: the "Players" tab — list, filter, detail, inventory editing and the edit marker.</summary>
    private static void Players(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(save);
        var token = System.IO.Path.GetFileNameWithoutExtension(save);
        var loaded = WaitLoaded(w, token, "Scan complete", 120);
        Console.WriteLine("[players] loaded=" + loaded + " app='" + Cut(TextOf(w, "StatusText")) + "' scan='" + Cut(TextOf(w, "ScanStatusText")) + "'");
        if (!loaded) { CloseNow(w); Pump(0.5); Console.WriteLine("DONE"); return; }
        EnsureDataPanelOpen(w);
        var tabs = w.FindNamed<TabControl>("DataTabs")!;
        int idx = -1;
        for (int i = 0; i < tabs.ItemCount; i++)
            if ((tabs.Items[i] as TabItem)?.Header as string == "Players") idx = i;
        if (idx < 0) { Console.WriteLine("FAILED: no Players tab"); CloseNow(w); Pump(0.5); Console.WriteLine("DONE"); return; }
        tabs.SelectedIndex = idx;
        Pump(1.5);

        var list = w.FindNamed<ListBox>("PlayerList")!;
        var detail = w.FindNamed<StackPanel>("PlayerDetailPanel")!;
        var filter = w.FindNamed<TextBox>("PlayerFilterBox")!;
        var pendingOnly = w.FindNamed<CheckBox>("PlayerPendingOnlyCheck")!;
        var count = w.FindNamed<TextBlock>("PlayerCountLabel")!;
        var layout = w.FindNamed<Grid>("PlayerLayoutGrid")!;
        var detailHost = w.FindNamed<Grid>("PlayerDetailHost")!;
        var emptyHint = w.FindNamed<TextBlock>("PlayerEmptyHint")!;
        var splitter = w.FindNamed<GridSplitter>("PlayerSplitter")!;
        string DetailText() => Cut(string.Join(" | ", Descendants(detail).OfType<TextBlock>().Take(3).Select(t => t.Text)));
        Console.WriteLine("[players] list: rows=" + list.ItemCount + ", counter='" + count.Text + "'");
        Console.WriteLine("  geometry: list=" + list.Bounds.Width.ToString("0") + "px, detail="
            + detailHost.Bounds.Width.ToString("0") + "px, splitter=" + splitter.IsVisible);
        Console.WriteLine("  list narrower than the detail at start=" + (list.Bounds.Width < detailHost.Bounds.Width));
        if (list.ItemCount == 0) { Console.WriteLine("  (no players in the save)"); CloseNow(w); Pump(0.5); Console.WriteLine("DONE"); return; }

        list.SelectedIndex = 0;
        Pump(1.2);
        Console.WriteLine("  detail: " + DetailText());
        var jump = Descendants(detail).OfType<Button>().FirstOrDefault(b => (b.Content as string) == "Show on map");
        Console.WriteLine("  Show on map: present=" + (jump is not null) + " enabled=" + (jump?.IsEnabled) + " tip='" + Cut(jump is null ? "" : TooltipOf(jump)) + "'");

        filter.Text = "zzz-no-such-player";
        Pump(1.0);
        var hintCentre = emptyHint.TranslatePoint(new Point(emptyHint.Bounds.Width / 2, emptyHint.Bounds.Height / 2), detailHost)
                         ?? new Point(0, 0);
        var hostCentre = new Point(detailHost.Bounds.Width / 2, detailHost.Bounds.Height / 2);
        Console.WriteLine("  filter miss: rows=" + list.ItemCount + ", counter='" + count.Text + "', detail: " + DetailText());
        Console.WriteLine("  empty state: hint.visible=" + emptyHint.IsVisible
            + " text='" + Cut(emptyHint.Text) + "'"
            + " hint centre=(" + hintCentre.X.ToString("0") + "," + hintCentre.Y.ToString("0") + ")"
            + " panel centre=(" + hostCentre.X.ToString("0") + "," + hostCentre.Y.ToString("0") + ")"
            + " delta=(" + (hintCentre.X - hostCentre.X).ToString("0") + "," + (hintCentre.Y - hostCentre.Y).ToString("0") + ")");
        Shot(w, "players_empty");
        filter.Text = "";
        Pump(1.0);

        // splitter: drag it 80 px to the right and check that the list widened while the detail narrowed
        var splitterAt = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), w)
                          ?? new Point(0, 0);
        double beforeList = list.Bounds.Width, beforeDetail = detailHost.Bounds.Width;
        w.MouseDown(splitterAt, MouseButton.Left, RawInputModifiers.None);
        w.MouseMove(new Point(splitterAt.X + 80, splitterAt.Y), RawInputModifiers.None);
        w.MouseUp(new Point(splitterAt.X + 80, splitterAt.Y), MouseButton.Left, RawInputModifiers.None);
        Pump(0.8);
        Console.WriteLine("  after dragging the splitter: list " + beforeList.ToString("0") + "→"
            + list.Bounds.Width.ToString("0") + "px, detail " + beforeDetail.ToString("0") + "→"
            + detailHost.Bounds.Width.ToString("0") + "px");
        Shot(w, "players_resized");

        var editBtn = Descendants(detail).OfType<Button>().FirstOrDefault(b => (b.Content as string) == "Edit inventory fields");
        if (editBtn is null) Console.WriteLine("  (the player has no parsed fields — nothing to edit)");
        else
        {
            editBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Pump(1.2);
            ClickButtonIn(detail, "Apply");
            Pump(1.5);
            var lines = (list.ItemsSource as List<string>) ?? new List<string>();
            Console.WriteLine("  after editing: pending players=" + EditsOf(w).PlayerCount
                + ", row='" + Cut(lines.Count > 0 ? lines[0] : "") + "'");
            pendingOnly.IsChecked = true;
            Pump(1.0);
            Console.WriteLine("  'edited only': rows=" + list.ItemCount);
            var undo = w.FindNamed<Button>("UndoBtn")!;
            undo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Pump(1.2);
            Console.WriteLine("  after Undo: pending players=" + EditsOf(w).PlayerCount
                + ", rows in the filter=" + list.ItemCount + ", detail: " + DetailText());
            pendingOnly.IsChecked = false;
            Pump(1.0);
        }
        Shot(w, "players_tab");
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>
    ///     Deleting chunks from the selection: checks that the operation goes to the working copy (the original does not
    ///     change), that it lands in History and that the map stops showing the deleted chunk.
    /// </summary>
    private static void DeleteChunksProbe(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(save);
        if (!WaitLoaded(w, SaveToken(save), "Scan complete", 900))
        {
            Console.WriteLine("FAILED: load");
            CloseNow(w);
            Pump(0.5);
            return;
        }
        EnsureDataPanelOpen(w);

        var originalHash = Sha256(save);
        int originalChunks;
        using (var orig = VSSaveFile.OpenReadOnly(save)) originalChunks = orig.Chunks.Count;

        var saveField = typeof(MainWindow).GetField("_save", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var editsField = typeof(MainWindow).GetField("_edits", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var working = (VSSaveFile)saveField.GetValue(w)!;
        var edits = (EditSession)editsField.GetValue(w)!;
        var map = (TerrainView)w.FindNamed<Control>("Map")!;
        Console.WriteLine("[deletechunks] working=" + working.Path);
        Console.WriteLine("[deletechunks] original=" + save + " chunks=" + originalChunks + " sha=" + originalHash[..16]);
        Console.WriteLine("[deletechunks] working chunks=" + working.Chunks.Count + " history=" + edits.AppliedCount);

        var target = map.Terrain?.PresentChunks.FirstOrDefault() ?? (0, 0);
        Console.WriteLine("[deletechunks] map has target before=" + (map.Terrain?.PresentChunks.Contains(target) ?? false));
        var select = typeof(TerrainView).GetMethod("SelectChunk", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        select.Invoke(map, new object[] { target.Item1, target.Item2 });
        Pump(0.8);
        Console.WriteLine("[deletechunks] selected=" + map.SelectionCount + " chunk=" + target + " button=" + w.FindNamed<Button>("DeleteChunksBtn")!.IsEnabled);
        Shot(w, "deletechunks_selected");

        SelectDataTab(w, "Selection");
        Pump(1.0);
        w.FindNamed<Button>("DeleteChunksBtn")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var endDialog = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < endDialog && w.OwnedWindows.Count == 0) Pump(0.5);
        var dlg = w.OwnedWindows.FirstOrDefault();
        if (dlg is null) { Console.WriteLine("FAILED: confirm dialog"); CloseNow(w); Pump(0.5); return; }
        Pump(0.5);
        var dlgText = string.Join(" | ", Descendants(dlg).OfType<TextBlock>().Select(t => t.Text));
        Console.WriteLine("[deletechunks] confirm: " + Cut(dlgText));
        Shot(dlg, "deletechunks_confirm");
        AnswerDialog(w, true);

        var end = DateTime.UtcNow.AddSeconds(900);
        string app = "";
        while (DateTime.UtcNow < end)
        {
            app = TextOf(w, "StatusText");
            if (app.Contains("Deleted ") || app.Contains("failed")) break;
            Pump(1.0);
        }
        Console.WriteLine("[deletechunks] status='" + Cut(app) + "'");
        Console.WriteLine("[deletechunks] history=" + edits.AppliedCount + " unsaved=" + edits.UnsavedAppliedCount);
        if (edits.AppliedCount > 0)
            Console.WriteLine("[deletechunks] last history: " + edits.Applied[^1].Scope + " · " + edits.Applied[^1].Label);

        var after = working.Chunks.Count;
        using (var orig = VSSaveFile.OpenReadOnly(save)) originalChunks = orig.Chunks.Count;
        Console.WriteLine("[deletechunks] working chunks after=" + after + ", original chunks=" + originalChunks
                          + ", original sha same=" + (Sha256(save) == originalHash));
        Console.WriteLine("[deletechunks] selection after=" + map.SelectionCount
                          + ", map has target after=" + (map.Terrain?.PresentChunks.Contains(target) ?? false)
                          + ", map size=" + (map.Terrain is null ? "NONE" : $"{map.Terrain.Width}x{map.Terrain.Height}"));
        Shot(w, "deletechunks_after");
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>Removing blocks from the save: selection ONLY via checkboxes in the "Find blocks" tab list (there are no ready-made
    /// categories in the UI — the search narrows the list and the user ticks), a warning in the confirmation,
    /// writing to the working copy and a factual check: the selected cells must be gone from the copy, the original does not
    /// change.</summary>

    /// <summary>
    ///     The "Find blocks" tab → "Replace selected…": choosing a replacement block from BlockIDs,
    ///     confirmation and checking that in the working copy the cells of the selected codes became the target block,
    ///     while the original did not change.
    /// </summary>
    private static void ReplaceBlocks(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(save);
        if (!WaitLoaded(w, SaveToken(save), "Scan complete", 900)) { Console.WriteLine("FAILED: load"); CloseNow(w); Pump(0.5); return; }
        EnsureDataPanelOpen(w);

        var tabs = w.FindNamed<TabControl>("DataTabs")!;
        int idx = -1;
        for (int i = 0; i < tabs.ItemCount; i++)
            if ((tabs.Items[i] as TabItem)?.Header as string == "Find blocks") idx = i;
        if (idx < 0) { Console.WriteLine("FAILED: no Find blocks tab"); CloseNow(w); Pump(0.5); return; }
        tabs.SelectedIndex = idx;
        Pump(1.5);

        var codes = (HashSet<string>)PrivField(w, "_filterCodes")!;
        var replaceBtn = w.FindNamed<Button>("ReplaceBlocksBtn")!;
        ClickButtonIn(w, "Clear");
        Pump(0.8);
        Console.WriteLine("[replaceblocks] empty selection: codes=" + codes.Count + ", ReplaceBlocksBtn.enabled=" + replaceBtn.IsEnabled + " (expected False)");

        w.FindNamed<TextBox>("FilterSearchBox")!.Text = "rock-";
        Pump(2.0);
        int rows = TickFilterRows(w, c => c.StartsWith("rock-", StringComparison.OrdinalIgnoreCase));
        var sourceCodes = codes.Where(c => c.StartsWith("rock-", StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"[replaceblocks] source rows={rows} codes={sourceCodes.Count} enabled={replaceBtn.IsEnabled}");
        if (sourceCodes.Count == 0) { Console.WriteLine("  (no rock-* — the probe is not representative)"); CloseNow(w); Pump(0.5); Console.WriteLine("DONE"); return; }

        replaceBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var end0 = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < end0 && w.OwnedWindows.Count == 0) Pump(0.5);
        var dlg = w.OwnedWindows.FirstOrDefault();
        if (dlg is null) { Console.WriteLine("  FAILED: the replace dialog did not open"); CloseNow(w); Pump(0.5); Console.WriteLine("DONE"); return; }
        Pump(0.8);
        Shot(dlg, "replaceblocks_dialog");
        var text = string.Join(" | ", Descendants(dlg).OfType<TextBlock>().Select(t => t.Text));
        Console.WriteLine($"  dialog='{dlg.Title}' text='{Cut(text)}'");

        // target: the first stone outside the selected codes; search for it in the dialog itself
        var reg = ((VSSaveFile)PrivField(w, "_save")!).BlockRegistry;
        var targetCode = reg.Codes.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(c => c.Contains("stone", StringComparison.OrdinalIgnoreCase) && !sourceCodes.Contains(c))
            .OrderBy(c => c.Length).FirstOrDefault()
            ?? reg.Codes.Distinct(StringComparer.OrdinalIgnoreCase).First(c => !sourceCodes.Contains(c));
        var targetId = reg.IdsOf(targetCode).Min();
        var dlgSearch = Descendants(dlg).OfType<TextBox>().First();
        dlgSearch.Text = targetCode;
        Pump(1.0);
        var dlgList = Descendants(dlg).OfType<ListBox>().First();
        dlgList.SelectedIndex = 0;
        Pump(0.5);
        var pickText = dlgList.SelectedItem?.ToString() ?? "?";
        Console.WriteLine($"  target='{targetCode}' id={targetId}, picked='{pickText}', rows={dlgList.ItemCount}");
        ClickButtonIn(dlg, "Replace");
        Pump(0.8);

        var end1 = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < end1 && w.OwnedWindows.Count == 0) Pump(0.5);
        var confirm = w.OwnedWindows.FirstOrDefault();
        if (confirm is not null)
        {
            var ctext = string.Join(" | ", Descendants(confirm).OfType<TextBlock>().Select(t => t.Text));
            Console.WriteLine($"  confirm='{confirm.Title}' text='{Cut(ctext)}'");
            AnswerDialog(w, true);
        }
        else Console.WriteLine("  FAILED: the confirmation did not open");

        var end2 = DateTime.UtcNow.AddSeconds(900);
        string app = "";
        while (DateTime.UtcNow < end2)
        {
            app = TextOf(w, "StatusText");
            if (app.Contains("working copy. Original unchanged") || app.Contains("nothing changed") || app.Contains("failed")) break;
            Pump(1.0);
        }
        Console.WriteLine("  result='" + Cut(app) + "'");
        Shot(w, "replaceblocks_after");

        var working = (VSSaveFile)PrivField(w, "_save")!;
        var edits = (EditSession)PrivField(w, "_edits")!;
        var sourceIds = sourceCodes.SelectMany(working.BlockRegistry.IdsOf).ToHashSet();
        long sourceLeft = 0, targetCells = 0;
        foreach (var row in working.ChunkRowsWithData)
        {
            var solids = ChunkBlockDecoder.DecodeChunkBlocks(row.Data);
            foreach (var b in solids) { if (sourceIds.Contains(b)) sourceLeft++; if (b == targetId) targetCells++; }
            var liquids = ChunkBlockDecoder.DecodeChunkLiquids(row.Data);
            if (liquids is not null) foreach (var b in liquids) { if (sourceIds.Contains(b)) sourceLeft++; if (b == targetId) targetCells++; }
        }
        long sourceOrig = 0, targetOrig = 0;
        using (var orig = VSSaveFile.OpenReadOnly(save))
        {
            foreach (var row in orig.ChunkRowsWithData)
            {
                foreach (var b in ChunkBlockDecoder.DecodeChunkBlocks(row.Data)) { if (sourceIds.Contains(b)) sourceOrig++; if (b == targetId) targetOrig++; }
                var liquids = ChunkBlockDecoder.DecodeChunkLiquids(row.Data);
                if (liquids is not null) foreach (var b in liquids) { if (sourceIds.Contains(b)) sourceOrig++; if (b == targetId) targetOrig++; }
            }
        }
        var ok = sourceOrig > 0 && sourceLeft == 0 && targetCells >= targetOrig + sourceOrig
                 && edits.AppliedCount > 0;
        Console.WriteLine($"  cells: source {sourceOrig} -> {sourceLeft}, target {targetOrig} -> {targetCells}; history={edits.AppliedCount}");
        Console.WriteLine(ok ? "RESULT: OK — block replacement reached the working copy, the original is untouched"
            : "RESULT: FAIL — block replacement not confirmed");
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    private static void RemoveBlocks(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(save);
        if (!WaitLoaded(w, SaveToken(save), "Scan complete", 900)) { Console.WriteLine("FAILED: load"); CloseNow(w); Pump(0.5); return; }
        EnsureDataPanelOpen(w);

        var tabs = w.FindNamed<TabControl>("DataTabs")!;
        int idx = -1;
        for (int i = 0; i < tabs.ItemCount; i++)
            if ((tabs.Items[i] as TabItem)?.Header as string == "Find blocks") idx = i;
        if (idx < 0) { Console.WriteLine("FAILED: no Find blocks tab"); CloseNow(w); Pump(0.5); return; }
        tabs.SelectedIndex = idx;
        Pump(1.5);

        var codes = (HashSet<string>)PrivField(w, "_filterCodes")!;
        var removeBtn = w.FindNamed<Button>("RemoveBlocksBtn")!;
        ClickButtonIn(w, "Clear");
        Pump(0.8);
        Console.WriteLine("[removeblocks] empty selection: codes=" + codes.Count + ", RemoveBlocksBtn.enabled=" + removeBtn.IsEnabled + " (expected False)");

        Console.WriteLine("[removeblocks] 1) warning about vanilla codes (ticking rock-* from the list)");
        w.FindNamed<TextBox>("FilterSearchBox")!.Text = "rock-";
        Pump(2.0);
        int rockRows = TickFilterRows(w, c => c.StartsWith("rock-", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine("  rows ticked=" + rockRows + ", codes selected=" + codes.Count + ", enabled=" + removeBtn.IsEnabled + " (expected True)");
        removeBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var end0 = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < end0 && w.OwnedWindows.Count == 0) Pump(0.5);
        var dlgRocks = w.OwnedWindows.FirstOrDefault();
        string rocksText = "";
        if (dlgRocks is not null)
        {
            Pump(0.8);
            rocksText = string.Join(" | ", Descendants(dlgRocks).OfType<TextBlock>().Select(t => t.Text));
            Console.WriteLine("  title='" + dlgRocks.Title + "', vanilla warning=" + rocksText.Contains("VANILLA"));
            Console.WriteLine("  text: " + Cut(rocksText));
            AnswerDialog(w, false);
            Pump(1.0);
        }
        else Console.WriteLine("  FAILED: the dialog (Rocks) did not open");
        Console.WriteLine("  after No: dialogs=" + w.OwnedWindows.Count + ", pending in the model=" + EditsOf(w).HasPending);

        Console.WriteLine("[removeblocks] 2) modded codes (search ':' + checkboxes) and writing to the copy");
        ClickButtonIn(w, "Clear");
        Pump(0.5);
        w.FindNamed<TextBox>("FilterSearchBox")!.Text = ":";
        Pump(2.0);
        int modRows = TickFilterRows(w, c => c.Contains(':'));
        var modCodes = codes.Where(c => c.Contains(':')).ToList();
        Console.WriteLine("  rows ticked=" + modRows + ", codes selected=" + codes.Count + " (all modded=" + (codes.Count == modCodes.Count) + "), enabled=" + removeBtn.IsEnabled + " (expected True)");
        Console.WriteLine("  examples: " + Cut(string.Join(", ", modCodes.Take(4))));
        if (modCodes.Count == 0) { Console.WriteLine("  (no modded blocks in the save — the probe is not representative)"); CloseNow(w); Pump(0.5); Console.WriteLine("DONE"); return; }

        removeBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var end1 = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < end1 && w.OwnedWindows.Count == 0) Pump(0.5);
        var dlg = w.OwnedWindows.FirstOrDefault();
        if (dlg is null) { Console.WriteLine("  FAILED: the dialog (modded codes) did not open"); CloseNow(w); Pump(0.5); Console.WriteLine("DONE"); return; }
        Pump(0.8);
        Shot(dlg, "removeblocks_dialog");
        var text = string.Join(" | ", Descendants(dlg).OfType<TextBlock>().Select(t => t.Text));
        Console.WriteLine("  title='" + dlg.Title + "'");
        Console.WriteLine("  text: " + Cut(text));
        AnswerDialog(w, true);

        var end2 = DateTime.UtcNow.AddSeconds(900);
        string app = "";
        while (DateTime.UtcNow < end2)
        {
            app = TextOf(w, "StatusText");
            if (app.Contains("working copy. Original unchanged") || app.Contains("nothing changed") || app.Contains("failed")) break;
            Pump(1.0);
        }
        Console.WriteLine("  result='" + Cut(app) + "'");
        Shot(w, "removeblocks_after");

        var saveField = typeof(MainWindow).GetField("_save", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var editsField = typeof(MainWindow).GetField("_edits", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var working = (VSSaveFile)saveField.GetValue(w)!;
        var edits = (EditSession)editsField.GetValue(w)!;
        Console.WriteLine("  working copy='" + working.Path + "' (original '" + save + "')");
        Console.WriteLine("  history=" + edits.AppliedCount + " unsaved=" + edits.UnsavedAppliedCount
                          + (edits.AppliedCount > 0 ? " last=" + edits.Applied[^1].Scope + " · " + edits.Applied[^1].Label : ""));

        var modIds = modCodes.SelectMany(working.BlockRegistry.IdsOf).ToHashSet();
        long left = 0, total = 0;
        foreach (var row in working.ChunkRowsWithData)
        {
            var solids = ChunkBlockDecoder.DecodeChunkBlocks(row.Data);
            var liquids = ChunkBlockDecoder.DecodeChunkLiquids(row.Data);
            foreach (var b in solids) { if (b != 0) total++; if (modIds.Contains(b)) left++; }
            if (liquids is not null) foreach (var b in liquids) { if (b != 0) total++; if (modIds.Contains(b)) left++; }
        }
        Console.WriteLine("  in the working copy: modded cells=" + left + " (left), total non-empty=" + total);

        long leftOrig = 0;
        using (var orig = VSSaveFile.OpenReadOnly(save))
        {
            foreach (var row in orig.ChunkRowsWithData)
            {
                foreach (var b in ChunkBlockDecoder.DecodeChunkBlocks(row.Data)) if (modIds.Contains(b)) leftOrig++;
                var l = ChunkBlockDecoder.DecodeChunkLiquids(row.Data);
                if (l is not null) foreach (var b in l) if (modIds.Contains(b)) leftOrig++;
            }
        }
        Console.WriteLine("  in the original modded cells=" + leftOrig + " (the original was not changed)");
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>The "clean a mod domain out of the world" scenario: the search in the "Find blocks" tab narrows the list
    /// to a single domain, ALL found rows are ticked, "Remove selected (on copy)…" → Yes.
    /// The copy is written to TMPDIR (set it inside the working tree, otherwise it cannot be compared after the run).
    /// This also checks what the user sees: how many rows were found, how many codes and cells
    /// were selected, how many cells are left in the copy. The full comparison with the original is separate (celldiff).</summary>
    private static void NdlRemove(string save, string needle)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(save);
        if (!WaitLoaded(w, SaveToken(save), "Scan complete", 1800))
        {
            Console.WriteLine("FAILED: load"); CloseNow(w); Pump(0.5); return;
        }
        EnsureDataPanelOpen(w);
        SelectDataTab(w, "Find blocks");

        var codes = (HashSet<string>)PrivField(w, "_filterCodes")!;
        var removeBtn = w.FindNamed<Button>("RemoveBlocksBtn")!;
        ClickButtonIn(w, "Clear");
        Pump(0.8);

        w.FindNamed<TextBox>("FilterSearchBox")!.Text = needle;
        Pump(3.0);
        var header = string.Join(" | ", Descendants(w.FindNamed<StackPanel>("FilterListPanel"))
            .OfType<TextBlock>().Select(t => t.Text).Take(2));
        Console.WriteLine("[ndlremove] search '" + needle + "': " + Cut(header));

        int rows = TickFilterRows(w, c => c.Contains(needle, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine("[ndlremove] rows ticked=" + rows + ", codes selected=" + codes.Count
            + ", enabled=" + removeBtn.IsEnabled + " (expected True)");

        var scan = ScanOf(w).Data!;
        long expectedCells = 0;
        int noDomain = 0;
        foreach (var code in codes)
        {
            scan.WorldBlockCodeCounts.TryGetValue(code, out var cnt);
            expectedCells += cnt;
            if (!code.Contains(':')) noDomain++;
        }
        var ids = new SortedSet<int>();
        int unknown;
        using (var src = VSSaveFile.OpenReadOnly(save))
        {
            var reg = src.BlockRegistry;
            unknown = 0;
            foreach (var code in codes)
            {
                var got = reg.IdsOf(code);
                if (got.Count == 0) unknown++;
                foreach (var id in got) ids.Add(id);
            }
        }
        Console.WriteLine("[ndlremove] expectation: codes=" + codes.Count + ", id=" + ids.Count
            + ", cells in the world=" + expectedCells.ToString("N0") + ", without a domain=" + noDomain
            + ", no id in the registry=" + unknown);

        removeBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var end0 = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < end0 && w.OwnedWindows.Count == 0) Pump(0.5);
        var dlg = w.OwnedWindows.FirstOrDefault();
        if (dlg is null) { Console.WriteLine("FAILED: the dialog did not open"); CloseNow(w); Pump(0.5); return; }
        Pump(0.8);
        Shot(dlg, "ndlremove_dialog");
        var text = string.Join(" | ", Descendants(dlg).OfType<TextBlock>().Select(t => t.Text));
        Console.WriteLine("[ndlremove] title='" + dlg.Title + "'");
        Console.WriteLine("[ndlremove] text: " + Cut(text));
        AnswerDialog(w, true);

        var end2 = DateTime.UtcNow.AddSeconds(1800);
        var app = "";
        while (DateTime.UtcNow < end2)
        {
            app = TextOf(w, "StatusText");
            if (app.Contains("working copy. Original unchanged") || app.Contains("nothing changed") || app.Contains("failed")) break;
            Pump(1.0);
        }
        Console.WriteLine("[ndlremove] result='" + Cut(app) + "'");
        Shot(w, "ndlremove_after");

        var saveField = typeof(MainWindow).GetField("_save", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var editsField = typeof(MainWindow).GetField("_edits", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var working = (VSSaveFile)saveField.GetValue(w)!;
        var edits = (EditSession)editsField.GetValue(w)!;
        Console.WriteLine("[ndlremove] working copy='" + working.Path + "'");
        Console.WriteLine("[ndlremove] history=" + edits.AppliedCount + " unsaved=" + edits.UnsavedAppliedCount
                          + (edits.AppliedCount > 0 ? " last=" + edits.Applied[^1].Scope + " · " + edits.Applied[^1].Label : ""));

        var modIds = ids.ToHashSet();
        long left = 0, total = 0;
        foreach (var row in working.ChunkRowsWithData)
        {
            var solids = ChunkBlockDecoder.DecodeChunkBlocks(row.Data);
            var liquids = ChunkBlockDecoder.DecodeChunkLiquids(row.Data);
            foreach (var b in solids) { if (b != 0) total++; if (modIds.Contains(b)) left++; }
            if (liquids is not null) foreach (var b in liquids) { if (b != 0) total++; if (modIds.Contains(b)) left++; }
        }
        Console.WriteLine("[ndlremove] in the working copy: cells of the selected ids=" + left + " (expected 0), total non-empty=" + total.ToString("N0"));

        long leftOrig = 0, totalOrig = 0;
        using (var orig = VSSaveFile.OpenReadOnly(save))
        {
            foreach (var row in orig.ChunkRowsWithData)
            {
                foreach (var b in ChunkBlockDecoder.DecodeChunkBlocks(row.Data))
                { if (b != 0) totalOrig++; if (modIds.Contains(b)) leftOrig++; }
                var l = ChunkBlockDecoder.DecodeChunkLiquids(row.Data);
                if (l is not null) foreach (var b in l) { if (b != 0) totalOrig++; if (modIds.Contains(b)) leftOrig++; }
            }
        }
        Console.WriteLine("[ndlremove] in the original: cells of the selected ids=" + leftOrig.ToString("N0")
            + ", total non-empty=" + totalOrig.ToString("N0"));
        Console.WriteLine("[ndlremove] balance: non-empty dropped by " + (totalOrig - total).ToString("N0")
            + " (expected " + leftOrig.ToString("N0") + ")");
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>P1-5: MRU — the recent list, the File → Recent submenu, Ctrl+1 and the start panel.
    /// We write the starting settings.json ourselves to check the "(missing)" branch and, at the same time, not read
    /// the user's settings file (run the probe with XDG_CONFIG_HOME inside the repository).</summary>
    private static void Recent(string save, string smallSave)
    {
        var settingsPath = AppSettings.SettingsPath;
        System.IO.File.WriteAllText(settingsPath, "{\"RecentFiles\":[\"/no/such/save.vcdbs\"]}");
        Console.WriteLine("[recent] settings: " + settingsPath);

        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        var panel = w.FindNamed<Border>("RecentPanel")!;
        var startList = w.FindNamed<ListBox>("RecentStartList")!;
        var menu0 = w.FindNamed<MenuItem>("RecentMenu")!;
        Console.WriteLine("  start with a missing file: panel visible=" + panel.IsVisible
            + ", rows=" + startList.ItemCount
            + ", text='" + Cut((startList.ItemsSource as List<string>)?.FirstOrDefault() ?? "") + "'"
            + ", menu item enabled=" + (menu0.Items[0] as MenuItem)?.IsEnabled);

        ResetStatus(w);
        w.OpenStartupFile(save);
        var token = System.IO.Path.GetFileNameWithoutExtension(save);
        var loaded = WaitLoaded(w, token, "Scan complete", 300);
        Console.WriteLine("  load=" + loaded + " app='" + Cut(TextOf(w, "StatusText")) + "'");
        if (!loaded) { CloseNow(w); Pump(0.5); Console.WriteLine("DONE"); return; }
        var recents = RecentsOf(w);
        Console.WriteLine("  after opening: recents=" + recents.Count + ", first='" + System.IO.Path.GetFileName(recents.FirstOrDefault() ?? "") + "'");
        Console.WriteLine("  start panel visible with the map open=" + panel.IsVisible);
        var menu = w.FindNamed<MenuItem>("RecentMenu")!;
        var headers = menu.Items.OfType<MenuItem>().Select(m => (m.Header as string ?? "") + (m.IsEnabled ? "" : " [disabled]")).ToList();
        Console.WriteLine("  Recent menu: " + Cut(string.Join(" | ", headers)));

        w.FindNamed<Control>("Map")!.Focus();
        w.KeyPress(Key.D1, RawInputModifiers.Control);
        Pump(4.0);
        Console.WriteLine("  Ctrl+1: status='" + Cut(TextOf(w, "StatusText")) + "'");
        CloseNow(w);
        Pump(1.0);

        // the second instance reads the same settings.json: the MRU survives a restart
        var w2 = new MainWindow { Width = 1280, Height = 800 };
        w2.Show();
        Pump(1.5);
        var panel2 = w2.FindNamed<Border>("RecentPanel")!;
        var start2 = w2.FindNamed<ListBox>("RecentStartList")!;
        var rows = (start2.ItemsSource as List<string>) ?? new List<string>();
        Console.WriteLine("  new instance without a save: panel visible=" + panel2.IsVisible + ", rows=" + rows.Count);
        foreach (var r in rows.Take(4)) Console.WriteLine("    '" + Cut(r) + "'");

        var menu2 = w2.FindNamed<MenuItem>("RecentMenu")!;
        var clear = menu2.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == "Clear list");
        clear?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump(1.0);
        Console.WriteLine("  after Clear list: items=" + menu2.Items.Count
            + ", first='" + ((menu2.Items[0] as MenuItem)?.Header as string ?? "") + "'"
            + ", panel visible=" + panel2.IsVisible
            + ", recents=" + RecentsOf(w2).Count);
        CloseNow(w2);
        Pump(1.0);
        Console.WriteLine("DONE");
    }

    /// <summary>Picks a chunk with an editable card and enters edit mode (Edit fields).
    /// Returns the column position or -1. Chunks are tried in descending "has something to edit":
    /// the list shows "entities N, be M", so empty chunks are not opened in vain.</summary>
    private static long PickEditableCard(Window w, int maxScan = 200)
    {
        var pick = w.FindNamed<ComboBox>("ChunkPick")!;
        var panel = w.FindNamed<StackPanel>("ChunkDataPanel")!;
        var candidates = new List<int>();
        for (int i = 0; i < pick.ItemCount; i++)
        {
            var m = System.Text.RegularExpressions.Regex.Match(pick.Items[i] as string ?? "", @"entities (\d+), be (\d+)");
            if (m.Success && (int.Parse(m.Groups[1].Value) > 0 || int.Parse(m.Groups[2].Value) > 0)) candidates.Add(i);
        }
        Console.WriteLine("  chunks with entity/be in the list: " + candidates.Count);
        foreach (var i in candidates.Take(maxScan))
        {
            pick.SelectedIndex = i;
            Pump(0.8);
            var section = panel.Children.OfType<StackPanel>().FirstOrDefault(s => Descendants(s).OfType<Button>().Any(b => (b.Content as string) == "Edit fields"));
            if (section is null) continue;
            Descendants(section).OfType<Button>().First(b => (b.Content as string) == "Edit fields").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Pump(1.0);
            var pos = EditingPos(w) ?? -1;
            if (pos >= 0) return pos;
        }
        return -1;
    }

    private static string TooltipOf(Control c) => ToolTip.GetTip(c) as string ?? "";

    private static List<string> RecentsOf(Window w)
    {
        var s = PrivField(w, "_settings") as AppSettings;
        return s is null ? new List<string>() : s.RecentFiles.ToList();
    }

    private static T? AncestorOf<T>(Visual v) where T : Visual
    {
        for (var p = v.GetVisualParent(); p is not null; p = p.GetVisualParent()) if (p is T t) return t;
        return null;
    }

    /// <summary>P1-3: editing a card repaints only its own section — the other sections (and the field lists
    /// expanded inside them) are not recreated, the scroll position is preserved.</summary>
    private static void PointUpdate(string bigSave)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(bigSave);
        if (!WaitLoaded(w, SaveToken(bigSave), "Scan complete", 900)) { Console.WriteLine("FAILED: load"); return; }
        EnsureDataPanelOpen(w);

        var panel = w.FindNamed<StackPanel>("ChunkDataPanel")!;
        var pick = w.FindNamed<ComboBox>("ChunkPick")!;
        bool HasEdit(Visual s) => Descendants(s).OfType<Button>().Any(b => (b.Content as string) == "Edit fields");

        // chunks that have anything to edit first: the list shows "entities N, be M",
        // otherwise on a save with rare entities scanning the first 60 chunks finds nothing
        var withCards = new List<int>();
        for (int i = 0; i < pick.ItemCount; i++)
        {
            var m = System.Text.RegularExpressions.Regex.Match(pick.Items[i] as string ?? "", @"entities (\d+), be (\d+)");
            if (m.Success && (int.Parse(m.Groups[1].Value) > 0 || int.Parse(m.Groups[2].Value) > 0)) withCards.Add(i);
        }
        int chosen = -1;
        foreach (var i in withCards)
        {
            pick.SelectedIndex = i;
            Pump(1.0);
            var secs = panel.Children.OfType<StackPanel>().ToList();
            if (secs.Count >= 3 && secs.Any(HasEdit)) { chosen = i; break; }
        }
        if (chosen < 0) { Console.WriteLine("FAILED: no chunk with several columns and an editable card"); return; }
        Console.WriteLine("  chunk#" + chosen + ": sections=" + panel.Children.Count + ", panel " + panel.Bounds.Height.ToString("0") + " px");

        var scroll = AncestorOf<ScrollViewer>(panel);
        Console.WriteLine("  scroll: " + (scroll is null ? "not found" : "present, extent=" + scroll.Extent.Height.ToString("0")));
        if (scroll is null) { Console.WriteLine("FAILED: no ScrollViewer around the panel"); return; }
        scroll.Offset = new Vector(0, 120);
        Pump(0.6);
        var offBefore = scroll.Offset;

        var before = panel.Children.OfType<StackPanel>().ToList();
        var witness = before.First(s => !HasEdit(s));
        var editSection = before.First(HasEdit);

        void Step(string what)
        {
            Console.WriteLine("  " + what + ": sections same=" + (panel.Children.Contains(witness) && panel.Children.Contains(editSection))
                + ", witness in place=" + panel.Children.Contains(witness)
                + ", sections=" + panel.Children.Count
                + ", offset=" + scroll.Offset.Y.ToString("0") + " (was " + offBefore.Y.ToString("0") + ")"
                + ", status='" + Cut(TextOf(w, "ChunkStatusText")) + "'");
        }

        var editBtn = Descendants(editSection).OfType<Button>().First(b => (b.Content as string) == "Edit fields");
        editBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Pump(1.2);
        Step("after Edit fields");
        Console.WriteLine("  in edit mode: Apply=" + Descendants(editSection).OfType<Button>().Any(b => (b.Content as string) == "Apply"));

        var apply = Descendants(editSection).OfType<Button>().FirstOrDefault(b => (b.Content as string) == "Apply");
        if (apply is null) { Console.WriteLine("FAILED: no Apply button in the card"); return; }
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Pump(1.5);
        Step("after Apply");
        Console.WriteLine("  the edit returned to view mode: Edit fields=" + HasEdit(editSection)
            + ", pending=" + EditsOf(w).PayloadEditCount
            + ", scroll preserved=" + (Math.Abs(scroll.Offset.Y - offBefore.Y) < 0.5));

        // second run: Cancel must not rebuild the panel either
        var editBtn2 = Descendants(editSection).OfType<Button>().FirstOrDefault(b => (b.Content as string) == "Edit fields");
        if (editBtn2 is not null)
        {
            editBtn2.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Pump(1.0);
            var cancel = Descendants(editSection).OfType<Button>().FirstOrDefault(b => (b.Content as string) == "Cancel");
            cancel?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Pump(1.2);
            Step("after Cancel");
        }

        // a full rebuild (chunk change) must return the panel to the start, not carry the old scroll
        pick.SelectedIndex = chosen == 0 ? 1 : 0;
        Pump(1.5);
        Console.WriteLine("  after changing the chunk: offset=" + scroll.Offset.Y.ToString("0") + " (expected 0)");
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>Check: there is no Cyrillic in the UI; the map tooltip is off, but the hover info and its option still work.</summary>
    private static void I18n(string bigSave)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(bigSave);
        if (!WaitLoaded(w, SaveToken(bigSave), "Scan complete", 900)) { Console.WriteLine("FAILED: load"); return; }
        Pump(2.0);

        var map = w.FindNamed<Control>("Map")!;
        Console.WriteLine("[i18n] ShowHoverTooltip by default = " + PrivField(map, "_showHoverTooltip"));

        // move the mouse to the centre of the map: the info line must update even if the tooltip is off
        var local = new Point(map.Bounds.Width / 2, map.Bounds.Height / 2);
        var win = map.TranslatePoint(local, w) ?? local;
        w.MouseMove(win, RawInputModifiers.None);
        Pump(1.0);
        Console.WriteLine("[i18n] InfoText after hover: '" + Cut(TextOf(w, "InfoText")) + "'");
        Shot(w, "i18n_01_tooltip_off");

        // enable the tooltip in settings — it is drawn again
        var cb = w.FindNamed<CheckBox>("HoverTooltipCheck")!;
        cb.IsChecked = true; Pump(1.0);
        Console.WriteLine("[i18n] after ticking the checkbox: _showHoverTooltip=" + PrivField(map, "_showHoverTooltip"));
        w.MouseMove(win, RawInputModifiers.None); Pump(0.3);
        Shot(w, "i18n_02_tooltip_on");
        cb.IsChecked = false; Pump(0.5);

        // scan all visible strings for Cyrillic
        var bad = new List<string>();
        void Check(string kind, object? ctrl, string? text)
        {
            if (HasCyr(text)) bad.Add(kind + ": " + text);
        }
        foreach (var c in Descendants(w))
        {
            switch (c)
            {
                case TextBlock tb: Check("TextBlock", c, tb.Text); break;
                case CheckBox chk: Check("CheckBox", c, chk.Content as string); break;
                case RadioButton rb: Check("Radio", c, rb.Content as string); break;
                case Button b: Check("Button", c, b.Content as string); break;
                case MenuItem mi: Check("MenuItem", c, mi.Header as string); break;
                case TabItem ti: Check("Tab", c, ti.Header as string); break;
                case TextBox tbx:
                    Check("Watermark", c, tbx.Watermark);
                    break;
                case ComboBox cmb: Check("Placeholder", c, cmb.PlaceholderText); break;
            }
            var tip = c is Control ctl ? ToolTip.GetTip(ctl) as string : null;
            Check("ToolTip", c, tip);
        }
        Check("WindowTitle", null, w.Title);
        Console.WriteLine("[i18n] Cyrillic UI strings found: " + bad.Count);
        foreach (var b in bad.Take(20)) Console.WriteLine("        " + b);
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    /// <summary>P1-7: one status line instead of eight (priority + lifetime), a hex box
    /// of fixed width with horizontal scrolling, a label for the progress bar.</summary>
    private static void StatusLine(string bigSave)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);

        // 1) no old status lines are left in the markup
        string[] gone = { "StatusText", "InfoText", "ScanStatusText", "ChunkStatusText", "SearchStatusText", "FilterStatusText", "SelInfoText" };
        var left = gone.Where(n => w.FindNamed<TextBlock>(n) is not null).ToList();
        var line = w.FindNamed<TextBlock>("AppStatusText");
        Console.WriteLine("[status] old status TextBlocks left: " + left.Count + (left.Count > 0 ? " (" + string.Join(",", left) + ")" : "")
            + "; the only line=" + Fmt(line));

        ResetStatus(w);
        Console.WriteLine("[status] after reset the line: '" + StatusLineOf(w) + "' (expected empty)");

        // 2) loading a save: one line shows the stage, the bar runs with a label
        var map = w.FindNamed<Control>("Map")!;
        var hexCenter = new Point(map.Bounds.Width / 2, map.Bounds.Height / 2);
        ResetStatus(w);
        w.OpenStartupFile(bigSave);
        bool sawLabel = false, sawBar = false, scanSeen = false;
        var deadline = DateTime.UtcNow.AddSeconds(900);
        while (DateTime.UtcNow < deadline && !(scanSeen && TextOf(w, "StatusText").StartsWith("opened:")))
        {
            var pl = w.FindNamed<TextBlock>("ProgressLabel");
            var pb = w.FindNamed<ProgressBar>("LoadProgress");
            if (pl is { IsVisible: true } && !string.IsNullOrEmpty(pl.Text)) sawLabel = true;
            if (pb is { IsVisible: true }) sawBar = true;
            scanSeen |= TextOf(w, "ScanStatusText").StartsWith("Scan complete");
            Pump(0.5);
        }
        Console.WriteLine("[status] during loading: progress label visible=" + sawLabel + ", bar visible=" + sawBar
            + "; after loading the label is visible=" + (w.FindNamed<TextBlock>("ProgressLabel")?.IsVisible)
            + " line='" + Cut(StatusLineOf(w)) + "'"
            + " (app='" + Cut(TextOf(w, "StatusText")) + "', scan='" + Cut(TextOf(w, "ScanStatusText")) + "')");

        // 3) the live line under the cursor fills the single line when there are no messages above
        ResetStatus(w);
        for (int i = 0; i < 5 && string.IsNullOrEmpty(TextOf(w, "InfoText")); i++)
        {
            w.MouseMove(map.TranslatePoint(hexCenter, w) ?? new Point(10, 10), RawInputModifiers.None);
            Pump(0.5);
        }
        var hoverText = TextOf(w, "InfoText");
        Console.WriteLine("[status] hover: channel='" + Cut(hoverText) + "', line='" + Cut(StatusLineOf(w)) + "', line=hover="
            + (hoverText.Length > 0 && StatusLineOf(w) == hoverText));

        // 4) chunk selection: the selection message lands on the same line
        var chunk = w.FindNamed<Control>("Map")!;
        var clickAt = chunk.TranslatePoint(new Point(chunk.Bounds.Width / 2, chunk.Bounds.Height / 2), w) ?? new Point(10, 10);
        w.MouseDown(clickAt, MouseButton.Left, RawInputModifiers.None);
        Pump(0.3);
        w.MouseUp(clickAt, MouseButton.Left, RawInputModifiers.None);
        Pump(3.0);
        var selText = TextOf(w, "SelInfoText");
        Console.WriteLine("[status] selection: channel='" + Cut(selText) + "' line='" + Cut(StatusLineOf(w)) + "', line=selection="
            + (selText.Length > 0 && StatusLineOf(w) == selText));

        // 5) an error overrides everything on the line; once its term expires the line returns to the live one
        w.ReportUnhandled(new InvalidOperationException("probe-error"));
        Pump(0.2);
        Console.WriteLine("[status] after the error: line='" + Cut(StatusLineOf(w)) + "' (expected Unexpected error)"
            + "; line timer running=" + ((DispatcherTimer?)PrivField(w, "_statusTimer"))?.IsEnabled
            + " — it is the one that must clear the message by its term in a normal run");
        Console.WriteLine("[status] App channel remembers the error='" + Cut(TextOf(w, "StatusText")) + "', Scan channel untouched='" + Cut(TextOf(w, "ScanStatusText")) + "'");
        var timerTicks = 0;
        var probeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => timerTicks++);
        probeTimer.Start();
        Pump(32.0);
        probeTimer.Stop();
        var afterWait = StatusLineOf(w);
        var changedByExpire = w.Status.Expire();
        Console.WriteLine("[status] after 32 s: line='" + Cut(afterWait) + "', hover again=" + (afterWait == hoverText && hoverText.Length > 0));
        Console.WriteLine("[status] an explicit line recompute removed the expired=" + changedByExpire + " → line='" + Cut(StatusLineOf(w)) + "', hover again="
            + (StatusLineOf(w) == hoverText && hoverText.Length > 0)
            + "; App channel remembers the error=" + Cut(TextOf(w, "StatusText")).StartsWith("Unexpected error"));
        Console.WriteLine("[status] DispatcherTimer in headless: " + timerTicks + " tick(s) in 32 s (0 — without a platform loop timers do not tick)");

        // 6) hex box: fixed width, a long blob goes into horizontal scrolling
        EnsureDataPanelOpen(w);
        var tabs = w.FindNamed<TabControl>("DataTabs")!;
        tabs.SelectedIndex = 0;
        Pump(1.0);
        var hex = w.FindNamed<TextBox>("HexEditBox")!;
        var panel = w.FindNamed<StackPanel>("ChunkDataPanel")!;
        double hexW0 = hex.Bounds.Width, panelW0 = panel.Bounds.Width;
        hex.Text = string.Concat(Enumerable.Repeat("0a ", 4000));
        Pump(1.0);
        Console.WriteLine("[status] hex box: width " + hexW0.ToString("0") + " → " + hex.Bounds.Width.ToString("0")
            + " (expected 320 and not growing), panel " + panelW0.ToString("0") + " → " + panel.Bounds.Width.ToString("0")
            + ", scrolling=" + ScrollViewer.GetHorizontalScrollBarVisibility(hex));
        Console.WriteLine("[status] the line does not overflow the window: line width=" + line!.Bounds.Width.ToString("0") + " ≤ window " + w.Width.ToString("0"));

        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    private static int CountOf(object? o)
        => o is null ? -1 : (int)(o.GetType().GetProperty("Count")?.GetValue(o) ?? -1);

    private static long? EditingPos(Window w) => ChunkViewOf(w).EditingPosition;

    private static void PutWholeChunk(Window w, long pos, byte[] blob) => EditsOf(w).PutWholeChunk(pos, blob);

    private static void RemoveWholeChunk(Window w, long pos) => EditsOf(w).RemoveWholeChunk(pos);

    /// <summary>Number of positions with edits — by the window's former field name (now it is EditSession).</summary>
    private static int DictCount(Window w, string field) => field switch
    {
        "_pendingWholeChunk" => EditsOf(w).WholeChunkEditCount,
        "_pendingPayloadEdits" => EditsOf(w).PayloadEditCount,
        _ => -1
    };

    /// <summary>B4: put the app into the "hex edit and field edit of one position" state and check the dialog.</summary>
    private static void ConflictForced(string bigSave)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        ResetStatus(w);
        w.OpenStartupFile(bigSave);
        if (!WaitLoaded(w, SaveToken(bigSave), "Scan complete", 900)) { Console.WriteLine("FAILED: load"); return; }
        var pick = w.FindNamed<ComboBox>("ChunkPick")!;
        var panel = w.FindNamed<StackPanel>("ChunkDataPanel")!;

        EnsureDataPanelOpen(w);
        long pos = PickEditableCard(w);
        Console.WriteLine("  editable position=" + pos);
        if (pos < 0) { Console.WriteLine("  FAILED: found no card to edit"); CloseNow(w); Pump(0.5); Console.WriteLine("DONE"); return; }

        // --- Case A: there is a hex edit, then a field edit of the same position is applied ---
        var fake = new byte[] { 0x0a, 0x14, 0x1e, 0x28 };
        PutWholeChunk(w, pos, fake);
        Console.WriteLine("  [A] set a hex edit on pos=" + pos + " (hex=" + DictCount(w, "_pendingWholeChunk") + ", field=" + DictCount(w, "_pendingPayloadEdits") + ")");
        ClickButtonIn(w, "Apply");
        Pump(2.0);
        var dlgA = w.OwnedWindows.Count > 0;
        Console.WriteLine("  [A] hex-vs-field dialog='" + dlgA + "'");
        Shot(w, "conflict_A_dialog");
        if (dlgA)
        {
            AnswerDialog(w, false);
            Pump(1.5);
            Console.WriteLine("  [A/No] hex=" + DictCount(w, "_pendingWholeChunk") + " field=" + DictCount(w, "_pendingPayloadEdits") + " (expected hex=1, field=0)");
            ClickButtonIn(w, "Apply");
            Pump(1.5);
            AnswerDialog(w, true);
            Pump(2.0);
            Console.WriteLine("  [A/Yes] hex=" + DictCount(w, "_pendingWholeChunk") + " field=" + DictCount(w, "_pendingPayloadEdits") + " (expected hex=0, field=1)");
            Shot(w, "conflict_A_after_yes");
        }

        // --- Case B: there is a field edit, then a hex edit of the same position ---
        var hexField = w.FindNamed<TextBox>("HexEditBox");
        if (hexField is not null)
        {
            // point Raw hex exactly at the position where the field edit lives
            EditsOf(w).HexTargetPosition = pos;
            Console.WriteLine("  [B] field=" + DictCount(w, "_pendingPayloadEdits") + ", pointing hex at pos=" + pos);
            ClickButtonIn(w, "Apply hex");
            Pump(2.0);
            var dlgB = w.OwnedWindows.Count > 0;
            Console.WriteLine("  [B] field-vs-hex dialog='" + dlgB + "' status='" + Cut(TextOf(w, "ChunkStatusText")) + "'");
            Shot(w, "conflict_B_dialog");
            if (dlgB)
            {
                AnswerDialog(w, false);
                Pump(1.5);
                Console.WriteLine("  [B/No] hex=" + DictCount(w, "_pendingWholeChunk") + " field=" + DictCount(w, "_pendingPayloadEdits") + " (expected hex=0, field=1)");
                ClickButtonIn(w, "Apply hex");
                Pump(1.5);
                AnswerDialog(w, true);
                Pump(2.0);
                Console.WriteLine("  [B/Yes] hex=" + DictCount(w, "_pendingWholeChunk") + " field=" + DictCount(w, "_pendingPayloadEdits") + " (expected hex=1, field=0)");
            }
        }

        RemoveWholeChunk(w, pos);   // drop the synthetic blob so it does not get into the copy
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    private static readonly System.Reflection.BindingFlags Priv = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    private static object? PrivField(object o, string name) => o.GetType().GetField(name, Priv)?.GetValue(o);

    /// <summary>Closes the window without raising the unsaved working copy confirmation:
    /// a headless probe cannot answer a modal dialog.</summary>
    private static void CloseNow(Window w)
    {
        if (w is MainWindow)
            w.GetType().GetField("_closeConfirmed", Priv)?.SetValue(w, true);
        w.Close();
    }

    // The model and the edits live in the window's services (ScanService/EditSession/ChunkViewService): probes
    // fetch them to read the same state as the UI instead of duplicating it in the window.
    private static ScanService ScanOf(Window w) => (ScanService)PrivField(w, "_scan")!;
    private static EditSession EditsOf(Window w) => (EditSession)PrivField(w, "_edits")!;
    private static VSSaveFile SaveOf(Window w) => (VSSaveFile)PrivField(w, "_save")!;
    private static ChunkViewService ChunkViewOf(Window w) => (ChunkViewService)PrivField(w, "_chunkView")!;

    private static void ResetStatus(Window w) => ((MainWindow)w).Status.ClearAll();

    /// <summary>Waits for the ACTUAL loading of a specific save (by name and chunk count), not for any text.</summary>
    /// <summary>The save token for WaitLoaded: the file name without extension. Probes also run on copies
    /// of saves (the directory with the original may be read-only), so the name comes from the path instead of being hardcoded.</summary>
    private static string SaveToken(string path) => System.IO.Path.GetFileNameWithoutExtension(path);

    private static bool WaitLoaded(Window w, string saveToken, string scanToken, double maxSeconds)
    {
        var end = DateTime.UtcNow.AddSeconds(maxSeconds);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            try { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } catch { }
            var st = TextOf(w, "StatusText");
            var sc = TextOf(w, "ScanStatusText");
            if (st.StartsWith("opened:") && st.Contains(saveToken) && sc.Contains(scanToken)) return true;
            Thread.Sleep(30);
        }
        return false;
    }

    private static void E2E(string bigSave, string smallSave)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);

        var bad = System.IO.Path.Combine(OutDir, "not-a-save.vcdbs");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(bad)!);
        System.IO.File.WriteAllText(bad, "this is definitely not a sqlite save");

        Console.WriteLine("[E2E] 1) broken file (exists, but is not a save)");
        ResetStatus(w);
        w.OpenStartupFile(bad);
        var failed = WaitFor(w, "StatusText", "Failed to open save", 60);
        Console.WriteLine("      failed=" + failed + " status='" + Cut(TextOf(w, "StatusText")) + "'");
        Console.WriteLine("      _busy after failure = " + PrivField(w, "_busy") + " (expected False)");

        Console.WriteLine("[E2E] 2) large save");
        ResetStatus(w);
        w.OpenStartupFile(bigSave);
        var ok = WaitLoaded(w, SaveToken(bigSave), "Scan complete", 900);
        Console.WriteLine("      loaded=" + ok + " status='" + Cut(TextOf(w, "StatusText")) + "'");
        Console.WriteLine("      scan='" + Cut(TextOf(w, "ScanStatusText")) + "'");
        if (!ok) { Console.WriteLine("      FAILED: the large save did not finish loading"); return; }

        Console.WriteLine("[E2E] 3) reload DURING the large save's scan");
        ResetStatus(w);
        w.OpenStartupFile(bigSave);
        // Wait specifically for the scan phase: while terrain is loading the window is busy (_busy) and a request to open
        // another save is deliberately rejected — otherwise the step would check the wrong thing.
        var scanStarted = WaitFor(w, "ScanStatusText", "Scanning", 180);
        var duringScan = TextOf(w, "ScanStatusText");
        Console.WriteLine("      scan started=" + scanStarted + ", status: '" + Cut(duringScan) + "'");
        if (!scanStarted) { Console.WriteLine("      FAILED: the large save's scan did not start"); return; }
        w.OpenStartupFile(smallSave);
        var smallOk = WaitLoaded(w, SaveToken(smallSave), "Scan complete", 300);
        Console.WriteLine("      small save loaded=" + smallOk + " status='" + Cut(TextOf(w, "StatusText")) + "'");
        Pump(25.0);   // if the stale scan published data, it would have overwritten the small save
        Console.WriteLine("      after 25 s scan='" + Cut(TextOf(w, "ScanStatusText")) + "'");
        Console.WriteLine("      picker items=" + (w.FindNamed<ComboBox>("ChunkPick")?.ItemCount ?? -1));
        Shot(w, "e2e_01_after_reload_during_scan");

        Console.WriteLine("[E2E] 4) large save again (for edits)");
        ResetStatus(w);
        w.OpenStartupFile(bigSave);
        var big2 = WaitLoaded(w, SaveToken(bigSave), "Scan complete", 900);
        if (!big2) { Console.WriteLine("      FAILED: reload"); return; }
        var pick = w.FindNamed<ComboBox>("ChunkPick")!;
        Console.WriteLine("      loaded=True picker items=" + pick.ItemCount);
        EnsureDataPanelOpen(w);

        Shot(w, "e2e_02_big_scanned");

        Console.WriteLine("[E2E] 5) field edit and hex/field conflict (B4)");
        bool conflictNo = false, conflictYes = false, dialogSeen = false;
        long pendingPos = PickEditableCard(w);
        if (pendingPos < 0) { Console.WriteLine("      FAILED: no card to edit"); return; }
        ClickButtonIn(w, "Apply");
        Pump(1.5);
        var st5 = TextOf(w, "ChunkStatusText");
        if (CountPending(st5) == 0) { Console.WriteLine("      no pending: " + Cut(st5)); return; }
        Console.WriteLine("      pendingPos=" + pendingPos + " status='" + Cut(st5) + "'");

        // Raw hex must point exactly at the column where the field edit lives (a chunk has several)
        EditsOf(w).HexTargetPosition = pendingPos;
        ClickButtonIn(w, "Apply hex");
        Pump(2.0);
        var hasDialog = w.OwnedWindows.Count > 0;
        Console.WriteLine("      conflict dialog=" + hasDialog + " status='" + Cut(TextOf(w, "ChunkStatusText")) + "'");
        if (hasDialog)
        {
            dialogSeen = true;
            AnswerDialog(w, false);
            Pump(2.0);
            // "No" does not write counters into the status (it holds the cancellation message) — look at the model itself
            conflictNo = EditsOf(w).PayloadEditCount > 0 && EditsOf(w).WholeChunkEditCount == 0;
            Console.WriteLine("      'No' -> field edits kept=" + conflictNo + " status='" + Cut(TextOf(w, "ChunkStatusText")) + "'");
            Shot(w, "e2e_03_hex_conflict");
            ClickButtonIn(w, "Apply hex");
            Pump(2.0);
            AnswerDialog(w, true);
            Pump(2.0);
            var after = TextOf(w, "ChunkStatusText");
            conflictYes = after.Contains("pending edit") || after.Contains("chunk(s)");
            Console.WriteLine("      'Yes' -> status='" + Cut(after) + "'");
        }
        Console.WriteLine("      summary: dialogSeen=" + dialogSeen + " noKeepsFields=" + conflictNo + " yesApplied=" + conflictYes);

        Console.WriteLine("[E2E] 6) saving the edits to the copy (review dialog)");
        byte[]? origBlob = null;
        if (pendingPos >= 0)
        {
            using (var s0 = VSSaveFile.OpenReadOnly(bigSave)) origBlob = s0.ReadChunk(pendingPos);
        }
        ClickButtonIn(w, "Review & apply…");
        Pump(3.0);
        var reviewDlg = w.OwnedWindows.FirstOrDefault();
        Console.WriteLine("      review dialog='" + (reviewDlg?.Title ?? "none") + "'");
        if (reviewDlg is not null)
        {
            var body = string.Join(" | ", Descendants(reviewDlg).OfType<TextBlock>().Take(6).Select(t => t.Text));
            Console.WriteLine("      dialog header: " + Cut(body));
            Shot(w, "e2e_03b_review_dialog");
            Shot(reviewDlg, "e2e_03b_review_dialog_window");
            var write = Descendants(reviewDlg).OfType<Button>().FirstOrDefault(b => (b.Content as string)?.StartsWith("Apply to working copy") == true);
            Console.WriteLine("      write button='" + (write?.Content as string ?? "none") + "' enabled=" + (write?.IsEnabled));
            write?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
        else
        {
            AnswerDialog(w, true);   // fallback: the old confirmation dialog
        }
        var end = DateTime.UtcNow.AddSeconds(600);
        while (DateTime.UtcNow < end && !TextOf(w, "ChunkStatusText").Contains("Applied")) Pump(1.0);
        var res = TextOf(w, "ChunkStatusText");
        Console.WriteLine("      result='" + Cut(res) + "'");
        Shot(w, "e2e_04_applied_to_working_copy");
        reviewDlg?.Close();
        Pump(0.5);
        var work = SaveOf(w);
        Console.WriteLine("      applied=" + EditsOf(w).AppliedCount + " pending=" + EditsOf(w).HasPending
                          + " workingCopy='" + work.Path + "' source='" + work.SourcePath + "'");
        if (pendingPos >= 0)
        {
            var copyBlob = work.ReadChunk(pendingPos);
            Console.WriteLine("      blob changed in the working copy="
                              + (copyBlob is not null && origBlob is not null && !copyBlob.SequenceEqual(origBlob)));
            using var s2 = VSSaveFile.OpenReadOnly(bigSave);
            var origAgain = s2.ReadChunk(pendingPos);
            Console.WriteLine("      original blob unchanged="
                              + (origAgain is not null && origBlob is not null && origAgain.SequenceEqual(origBlob)));
        }

        Console.WriteLine("[E2E] 7) reopening after all operations (and the working copy question)");
        ResetStatus(w);
        w.OpenStartupFile(smallSave);
        Pump(1.0);
        var unsavedDlg = w.OwnedWindows.FirstOrDefault();
        Console.WriteLine("      unsaved working copy question='" + (unsavedDlg?.Title ?? "none") + "'");
        if (unsavedDlg is not null)
        {
            AnswerDialog(w, false); // No — do not save
            Pump(1.0);
            var discardDlg = w.OwnedWindows.FirstOrDefault();
            Console.WriteLine("      loss confirmation='" + (discardDlg?.Title ?? "none") + "'");
            if (discardDlg is not null) AnswerDialog(w, true); // Yes — discard and open
        }
        var again = WaitLoaded(w, SaveToken(smallSave), "Scan complete", 300);
        Console.WriteLine("      reopened=" + again + " status='" + Cut(TextOf(w, "StatusText")) + "'");
        CloseNow(w);
        Pump(1.0);
        Console.WriteLine("DONE");
    }

    /// <summary>The map legend is pinned to the bottom-left of the map and is covered by the expanded drawer — hence
    /// we check it separately with the drawer collapsed: in the dark theme there must be no light fill.</summary>
    private static void LegendTheme(string save)
    {
        var w = new MainWindow { Width = 1280, Height = 800 };
        w.Show();
        Pump(1.0);
        w.RequestedThemeVariant = ThemeVariant.Dark;
        Pump(0.5);
        ResetStatus(w);
        w.OpenStartupFile(save);
        var token = System.IO.Path.GetFileNameWithoutExtension(save);
        if (!WaitLoaded(w, token, "Scan complete", 900)) { Console.WriteLine("FAILED: load"); return; }
        var panel = w.FindNamed<Expander>("DataPanel")!;
        panel.IsExpanded = false;   // otherwise the lower part of the map (and the legend) is hidden behind the drawer
        Pump(2.0);

        // why the brushes do not resolve: compare the ways of accessing App.Resources
        foreach (var k in new[] { "PanelBackgroundBrush", "CardBorderBrush", "TextPrimaryBrush" })
        {
            var winExplicit = w.TryGetResource(k, w.ActualThemeVariant, out var v1) ? Describe(v1 as IBrush) : "-";
            var winNull = w.TryGetResource(k, null, out var v2) ? Describe(v2 as IBrush) : "-";
            var winFind = w.TryFindResource(k, out var v3) ? Describe(v3 as IBrush) : "-";
            var appExplicit = ((Avalonia.Controls.IResourceHost)Application.Current!).TryGetResource(k, w.ActualThemeVariant, out var v4) ? Describe(v4 as IBrush) : "-";
            var appResources = Application.Current!.Resources.TryGetResource(k, w.ActualThemeVariant, out var v5) ? Describe(v5 as IBrush) : "-";
            Console.WriteLine($"[brush] {k}: win.explicit={winExplicit} win.null={winNull} win.TryFind={winFind} app.explicit={appExplicit} app.Resources={appResources}");
        }
        var drawer = w.FindNamed<Expander>("DataPanel")?.Parent as Border;
        var card = Descendants(w.FindNamed<StackPanel>("ChunkDataPanel")!).OfType<TextBlock>().FirstOrDefault();
        Console.WriteLine("[brush] reference from XAML (drawer background)=" + Describe(drawer?.Background)
            + " first card text=" + Describe(card?.Foreground));

        var host = w.FindNamed<ContentControl>("MapLegendHost")!;
        Console.WriteLine("[legend] theme=" + w.ActualThemeVariant + " host visible=" + host.IsVisible + " bounds=" + Fmt(host));
        var legendBox = host.Content as Border;
        if (legendBox is not null)
        {
            Console.WriteLine("[legend] background=" + Describe(legendBox.Background) + " border=" + Describe(legendBox.BorderBrush));
            foreach (var tb in Descendants(legendBox).OfType<TextBlock>().Take(5))
                Console.WriteLine("[legend] text '" + Cut(tb.Text) + "' colour=" + Describe(tb.Foreground) + " size=" + tb.FontSize);
        }
        else Console.WriteLine("[legend] content=" + (host.Content?.GetType().Name ?? "null"));

        Shot(w, "legend_dark_collapsed");
        var darkFill = (legendBox?.Background as ISolidColorBrush)?.Color;
        w.RequestedThemeVariant = ThemeVariant.Light; Pump(2.0); Shot(w, "legend_light_collapsed");
        var lightFill = (legendBox?.Background as ISolidColorBrush)?.Color;
        // The old ThemeBrush fallback did not depend on the real resources and gave White on the dark theme:
        // so we check not "the brush is not empty" but that the fill is DIFFERENT and on the correct side.
        bool followsTheme = darkFill is { } dc && lightFill is { } lc
            && Brightness(dc) < 0.3 && Brightness(lc) > 0.7;
        Console.WriteLine("[legend] fill: dark theme=" + darkFill + " light=" + lightFill + " -> "
            + (followsTheme ? "OK: follows the theme" : "FAILED: the fill does not follow the theme"));
        w.RequestedThemeVariant = ThemeVariant.Dark; Pump(2.0); Shot(w, "legend_dark_collapsed2");
        CloseNow(w);
        Pump(0.5);
        Console.WriteLine("DONE");
    }

    private static double Brightness(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static string Sha256(string path)
    {
        using var fs = System.IO.File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    private static string Describe(IBrush? b)
        => b is ISolidColorBrush s ? s.Color.ToString() : (b?.ToString() ?? "(null)");

    private static void Pump(double seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            try { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } catch { }
            Thread.Sleep(25);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Shot(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        try { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } catch { }
        using var frame = w.CaptureRenderedFrame();
        if (frame == null)
        {
            Console.WriteLine("frame null: " + name);
            return;
        }
        var path = System.IO.Path.Combine(OutDir, name + ".png");
        frame.Save(path);
        Console.WriteLine("shot: " + path);
    }

    /// <summary>Status text by the FORMER control name: the visible line is now single, but the model
    /// keeps the last message of each channel — the probe reads the channel, not what is on the line.</summary>
    private static string TextOf(Window w, string name)
    {
        var channel = name switch
        {
            "StatusText" => StatusChannel.App,
            "InfoText" => StatusChannel.Hover,
            "ScanStatusText" => StatusChannel.Scan,
            "ChunkStatusText" => StatusChannel.Chunk,
            "SearchStatusText" => StatusChannel.Search,
            "FilterStatusText" => StatusChannel.Filter,
            "SelInfoText" => StatusChannel.Selection,
            _ => (StatusChannel?)null,
        };
        if (channel is { } ch) return ((MainWindow)w).Status.TextOf(ch);
        return w.FindNamed<TextBlock>(name)?.Text ?? "(none)";
    }

    /// <summary>The text that is actually visible on the single status line.</summary>
    private static string StatusLineOf(Window w) => w.FindNamed<TextBlock>("AppStatusText")?.Text ?? "";

    private static void SetText(Window w, string name, string text)
    {
        var tb = w.FindNamed<TextBox>(name);
        if (tb != null)
        {
            tb.Text = text;
        }
    }

    private static IEnumerable<Visual> Descendants(Visual v)
    {
        foreach (var c in v.GetVisualChildren())
        {
            yield return c;
            foreach (var d in Descendants(c))
            {
                yield return d;
            }
        }
    }

    private static void ClickButton(Window w, string content)
    {
        var btn = Descendants(w).OfType<Button>().FirstOrDefault(b => (b.Content as string) == content);
        if (btn == null)
        {
            Console.WriteLine("button not found: " + content);
            return;
        }
        btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
