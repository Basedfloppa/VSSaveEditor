using System.Collections.Concurrent;
using System.Text;

namespace VSCore;

/// <summary>Result of building the terrain map: surface and bottom bound per column (top-down).</summary>
public sealed class TerrainMap
{
    public int MinX, MinZ, Width, Height;
    public int[] SurfaceY;        // absolute surface Y (0 = no data)
    public int[] BottomY;         // absolute bottom bound of the column (0 = none)
    public ushort[] TopBlock;     // block id at the surface
    public int[] WaterDepth;      // real thickness of the water body under the surface (in blocks) for water; otherwise 0
    public int MaxSurface, MinSurface;

    /// <summary>The Y the game draws the map by: the first rain-IMPERMEABLE block from the top.
    /// Not the same as <see cref="SurfaceY"/>: grass/flowers have rainPermeable=true and the game does not
    /// show them on the map — otherwise a field would be bright green and flowery instead of olive turf.</summary>
    public ushort[]? MapY;
    /// <summary>Block id at <see cref="MapY"/> — the colour is taken from it, not from the top block.</summary>
    public ushort[]? MapBlock;
    /// <summary>In-game map light multiplier (x128): slope + blur + quantization,
    /// as in ChunkMapLayer.GenerateChunkImage. 128 = unchanged.</summary>
    public byte[]? Shadow;

    /// <summary>Fraction of the year from the save calendar (0…1): seasonal tint of grass/foliage. 0.25 — spring.</summary>
    public double YearRel = 0.25;

    /// <summary>World sea level: the altitude corrections of the tint are computed from it (Climate.GetRainFall /
    /// GetAdjustedTemperature). The save does not store it, so we take the mode of the water surface — an ocean
    /// yields the most frequent altitude. 110 — the game default when there is no water in the area.</summary>
    public int SeaLevel = 110;

    /// <summary>Lower/upper Y for which the save has data at all (the cy bounds of the chunks).
    /// This is the range of the "Y layer" slider: a slice is interesting in caves too — below the surface.</summary>
    public int MinWorldY, MaxWorldY;

    /// <summary>Coordinates of the chunks that actually exist (cx,cz). Used so as not to
    /// draw "non-existent" chunks or a grid where there is no data.</summary>
    public readonly System.Collections.Generic.HashSet<(int cx, int cz)> PresentChunks = new();

    /// <summary>The area the map was built for (the window), or null — a map over the whole save.
    /// A save with chunks at two ends of the world cannot be covered by one map (bbox ~10¹³ columns),
    /// so a window is built for one area and the others follow the scrolling (see <see cref="ChunkAreas"/>).</summary>
    public ChunkArea? Area { get; private set; }

    /// <summary>Block registry of the save (block id -> code), if available.</summary>
    public BlockRegistry? Registry;

    /// <summary>Material of the top chiselled block (Chisel/MicroBlock) per column:
    /// packed (wx,wz) -> block id. Filled in <see cref="Build"/> from the block entities,
    /// otherwise all such blocks share one generic colour.</summary>
    public Dictionary<long, int> ChiselMaterial { get; set; } = new();

    /// <summary>Column key (wx,wz) for <see cref="ChiselMaterial"/>.</summary>
    public static long PackColumn(int wx, int wz) => ((long)wx << 32) ^ (uint)wz;

    /// <summary>Voxel source (chunks on demand) — needed for the Y slices
    /// (<see cref="TerrainColorizer.SliceColor"/>). null = the slice cannot be drawn and the editor
    /// shows the previous mask "the column is solid at this Y".</summary>
    public IVoxelSource? Voxels { get; set; }

    /// <summary>Column index in the flat map arrays, or -1 if the coordinates are outside the world.</summary>
    public int ColumnIndex(int wx, int wz)
    {
        if (wx < MinX || wz < MinZ || wx >= MinX + Width || wz >= MinZ + Height) return -1;
        return (wz - MinZ) * Width + (wx - MinX);
    }

    /// <summary>The real block exactly at level Y (liquids included). false = no data
    /// (no voxel source or no chunk); id == 0 = air.</summary>
    public bool TryBlockAt(int wx, int wy, int wz, out ushort id)
    {
        id = 0;
        var voxels = Voxels;
        if (voxels is null || wx < 0 || wy < 0 || wz < 0) return false;
        var blocks = voxels.GetChunkBlocks(wx >> 5, wy >> 5, wz >> 5);
        if (blocks is null) return false;
        id = blocks[(wx & 31) + (wz & 31) * 32 + (wy & 31) * 1024];
        return true;
    }

