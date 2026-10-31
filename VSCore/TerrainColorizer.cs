namespace VSCore;

/// <summary>
/// Terrain map colours and shading — shared logic for the on-screen renderer (`TerrainView`) and
/// the PNG map export (`MapImageRenderer`), so that the picture matches the map 1:1.
/// </summary>
public static class TerrainColorizer
{
    public const int ModeHeight = 0;
    public const int ModeBlocks = 1;
    public const int ModeDepth = 2;   // bathymetry: ocean depth (shallows light, depths dark)

    /// <summary>Base colour of a column for the render mode (without shading; the light multiplier is
    /// <see cref="ExtraShade"/>: for Blocks it is the in-game map shadow, for the other modes hillshade).</summary>
    public static (byte R, byte G, byte B) ColumnColor(TerrainMap tm, int i, int v, int? layerY, int mode, double range)
    {
        int id = tm.TopBlock[i];
        string? code = tm.Registry?.CodeOf(id);

        // A chiselled block (Chisel/MicroBlock) keeps its material in the block entity: we take the colour of the
        // actual material, otherwise all chiselled blocks would share one generic colour. Variants with a known
        // top overlay (chiseledblock-snow and the like) are not substituted — snow/ice is visible there.
        if (tm.ChiselMaterial.Count > 0 && BlockColorPalette.IsChiseled(code)
            && !code!.Contains("snow", StringComparison.OrdinalIgnoreCase)
            && !code.Contains("ice", StringComparison.OrdinalIgnoreCase))
        {
            int wx = tm.MinX + i % tm.Width, wz = tm.MinZ + i / tm.Width;
            if (tm.ChiselMaterial.TryGetValue(TerrainMap.PackColumn(wx, wz), out int mid) && mid > 0)
            {
                id = mid;
                code = tm.Registry?.CodeOf(mid) ?? code;
            }
        }

        // Blocks on the surface is the in-game map: the colour comes from the rain surface (without grass and
        // flowers) and is tinted by season/climate; the shadow (as in ChunkMapLayer) is applied by the caller.
        if (mode == ModeBlocks && layerY is null)
        {
            int wx = tm.MinX + i % tm.Width, wz = tm.MinZ + i / tm.Width;
            int mapId = tm.MapBlock is { } mb && i < mb.Length ? mb[i] : 0;
            int mapY = tm.MapY is { } my && i < my.Length ? my[i] : 0;
            if (mapId == 0) { mapId = id; mapY = v; }                 // no rain surface — the top block
            else if (mapId == tm.TopBlock[i]) mapId = id;              // chiselled block: the material comes from the block entity
            var mapCode = tm.Registry?.CodeOf(mapId) ?? code;
            // The caller applies the lighting through ExtraShade: that way the "Hillshade" checkbox in the export
            // also disables the in-game shadow instead of pretending to disable it.
            return BlockColor(tm, mapCode, (ushort)mapId, wx, mapY, wz);
        }
        if (mode == ModeDepth)
        {
            // Bathymetry: only the ocean is informative, land is a neutral background.
            if (!BlockColorPalette.IsWater(code)) return ((byte)120, (byte)124, (byte)110);
            int depth = tm.WaterDepth is { } wd && (uint)i < (uint)wd.Length ? Math.Max(0, wd[i]) : 0;
            return DepthColor(depth);
        }
        if (layerY is int l)
            return tm.BottomY[i] > 0 && tm.BottomY[i] <= l && l <= tm.SurfaceY[i] ? ((byte)150, (byte)150, (byte)30) : ((byte)210, (byte)210, (byte)235);
        if (BlockColorPalette.IsWater(code))
        {
            int depth = tm.WaterDepth is { } wd && (uint)i < (uint)wd.Length ? Math.Max(0, wd[i]) : 0;
            double d = Math.Clamp(depth / 7.0, 0, 1);
            return ((byte)Mix(64, 12, d), (byte)Mix(155, 55, d), (byte)Mix(225, 130, d));
        }
        if (mode == ModeBlocks)
        {
            int wx = tm.MinX + i % tm.Width, wz = tm.MinZ + i / tm.Width;
            var (cr, cg, cb) = BlockColor(tm, code, (ushort)Math.Clamp(id, 0, ushort.MaxValue), wx, v, wz);
            double sh = 0.9 + 0.1 * (v - tm.MinSurface) / range;
            return ((byte)Math.Clamp(cr * sh, 0, 255), (byte)Math.Clamp(cg * sh, 0, 255), (byte)Math.Clamp(cb * sh, 0, 255));
        }
        double t = (v - tm.MinSurface) / range;
        return ((byte)(90 + t * 160), (byte)(175 - t * 90), (byte)(45 + t * 130));
    }

