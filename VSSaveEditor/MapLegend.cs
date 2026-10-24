using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VSCore;

namespace VSSaveEditor;

/// <summary>
///     Map legend: explains what the colours of the active mode mean. The palettes are the same as in
///     rendering (TerrainColorizer/BlockColorPalette), so the legend cannot drift from the map (P1-6).
/// </summary>
internal static class MapLegend
{
    private const int StripSteps = 12;

    public static Control Build(Control owner, TerrainMap tm, int mode, int? layerY,
        IReadOnlyList<(string Code, long Count)> top)
    {
        var box = new StackPanel { Spacing = 3, MaxWidth = 260 };
        box.Children.Add(Text(owner, layerY is int y ? $"Y layer = {y} (block slice)" : TerrainView.ModeName(mode),
            true));

        if (layerY is not null)
        {
            // In slice mode the colour comes from the block at this level, and air is the "hole" (SliceColor).
            box.Children.Add(Text(owner, "colour = block at this level", gray: true));
            box.Children.Add(Text(owner, "empty = air / chunk not loaded", gray: true));
            AddTopBlocks(owner, box, tm, top);
        }
        else if (mode == TerrainView.ModeDepth)
        {
            box.Children.Add(Strip(Enumerable.Range(0, 11).Select(d => ToColor(TerrainColorizer.DepthColor(d)))));
            box.Children.Add(Text(owner, "0 (shallow) → 10+ (deep)", gray: true));
            box.Children.Add(Row(owner, Color.FromRgb(120, 124, 110), "land"));
        }
        else if (mode == TerrainView.ModeBlocks)
        {
            AddTopBlocks(owner, box, tm, top);
            box.Children.Add(Text(owner, "as the in-game world map (season tint, rain surface)", gray: true));
        }
        else
        {
            int lo = tm.MinSurface, hi = Math.Max(lo + 1, tm.MaxSurface);
            box.Children.Add(Strip(Enumerable.Range(0, StripSteps)
                .Select(i => HeightColor(i / (double)(StripSteps - 1)))));
            box.Children.Add(Text(owner, $"Y {lo} (low) → Y {hi} (high)", gray: true));
            box.Children.Add(Strip(Enumerable.Range(0, 6).Select(i => ToColor(TerrainColorizer.DepthColor(i * 2)))));
            box.Children.Add(Text(owner, "water: shallow → deep", gray: true));
        }

        var legend = new Border
        {
            Child = box,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6),
            Opacity = 0.92,
            IsHitTestVisible = false // the map under the legend must keep receiving zoom/pan/selection
        };
        CardUi.BindTheme(owner, legend, Border.BackgroundProperty, "PanelBackgroundBrush", true);
        CardUi.BindTheme(owner, legend, Border.BorderBrushProperty, "CardBorderBrush", true);
        return legend;
    }

    /// <summary>Height colour - exactly the heightmap formula from TerrainColorizer.ColumnColor.</summary>
    private static Color HeightColor(double t)
    {
        return Color.FromRgb((byte)(90 + t * 160), (byte)(175 - t * 90), (byte)(45 + t * 130));
    }

    private static Color ToColor((byte R, byte G, byte B) c)
    {
        return Color.FromRgb(c.R, c.G, c.B);
    }

    private static void AddTopBlocks(Control owner, StackPanel box, TerrainMap tm,
        IReadOnlyList<(string Code, long Count)> top)
    {
        if (top.Count == 0)
        {
            box.Children.Add(Text(owner, "block list appears after the scan", gray: true));
            return;
        }

        // Swatch - the same colour as the map (the tint depends on position, so take the map centre).
        int wx = tm.MinX + tm.Width / 2, wz = tm.MinZ + tm.Height / 2;
        foreach (var (code, count) in top.Take(8))
        {
            var (r, g, b) = TerrainColorizer.BlockColor(tm, code, 0, wx, tm.SeaLevel, wz);
            box.Children.Add(Row(owner, Color.FromRgb(r, g, b), $"{code}  {count:N0}"));
        }
    }

    private static Control Strip(IEnumerable<Color> colors)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        foreach (var c in colors)
            sp.Children.Add(new Border { Width = 12, Height = 10, Background = new SolidColorBrush(c) });
        return sp;
    }

    private static Control Row(Control owner, Color color, string label)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var swatch = new Border
        {
            Width = 12,
            Height = 12,
            Background = new SolidColorBrush(color),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };
        CardUi.BindTheme(owner, swatch, Border.BorderBrushProperty, "CardBorderBrush", true);
        sp.Children.Add(swatch);
        sp.Children.Add(Text(owner, label));
        return sp;
    }

    private static TextBlock Text(Control owner, string text, bool bold = false, bool gray = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = UiStyle.MonoFontSize,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            TextWrapping = TextWrapping.Wrap
        };
        CardUi.BindTheme(owner, tb, TextBlock.ForegroundProperty, gray ? "TextSecondaryBrush" : "TextPrimaryBrush");
        return tb;
    }
}