    /// <summary>Limit of the map area in columns. A column costs four arrays —
    /// int+int+ushort+int = 14 bytes. It is needed because W and H come from the 22-bit X/Z of a chunk
    /// (up to 4 Mi blocks each), and two chunks at opposite ends of the world give W×H ~10¹³ columns,
    /// which overflows int and ends in an OOM. The threshold guards against absurdity, not a memory budget:
    /// the largest real save among those checked ("Captured d3e82bd6") yields 79,265,792 columns
    /// (7,552×10,496, ~1.1 GiB of arrays), so 256 Mi is a ~3× margin and still far from 10¹³.</summary>
    public const long MaxCells = 256L * 1024 * 1024;

    /// <summary>Area size check BEFORE allocation: a comprehensible error instead of an OOM
    /// (width/height in blocks).</summary>
    public static void EnsureAreaFits(int width, int height)
    {
        long cells = (long)width * height;
        if (cells > MaxCells)
            throw new InvalidOperationException(
                $"map area too large: {width}x{height} blocks ({cells} columns, limit {MaxCells}) — " +
                "the save contains chunks at opposite ends of the world");
    }

    /// <summary>
    ///     Map of the save's main area (the one with the world centre) — the default behaviour for callers
    ///     that do not need the list of areas. A save with chunks at two ends of the world cannot be covered by
    ///     one map (bbox ~10¹³ columns), so "the whole save" here means "the main area".
    /// </summary>
    public static TerrainMap Build(VSSaveFile save, IProgress<double>? progress = null)
        => BuildInArea(save, ChunkAreas.Build(save).Main, progress);