    /// <summary>
    /// Cached surface colour: ColumnColor + ExtraShade, clamped exactly like the renderer did
    /// (ShadedContribution). The cache is filled lazily per visible column, so a large save pays only
    /// for what was actually shown.
    /// </summary>
    public static uint ColumnColorCached(TerrainMap tm, int i, int v, int? layer, int mode,
        double range, TerrainColorCache cache)
    {
        uint packed = cache.Get(i);
        if (packed != 0) return packed;
        var c = ColumnColor(tm, i, v, layer, mode, range);
        var sh = ExtraShade(tm, i, mode);
        var add = ShadedContribution(c, sh);
        packed = TerrainColorCache.Pack(true, (byte)add.R, (byte)add.G, (byte)add.B);
        cache.Set(i, packed);
        return packed;
    }

    /// <summary>Cached Y-slice colour (also memoises "air at this level" via the Has bit).</summary>
    public static uint SliceColorCached(TerrainMap tm, int i, int wx, int wy, int wz, int mode,
        double range, TerrainColorCache cache)
    {
        uint packed = cache.Get(i);
        if (packed != 0) return packed;
        var (r, g, b, has) = SliceColor(tm, wx, wy, wz, mode, range);
        packed = TerrainColorCache.Pack(has, r, g, b);
        cache.Set(i, packed);
        return packed;
    }

    /// <summary>
    /// Colour of the REAL block exactly at level Y — a horizontal slice of the map (the "Y layer" slider).
    /// <c>HasBlock = false</c> means air/no data: the caller draws the background there (in the editor —
    /// transparent), so caves and voids at that level show up as "holes".
    ///
    /// Differences from <see cref="ColumnColor"/>: the colour comes from the block AT THIS level, not from the surface
    /// of the column; relief shading is not applied (it describes the surface); the heightmap mode is drawn
    /// with the block palette, because "colour by height" at a fixed Y is the same for all land.
    /// If the voxel source is unavailable (the map was built without a save), we degrade to the previous
    /// two-colour mask "the column is solid at this Y".
    /// </summary>
    public static (byte R, byte G, byte B, bool HasBlock) SliceColor(TerrainMap tm, int wx, int wy, int wz, int mode, double range)
    {
        int i = tm.ColumnIndex(wx, wz);
        if (i < 0) return (0, 0, 0, false);

        if (tm.Voxels is null)
        {
            // There is no voxel source at all (the map was built without a save) — the previous
            // two-colour mask "the column is solid at this Y".
            if (tm.SurfaceY[i] <= 0) return (0, 0, 0, false);
            var mask = ColumnColor(tm, i, tm.SurfaceY[i], wy, mode, range);
            return (mask.R, mask.G, mask.B, true);
        }
        // The source exists: no chunk or air at this level is a BACKGROUND, not "the column is solid"
        // (otherwise a slice above the relief or inside an unloaded chunk would be flooded with the mask).
        if (!tm.TryBlockAt(wx, wy, wz, out ushort id) || id == 0) return (0, 0, 0, false);

        string? code = tm.Registry?.CodeOf(id);
        // A chiselled block: the material lives in the block entity and belongs to the top block of the column,
        // so we substitute only when the slice runs exactly along the surface.
        if (wy == tm.SurfaceY[i] && tm.ChiselMaterial.Count > 0 && BlockColorPalette.IsChiseled(code)
            && !code!.Contains("snow", StringComparison.OrdinalIgnoreCase)
            && !code.Contains("ice", StringComparison.OrdinalIgnoreCase))
        {
            if (tm.ChiselMaterial.TryGetValue(TerrainMap.PackColumn(wx, wz), out int mid) && mid > 0)
            {
                id = (ushort)mid;
                code = tm.Registry?.CodeOf(mid) ?? code;
            }
        }

        int depthAtLevel = Math.Max(1, tm.SurfaceY[i] - wy + 1);
        if (mode == ModeDepth)
        {
            if (!BlockColorPalette.IsWater(code)) return ((byte)120, (byte)124, (byte)110, true);
            var (dr, dg, db) = DepthColor(depthAtLevel - 1);
            return (dr, dg, db, true);
        }
        if (BlockColorPalette.IsWater(code))
        {
            double d = Math.Clamp(depthAtLevel / 7.0, 0, 1);
            return ((byte)Mix(64, 12, d), (byte)Mix(155, 55, d), (byte)Mix(225, 130, d), true);
        }
        var (cr, cg, cb) = BlockColor(tm, code, (ushort)Math.Clamp((int)id, 0, ushort.MaxValue), wx, wy, wz, textured: false);
        double sh = 0.9 + 0.1 * (wy - tm.MinSurface) / range;   // the same altitude correction as on the surface
        return ((byte)Math.Clamp(cr * sh, 0, 255), (byte)Math.Clamp(cg * sh, 0, 255), (byte)Math.Clamp(cb * sh, 0, 255), true);
    }

    /// <summary>
    ///     Colour of the block on the in-game map: the average top-face colour (plus a random texel
    ///     sample, like GetRandomColor in the game) × the climatic/seasonal tint.
    ///     The position is needed both for the tint (the seasonal LUT row is picked by a hash) and for the texel choice.
    /// </summary>
    public static (byte R, byte G, byte B) BlockColor(TerrainMap tm, string? code, ushort id,
        int wx, int wy, int wz, bool textured = true)
    {
        (byte R, byte G, byte B) rgb;
        // First the raw texture colour: ColorFor would return semantics for grass/water, while the map needs
        // exactly the texture colour, which is then multiplied by the tint.
        if (!BlockColors.TryGet(code, out rgb))
            rgb = BlockColorPalette.ColorFor(code, id);

        if (BlockColors.TryGetMap(code, out var meta))
        {
            var tint = MapTint.For(meta.ClimateMap, meta.SeasonMap, wx, wy, wz, tm.YearRel, tm.SeaLevel);
            // The game (ChunkMapLayer.GenerateChunkImage, colorAccurate) first tints BOTH colours —
            // the average GetColor and the random texel GetRandomColor — and only then blends
            // the texel onto the average at weight 0.6. Previously the texel was mixed into the raw average
            // before the tint, and for blocks with a grass overlay — twice (before and after), which made
            // sparse/verysparse and forest drift towards the texel colour instead of the grass layer.
            var texel = MapTint.Apply(meta.Samples[MapTint.Murmur3Mod(wx, wy, wz, meta.Samples.Length)], tint);
            // BlockWithGrassOverlay/BlockForestFloor: ONLY the grass layer is tinted, while the base
            // (soil, clay, peat, forest litter) stays untouched and is blended in with the overlay
            // weight. Weight 0 with a base present is forestfloor-0: the game returns the base without a tint.
            if (meta.GrassBase is { } baseColour)
            {
                var getColor = meta.GrassWeight > 0f
                    ? Overlay(baseColour, MapTint.Apply(rgb, tint), meta.GrassWeight)
                    : baseColour;
                rgb = textured && meta.Samples.Length > 1 ? Overlay(getColor, texel, 0.6f) : getColor;
            }
            else
            {
                rgb = MapTint.Apply(rgb, tint);
                if (textured && meta.Samples.Length > 1) rgb = Overlay(rgb, texel, 0.6f);
            }
        }
        return rgb;
    }