    /// <summary>
    ///     Map of one area: rows of other areas are not read from the DB and not decoded at all.
    ///     In a two-area save that is 16,144 rows instead of 155,584 — both in memory and in time
    ///     (the flat array of phase 1 is sized by the rows: 132 MB against 1.27 GB).
    /// </summary>
    public static TerrainMap BuildInArea(VSSaveFile save, ChunkArea? area, IProgress<double>? progress = null)
    {
        var rows = save.ChunkRowsInWindow(area);
        int n = rows.Count;
        int total = Math.Max(1, n);
        var reg = save.BlockRegistry;

        // Chiselled blocks (Chisel/MicroBlock): their material lives in the block entity, not in the block id.
        // We scan only if such codes exist in the save at all — otherwise there is zero overhead.
        bool scanChisel = reg.AnyCodeContains("chisel") || reg.AnyCodeContains("microblock");
        var chisel = new ConcurrentDictionary<long, long>();

        // Phase 1 (parallel): decode + scan of every chunk into a compact flat array of ulong[1024] per chunk.
        // The columns of each chunk are independent, so parallelism gives ~xN. Merging the surfaces of a column
        // (a column may live in several cy chunks) is done afterwards, sequentially, straight into the arrays.
        // Rain permeability by block id: the hot column-scan loop needs an array,
        // not a dictionary lookup by code string per block (otherwise 32 lookups per column).
        var rainPerm = new bool[Math.Max(1, reg.Count)];
        for (int id = 1; id < rainPerm.Length; id++)
            rainPerm[id] = BlockColors.IsRainPermeable(reg.CodeOf((ushort)id));

        int[] cxA = new int[n], czA = new int[n], cyA = new int[n];
        ulong[] flat = new ulong[(long)n * 1024];   // 0 => no data for this column
        int done = 0;
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
        Parallel.For(0, n, opts, idx =>
        {
            var ch = rows[idx];
            var (cx, cy, cz) = ChunkPosHelper.DecodeIndex((ulong)ch.Position);
            cxA[idx] = cx; czA[idx] = cz; cyA[idx] = cy;
            if (scanChisel) ScanChisel(ch.Data, reg, chisel);
            ushort[] blocks;
            try { blocks = ChunkBlockDecoder.DecodeChunkBlocksMergedOnePass(ch.Data); }
            catch { return; }
            long fb = (long)idx * 1024;
            for (int x = 0; x < 32; x++)
                for (int z = 0; z < 32; z++)
                {
                    int baseIdx = x + z * 32;
                    int top = -1, bottom = 0, rainTop = 0;
                    ushort rainId = 0;
                    for (int y = 31; y >= 0; y--)
                    {
                        ushort id = blocks[baseIdx + 1024 * y];
                        if (id == 0) continue;
                        if (top < 0) top = y;
                        bottom = y;
                        if (rainId == 0 && id < rainPerm.Length && !rainPerm[id])
                        {
                            rainId = id;
                            rainTop = y;
                        }
                    }
                    if (top < 0) continue;
                    ushort topId = blocks[baseIdx + 1024 * top];
                    int wd = 0;
                    if (BlockColorPalette.IsWater(reg.CodeOf(topId)))
                        for (int yy = top; yy >= 0; yy--)
                        {
                            if (BlockColorPalette.IsWater(reg.CodeOf(blocks[baseIdx + 1024 * yy]))) wd++;
                            else break;
                        }
                    // hash bit(63) | top_local[62..58] | bottom_local[57..53] | cy[52..50] | topId[49..34] | wd[33..29]
                    // | rainTop_local[28..24] | rainId[23..8]. The cy field is not read when merging,
                    // so it was left unoccupied — the rain fields sit in the free lower bits.
                    ulong v = 1UL << 63
                        | ((ulong)top << 58) | ((ulong)bottom << 53) | ((ulong)cy << 50)
                        | ((ulong)topId << 34) | ((ulong)wd << 29)
                        | ((ulong)rainTop << 24) | ((ulong)rainId << 8);
                    flat[fb + baseIdx] = v;
                }
            int p = Interlocked.Increment(ref done);
            if (progress != null && (p % 16 == 0 || p == n)) progress.Report((double)p / total);
        });

        // Phase 2a: world bounds — a parallel min/max reduction over the columns that have data.
        int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
        var boundsLock = new object();
        Parallel.For(0, n, opts,
            () => (minX: int.MaxValue, minZ: int.MaxValue, maxX: int.MinValue, maxZ: int.MinValue),
            (i, _, acc) =>
            {
                int wxBase = cxA[i] * 32, wzBase = czA[i] * 32;
                long fb = (long)i * 1024;
                for (int col = 0; col < 1024; col++)
                {
                    if ((flat[fb + col] >> 63) == 0) continue;
                    int wx = wxBase + (col & 31), wz = wzBase + (col >> 5);
                    if (wx < acc.minX) acc.minX = wx; if (wx > acc.maxX) acc.maxX = wx;
                    if (wz < acc.minZ) acc.minZ = wz; if (wz > acc.maxZ) acc.maxZ = wz;
                }
                return acc;
            },
            acc =>
            {
                lock (boundsLock)
                {
                    if (acc.minX < minX) minX = acc.minX;
                    if (acc.minZ < minZ) minZ = acc.minZ;
                    if (acc.maxX > maxX) maxX = acc.maxX;
                    if (acc.maxZ > maxZ) maxZ = acc.maxZ;
                }
            });
        // The Y range that has data: from the cy of the decoded chunks. Computed before the bounds check
        // so that an "empty" map (see EmptyMap) also knows its Y range.
        int minCy = int.MaxValue, maxCy = int.MinValue;
        for (int i = 0; i < n; i++)
        {
            int cy = cyA[i];
            if (cy < minCy) minCy = cy;
            if (cy > maxCy) maxCy = cy;
        }
        if (n == 0) { minCy = 0; maxCy = 0; }

        // All chunks of the area decoded to air (or there is no data): minX > maxX is not a "world
        // of zero size" but an absence of data. Without a separate branch W×H would be computed from int.MaxValue/
        // int.MinValue (overflow), producing random bounds and indexing of empty arrays.
        if (n == 0 || minX > maxX || minZ > maxZ) return EmptyMap(save, area, minCy, maxCy);

        int W = maxX - minX + 1, H = maxZ - minZ + 1;
        EnsureAreaFits(W, H);   // W×H may be ~10¹³: checked BEFORE the allocation
        int cells = (int)((long)W * H);
        var tm = new TerrainMap
        {
            MinX = minX, MinZ = minZ, Width = W, Height = H,
            SurfaceY = new int[cells],
            BottomY = new int[cells],
            TopBlock = new ushort[cells],
            WaterDepth = new int[cells],
            MapY = new ushort[cells],
            MapBlock = new ushort[cells],
            MaxSurface = int.MinValue, MinSurface = int.MaxValue,
            MinWorldY = minCy * 32, MaxWorldY = maxCy * 32 + 31,
            Area = area
        };
        // a "no data" sentinel for the max merge (otherwise a real surface at y=0 would look like an empty one)
        for (int i = 0; i < tm.SurfaceY.Length; i++) tm.SurfaceY[i] = int.MinValue;

        // Phase 2b: merging the column candidates. Chunks are grouped by (cx,cz): such a 32×32 block
        // owns its columns exclusively, so the groups are processed in parallel without races,
        // and inside a group the max merge by cy gives exactly the same result as a sequential pass.
        var byColumn = new Dictionary<long, List<int>>(Math.Max(4, n));
        for (int i = 0; i < n; i++)
        {
            long key = ((long)cxA[i] << 32) | (uint)czA[i];
            if (!byColumn.TryGetValue(key, out var list)) byColumn[key] = list = new List<int>(4);
            list.Add(i);
            tm.PresentChunks.Add((cxA[i], czA[i]));   // real chunks (any cy)
        }
        Parallel.ForEach(byColumn.Values, opts, list =>
        {
            foreach (int i in list)
            {
                int wxBase = cxA[i] * 32, wzBase = czA[i] * 32, cyBase = cyA[i] * 32;
                long fb = (long)i * 1024;
                for (int col = 0; col < 1024; col++)
                {
                    ulong v = flat[fb + col];
                    if ((v >> 63) == 0) continue;
                    int top = (int)((v >> 58) & 31), bottom = (int)((v >> 53) & 31);
                    int topId = (int)((v >> 34) & 0xFFFF), wd = (int)((v >> 29) & 31);
                    int rainTop = (int)((v >> 24) & 31), rainId = (int)((v >> 8) & 0xFFFF);
                    int wx = wxBase + (col & 31), wz = wzBase + (col >> 5);
                    int wy = cyBase + top;
                    int idx = (wz - minZ) * W + (wx - minX);
                    if (wy > tm.SurfaceY[idx])
                    {
                        tm.SurfaceY[idx] = wy;
                        tm.BottomY[idx] = cyBase + bottom;
                        tm.TopBlock[idx] = (ushort)topId;
                        tm.WaterDepth[idx] = wd;
                    }
                    // Merge by cy: we take the topmost non-permeable block of the column.
                    if (rainId != 0)
                    {
                        int rainWy = cyBase + rainTop;
                        if (rainWy > tm.MapY[idx])
                        {
                            tm.MapY[idx] = (ushort)rainWy;
                            tm.MapBlock[idx] = (ushort)rainId;
                        }
                    }
                }
            }
        });
        // normalise empty cells (SurfaceY=0 = no data) + relief bounds from the final columns
        int len = tm.SurfaceY.Length;
        for (int i = 0; i < len; i++)
        {
            int sy = tm.SurfaceY[i];
            if (sy == int.MinValue) { tm.SurfaceY[i] = 0; continue; }
            if (sy > tm.MaxSurface) tm.MaxSurface = sy;
            if (sy < tm.MinSurface) tm.MinSurface = sy;
        }
        // Chiselled blocks: expand the packed (posy<<32 | materialId) into materialId per column.
        if (chisel.Count > 0)
            foreach (var kv in chisel) tm.ChiselMaterial[kv.Key] = (int)(uint)kv.Value;

        tm.Registry = save.BlockRegistry.Count > 0 ? save.BlockRegistry : null;
        tm.YearRel = save.DetectYearRel() ?? 0.25;
        tm.SeaLevel = EstimateSeaLevel(tm, save.DetectSpawnY() ?? 0);
        tm.Shadow = BuildShadow(tm);
        // Y slices: voxels are read from the save on demand (only the visible area).
        tm.Voxels = new SaveVoxelSource(save.ReadChunk);
        return tm;
    }