    /// <summary>Lighting that still has to be applied to the colour: for Blocks it is already inside
    /// <see cref="ColumnColor"/> (the in-game map shadow), for the other modes it is hillshade.</summary>
    public static double ExtraShade(TerrainMap tm, int i, int mode) => mode == ModeBlocks ? Shade(tm, i) : Hillshade(tm, i, tm.Width);

    /// <summary>
    ///     Contribution of one column to the LOD averaging: the colour with the shadow applied, clamped to 0..255.
    ///     The clamp is mandatory BEFORE summing: the in-game map shadow reaches 1.48, so for snow and
    ///     sand c.R*sh is regularly above 255 (266 and higher). Summing without the clamp makes the average
    ///     overflow the byte on the cast — white snow turns almost black while yellow flowers
    ///     stay bright. From the outside this looked like a yellow-black artefact on the LOD only.
    /// </summary>
    public static (int R, int G, int B) ShadedContribution((byte R, byte G, byte B) c, double sh)
        => ((int)Math.Clamp(c.R * sh, 0, 255),
            (int)Math.Clamp(c.G * sh, 0, 255),
            (int)Math.Clamp(c.B * sh, 0, 255));

    /// <summary>In-game map light multiplier per column (1.0 — no data/no array).</summary>
    public static double Shade(TerrainMap tm, int i)
    {
        var shadow = tm.Shadow;
        return shadow is not null && (uint)i < (uint)shadow.Length ? shadow[i] / 128.0 : 1.0;
    }

    private static (byte R, byte G, byte B) Overlay((byte R, byte G, byte B) a, (byte R, byte G, byte B) b, float w)
        => ((byte)(a.R * (1 - w) + b.R * w), (byte)(a.G * (1 - w) + b.G * w), (byte)(a.B * (1 - w) + b.B * w));

    /// <summary>Ocean depth colour from a discrete scale: clear contrast between depth steps,
    /// so that even a small difference (1…3 blocks) reads. depth≥LUT.Length — the darkest.</summary>
    public static (byte R, byte G, byte B) DepthColor(int depth)
    {
        ReadOnlySpan<(byte, byte, byte)> lut = new (byte, byte, byte)[]
        {
            (150, 200, 220), (115, 175, 208), (82, 150, 198), (56, 128, 188),
            (40, 110, 177), (28, 95, 165), (20, 82, 152), (14, 70, 140),
            (10, 60, 128), (7, 51, 117), (4, 43, 106)
        };
        int idx = Math.Clamp(depth, 0, lut.Length - 1);
        return lut[idx];
    }

    /// <summary>Relief shading: the surface normal and light from the upper left (0.62…1.0).</summary>
    public static double Hillshade(TerrainMap tm, int i, int W)
    {
        int h = tm.Height;
        int z = i / W, x = i - z * W;
        int me = tm.SurfaceY[i]; if (me <= 0) return 1.0; // no data — no shading
        int e = (x + 1 < W && tm.SurfaceY[i + 1] > 0) ? tm.SurfaceY[i + 1] : me;
        int w = (x - 1 >= 0 && tm.SurfaceY[i - 1] > 0) ? tm.SurfaceY[i - 1] : me;
        int s = (z + 1 < h && tm.SurfaceY[i + W] > 0) ? tm.SurfaceY[i + W] : me;
        int n = (z - 1 >= 0 && tm.SurfaceY[i - W] > 0) ? tm.SurfaceY[i - W] : me;
        double dzx = (e - w) / 2.0;
        double dzy = (s - n) / 2.0;
        // surface normal f = (-dzx, -dzy, 1); light from the upper left L = (-1,-1,2)
        double lx = -1.0, ly = -1.0, lz = 2.0;
        double nx = -dzx, ny = -dzy, nz = 1.0;
        double inv = 1.0 / (Math.Sqrt(lx * lx + ly * ly + lz * lz) * Math.Sqrt(nx * nx + ny * ny + nz * nz));
        double dot = (nx * lx + ny * ly + nz * lz) * inv;
        return 0.62 + 0.38 * Math.Max(0.0, dot);
    }

    private static int Mix(int a, int b, double t) => (int)(a + (b - a) * t);
}