    /// <summary>
    ///     A map without data: 1×1 with zeroed arrays. Needed for "the save has no chunks" and for an area
    ///     where all chunks decoded to air: otherwise the bounds would be computed from int.Min/MaxValue
    ///     (int overflow yields random numbers), and the views would index empty arrays.
    /// </summary>
    private static TerrainMap EmptyMap(VSSaveFile save, ChunkArea? area, int minCy, int maxCy)
    {
        var tm = new TerrainMap
        {
            MinX = area?.MinX ?? 0,
            MinZ = area?.MinZ ?? 0,
            Width = 1,
            Height = 1,
            SurfaceY = new int[1],
            BottomY = new int[1],
            TopBlock = new ushort[1],
            WaterDepth = new int[1],
            MapY = new ushort[1],
            MapBlock = new ushort[1],
            Shadow = new byte[1] { 128 },
            YearRel = save.DetectYearRel() ?? 0.25,
            SeaLevel = save.DetectSpawnY() is int sy and > 0 ? sy : 110,
            MinSurface = 0,
            MaxSurface = 0,
            MinWorldY = minCy * 32,
            MaxWorldY = maxCy * 32 + 31,
            Area = area
        };
        tm.Registry = save.BlockRegistry.Count > 0 ? save.BlockRegistry : null;
        tm.Voxels = new SaveVoxelSource(save.ReadChunk);
        return tm;
    }

    /// <summary>Sea level as the mode of the water surface: in 1.22 the world height is 512 and the sea level
    /// is not 110 but depends on the world; without this the tint would be computed for the wrong altitude and grass
    /// would drift to brown. If there is no water — 110 (the game default).</summary>
    private static int EstimateSeaLevel(TerrainMap tm, int spawnY)
    {
        if (tm.MapY is null || tm.WaterDepth is null) return 110;
        int maxY = Math.Max(tm.MaxWorldY, 1);
        var hist = new int[maxY + 1];
        int total = 0;
        for (int i = 0; i < tm.MapY.Length; i++)
        {
            if (tm.WaterDepth[i] <= 0) continue;
            int y = tm.MapY[i];
            if (y <= maxY) { hist[y]++; total++; }
        }
        int best = 0, bestCount = 0;
        for (int y = 0; y <= maxY; y++)
            if (hist[y] > bestCount) { bestCount = hist[y]; best = y; }
        if (best == 0) return spawnY > 0 ? spawnY : 110;
        // Spawn below the "water mode" means the mode is a lake on a slope, not the sea.
        return spawnY > 0 && spawnY < best ? spawnY : best;
    }

    /// <summary>
    ///     In-game map lighting: slope from the <see cref="MapY"/> heights (the neighbours to the left/above/
    ///     north-west), a multiplier of 0.52…1.48, a 3×3 box blur and dithered quantization —
    ///     the same as ChunkMapLayer.GenerateChunkImage. Without this the map is darker than the game: the current
    ///     hillshade only darkens (0.62…1.0), while the game lights slopes by up to +48%.
    ///     The blur here is global, whereas the game blurs within 32×32 chunks; this gives a barely visible
    ///     difference at the borders, but there are no block seams.
    /// </summary>
    private static byte[] BuildShadow(TerrainMap tm)
    {
        int W = tm.Width, H = tm.Height, len = W * H;
        var raw = new byte[len];
        for (int z = 0; z < H; z++)
        {
            int row = z * W;
            for (int x = 0; x < W; x++)
            {
                int i = row + x;
                int me = tm.MapY[i];
                if (me == 0) { raw[i] = 128; continue; }
                int up = z > 0 ? tm.MapY[i - W] : 0;
                int left = x > 0 ? tm.MapY[i - 1] : 0;
                int upLeft = x > 0 && z > 0 ? tm.MapY[i - W - 1] : 0;
                if (up == 0) up = me;
                if (left == 0) left = me;
                if (upLeft == 0) upLeft = me;
                int d1 = me - upLeft, d2 = me - up, d3 = me - left;
                int signs = Math.Sign(d1) + Math.Sign(d2) + Math.Sign(d3);
                int maxAbs = Math.Max(Math.Abs(d1), Math.Max(Math.Abs(d2), Math.Abs(d3)));
                float mult = 1f;
                if (signs > 0) mult = 1.08f + Math.Min(0.5f, maxAbs / 10f) / 1.25f;
                else if (signs < 0) mult = 0.92f - Math.Min(0.5f, maxAbs / 10f) / 1.25f;
                raw[i] = (byte)Math.Clamp(mult * 128f, 0f, 255f);
            }
        }

        // 3×3 box blur (in the game range=2 gives a three-pixel window) — separable.
        var tmp = new byte[len];
        for (int z = 0; z < H; z++)
        {
            int row = z * W;
            for (int x = 0; x < W; x++)
            {
                int a = raw[row + Math.Max(0, x - 1)], b = raw[row + x], c = raw[row + Math.Min(W - 1, x + 1)];
                tmp[row + x] = (byte)((a + b + c) / 3);
            }
        }
        var blur = new byte[len];
        for (int x = 0; x < W; x++)
            for (int z = 0; z < H; z++)
            {
                int a = tmp[Math.Max(0, z - 1) * W + x], b = tmp[z * W + x], c = tmp[Math.Min(H - 1, z + 1) * W + x];
                blur[z * W + x] = (byte)((a + b + c) / 3);
            }

        var shadow = new byte[len];
        for (int i = 0; i < len; i++)
        {
            // quantization by 0.2 + the fractional part of the UNBLURRED value (the game's dithering)
            float q = (int)((blur[i] / 128f - 1f) * 5f) / 5f;
            float frac = ((raw[i] / 128f - 1f) * 5f) % 1f / 5f;
            float mult = 1f + q + frac;
            shadow[i] = (byte)Math.Clamp(mult * 128f, 0f, 255f);
        }
        return shadow;
    }

    /// <summary>Finds chiselled blocks in a chunk and records their material per column.
    /// Deliberately WITHOUT a full chunk parse: we find the ASCII name of the block entity type and reconstruct
    /// the length-delimited field 8 record around it (tag 0x42 + length varint). That way the scan costs
    /// only as much as the number of chiselled blocks, not all fields of all chunks.
    /// The value is packed (posy &lt;&lt; 32 | materialId) so that the topmost block stays in the column.</summary>
    private static void ScanChisel(byte[] blob, BlockRegistry reg, ConcurrentDictionary<long, long> sink)
    {
        foreach (var needle in ChiselNeedles)
        {
            int from = 0;
            while (from <= blob.Length - needle.Length)
            {
                int rel = blob.AsSpan(from).IndexOf(needle);
                if (rel < 0) break;
                int namePos = from + rel;
                from = namePos + 1;

                int payloadStart = namePos - 1;              // the length byte of the type name
                if (payloadStart < 3) continue;
                if (blob[payloadStart] != (byte)needle.Length) continue;

                // the payload length varint ends right before the payload
                int vEnd = payloadStart - 1;
                int vStart = vEnd;
                while (vStart > 0 && (blob[vStart - 1] & 0x80) != 0) vStart--;   // the continuation bytes come earlier
                long len = 0; int shift = 0;
                for (int k = vStart; k <= vEnd && shift < 64; k++, shift += 7) len |= (long)(blob[k] & 0x7F) << shift;
                if (vStart < 1 || blob[vStart - 1] != 0x42) continue;   // tag: field 8, wiretype 2
                // LengthFits rather than `end > blob.Length`: payloadStart + len overflows at len ≈ long.Max
                // and lets the check pass, after which (int)len gives a negative length for new byte[].
                if (len < needle.Length + 1 || !VSCore.Format.PbReader.LengthFits(len, payloadStart, blob.Length)) continue;
                long end = payloadStart + len;

                var payload = new byte[(int)len];
                Array.Copy(blob, payloadStart, payload, 0, (int)len);
                if (ChiselMaterials.TryRead(payload, reg, out int wx, out int wy, out int wz, out var mats))
                {
                    long packed = ((long)wy << 32) | (uint)PickChiselMaterial(mats, reg);
                    sink.AddOrUpdate(PackColumn(wx, wz), packed, (_, old) => packed > old ? packed : old);
                }
                from = (int)end;   // we do not look inside this payload any more
            }
        }
    }

    private static readonly byte[][] ChiselNeedles =
    {
        Encoding.ASCII.GetBytes("Chisel"),
        Encoding.ASCII.GetBytes("MicroBlock"),
    };

    /// <summary>The material to paint a chiselled block with: the first "real" material from `materials`.
    /// Technical collider blocks (`meta-selectablecollider`, `meta-collider`) are not what is visible
    /// on the block, so we skip them; if there is nothing else, we return 0 — the caller
    /// then uses the neutral "chiselled" tone (a collider colour would be misleading).</summary>
    private static int PickChiselMaterial(int[] mats, BlockRegistry reg)
    {
        foreach (var m in mats)
        {
            if (m <= 0) continue;
            var code = reg.CodeOf(m);
            if (code is not null && code.StartsWith("meta-", StringComparison.OrdinalIgnoreCase)) continue;
            return m;
        }
        return 0;
    }
}
