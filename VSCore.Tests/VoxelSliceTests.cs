using Xunit;

namespace VSCore.Tests;

/// <summary>
/// A horizontal slice along Y (the "Y layer" slider): the color of the REAL block at the selected level,
/// air = background; an on-demand voxel source and its cache.
/// </summary>
public class VoxelSliceTests
{
    // ---- fake voxel source: a 32x32x32 chunk, we decode nothing ----
    private sealed class FakeVoxels : IVoxelSource
    {
        private readonly Dictionary<long, ushort[]> _chunks = new();
        public int CachedChunks => _chunks.Count;

        public void Set(int wx, int wy, int wz, ushort id)
        {
            long key = (long)ChunkPosHelper.ToIndex(wx >> 5, wy >> 5, wz >> 5);
            if (!_chunks.TryGetValue(key, out var b))
                _chunks[key] = b = new ushort[ChunkBlockDecoder.CellsPerChunk];
            b[ChunkPosHelper.Index3D(wx & 31, wy & 31, wz & 31)] = id;
        }

        public ushort[]? GetChunkBlocks(int cx, int cy, int cz)
            => _chunks.TryGetValue((long)ChunkPosHelper.ToIndex(cx, cy, cz), out var b) ? b : null;
    }

    private static BlockRegistry Registry(params (int Id, string Code)[] items)
    {
        var payload = new MemoryStream();
        foreach (var (id, code) in items)
        {
            var e = new MemoryStream();
            e.WriteByte(0x08); e.Write(Varint(id));
            var cb = System.Text.Encoding.UTF8.GetBytes(code);
            e.WriteByte(0x12); e.Write(Varint(cb.Length)); e.Write(cb);
            var eb = e.ToArray();
            payload.WriteByte(0x12); payload.Write(Varint(eb.Length)); payload.Write(eb);
        }
        var p = payload.ToArray();
        var gd = new MemoryStream();
        gd.Write(System.Text.Encoding.ASCII.GetBytes("BlockIDs"));
        gd.WriteByte(0x12); gd.Write(Varint(p.Length)); gd.Write(p);
        return BlockRegistry.FromGamedata(gd.ToArray());
    }

    private static byte[] Varint(int v)
    {
        var ms = new MemoryStream();
        uint x = (uint)v;
        while (x >= 0x80) { ms.WriteByte((byte)(x | 0x80)); x >>= 7; }
        ms.WriteByte((byte)x);
        return ms.ToArray();
    }

    private static TerrainMap Terrain(BlockRegistry reg, int w, int h, int surface)
    {
        int n = w * h;
        var tm = new TerrainMap
        {
            MinX = 0, MinZ = 0, Width = w, Height = h,
            SurfaceY = new int[n], BottomY = new int[n], TopBlock = new ushort[n], WaterDepth = new int[n],
            MinSurface = surface, MaxSurface = surface, Registry = reg
        };
        for (int i = 0; i < n; i++) { tm.SurfaceY[i] = surface; tm.BottomY[i] = surface - 3; }
        return tm;
    }

    [Fact(DisplayName = "slice: TryBlockAt finds the block in the right chunk, and returns false outside the world/without data")]
    public void TryBlockAt_ResolvesChunkAndBounds()
    {
        var vox = new FakeVoxels();
        vox.Set(40, 70, 5, 7);              // chunk (1,2,0), locally (8,6,5)
        var tm = Terrain(Registry((7, "rock-granite")), 64, 64, 70);
        tm.Voxels = vox;

        Assert.True(tm.TryBlockAt(40, 70, 5, out var id));
        Assert.Equal(7, id);
        Assert.True(tm.TryBlockAt(40, 71, 5, out var air));
        Assert.Equal(0, air);               // air at the same spot, but higher up
        Assert.False(tm.TryBlockAt(-1, 70, 5, out _));          // a negative coordinate
        Assert.False(tm.TryBlockAt(1000, 70, 1000, out _));     // no such chunk
        Assert.False(tm.TryBlockAt(40, -1, 5, out _));
    }

    [Fact(DisplayName = "slice: the color of the REAL block is used at the level, air = background (HasBlock=false)")]
    public void SliceColor_UsesRealBlockAtLevel()
    {
        var reg = Registry((1, "rock-granite"), (2, "soil-low-none"), (3, "air"));
        var tm = Terrain(reg, 4, 4, surface: 5);
        var vox = new FakeVoxels();
        vox.Set(0, 5, 0, 1);   // the surface is granite
        vox.Set(0, 4, 0, 2);   // soil underneath
        // there is no block at y=6 (air)
        tm.Voxels = vox;

        var top = TerrainColorizer.SliceColor(tm, 0, 5, 0, TerrainColorizer.ModeBlocks, 1);
        var below = TerrainColorizer.SliceColor(tm, 0, 4, 0, TerrainColorizer.ModeBlocks, 1);
        var air = TerrainColorizer.SliceColor(tm, 0, 6, 0, TerrainColorizer.ModeBlocks, 1);

        Assert.True(top.HasBlock);
        Assert.True(below.HasBlock);
        Assert.False(air.HasBlock);          // air at the level is the background

        var (cr, cg, cb) = BlockColorPalette.ColorFor("rock-granite", 1);
        Assert.Equal((byte)(cr * 0.9), top.R);   // wy == MinSurface -> sh = 0.9, as on the surface
        Assert.Equal((byte)(cg * 0.9), top.G);
        Assert.Equal((byte)(cb * 0.9), top.B);

        var (sr, sg, sb) = BlockColorPalette.ColorFor("soil-low-none", 2);
        Assert.Equal((byte)(sr * 0.8), below.R);  // one below the surface: sh = 0.9 - 0.1
        Assert.NotEqual(top.R, below.R);          // this is the color of the block at the level, not of the column surface
        _ = sg; _ = sb;
    }

    [Fact(DisplayName = "slice: in heightmap mode the block colors are drawn (height-based color is indistinguishable at a fixed Y)")]
    public void SliceColor_HeightMode_StillShowsBlocks()
    {
        var reg = Registry((1, "rock-granite"), (2, "sand"));
        var tm = Terrain(reg, 4, 4, surface: 5);
        var vox = new FakeVoxels();
        vox.Set(0, 5, 0, 1);
        vox.Set(1, 5, 0, 2);
        tm.Voxels = vox;

        var a = TerrainColorizer.SliceColor(tm, 0, 5, 0, TerrainColorizer.ModeHeight, 1);
        var b = TerrainColorizer.SliceColor(tm, 1, 5, 0, TerrainColorizer.ModeHeight, 1);
        Assert.True(a.HasBlock && b.HasBlock);
        Assert.NotEqual(a.R, b.R);   // not a flat "height color" but the actual materials
    }

    [Fact(DisplayName = "slice: water is colored by depth — deeper is darker")]
    public void SliceColor_WaterDepth()
    {
        var reg = Registry((1, "water-still-7"), (2, "rock-granite"));
        var tm = Terrain(reg, 4, 4, surface: 10);
        var vox = new FakeVoxels();
        vox.Set(0, 10, 0, 1);   // the water surface
        vox.Set(0, 6, 0, 1);    // deeper
        tm.Voxels = vox;

        var shallow = TerrainColorizer.SliceColor(tm, 0, 10, 0, TerrainColorizer.ModeBlocks, 1);
        var deep = TerrainColorizer.SliceColor(tm, 0, 6, 0, TerrainColorizer.ModeBlocks, 1);
        Assert.True(shallow.HasBlock && deep.HasBlock);
        Assert.True(deep.R < shallow.R && deep.G < shallow.G && deep.B < shallow.B, "deeper means darker");
    }

    [Fact(DisplayName = "slice: without a voxel source the old \"the column is solid at this Y\" mask is used")]
    public void SliceColor_WithoutVoxels_FallsBackToMask()
    {
        var tm = Terrain(Registry((1, "rock-granite")), 4, 4, surface: 5);   // BottomY = 2
        // Voxels = null
        var inside = TerrainColorizer.SliceColor(tm, 0, 4, 0, TerrainColorizer.ModeBlocks, 1);
        var outside = TerrainColorizer.SliceColor(tm, 0, 9, 0, TerrainColorizer.ModeBlocks, 1);

        Assert.True(inside.HasBlock);
        Assert.Equal(((byte)150, (byte)150, (byte)30), (inside.R, inside.G, inside.B));
        Assert.True(outside.HasBlock);
        Assert.Equal(((byte)210, (byte)210, (byte)235), (outside.R, outside.G, outside.B));
    }

    [Fact(DisplayName = "slice: the voxel source exists but there is no chunk at this Y — that is the background (not the mask)")]
    public void SliceColor_MissingChunk_IsBackground()
    {
        var tm = Terrain(Registry((1, "rock-granite")), 4, 4, surface: 5);
        tm.Voxels = new FakeVoxels();   // empty source: there is not a single chunk
        Assert.False(TerrainColorizer.SliceColor(tm, 0, 5, 0, TerrainColorizer.ModeBlocks, 1).HasBlock);
        Assert.False(TerrainColorizer.SliceColor(tm, 0, 100, 0, TerrainColorizer.ModeBlocks, 1).HasBlock);
    }

    [Fact(DisplayName = "SaveVoxelSource: repeated lookups come from the cache and extra chunks are evicted")]
    public void VoxelCache_HitsAndEvicts()
    {
        int calls = 0;
        var src = new SaveVoxelSource(_ => { System.Threading.Interlocked.Increment(ref calls); return null; }, capacity: 2);

        src.GetChunkBlocks(0, 0, 0);
        src.GetChunkBlocks(0, 0, 0);
        Assert.Equal(1, calls);                        // the second time comes from the cache

        src.GetChunkBlocks(1, 0, 0);
        src.GetChunkBlocks(2, 0, 0);                   // chunk (0,0,0) was evicted
        Assert.True(src.CachedChunks <= 2, "the cache must not grow beyond its capacity");

        src.GetChunkBlocks(0, 0, 0);
        Assert.Equal(4, calls);                        // the first three calls plus a reload
        Assert.Equal(4, src.DecodedChunks);
        Assert.Equal(4, src.CacheMisses);
    }

    [Fact(DisplayName = "SaveVoxelSource: a read error (closed save) does not bring the render down")]
    public void VoxelCache_LoaderThrows_ReturnsNull()
    {
        var src = new SaveVoxelSource(_ => throw new InvalidOperationException("save disposed"));
        Assert.Null(src.GetChunkBlocks(3, 1, 4));
        Assert.Null(src.GetChunkBlocks(3, 1, 4));
    }

    [Fact(DisplayName = "image export: air at the Y level is transparent, a solid block is not")]
    public void Export_Slice_AirTransparent()
    {
        var reg = Registry((1, "rock-granite"));
        var tm = Terrain(reg, 4, 4, surface: 5);
        tm.TopBlock[0] = 1;
        var vox = new FakeVoxels();
        vox.Set(0, 5, 0, 1);    // only column (0,0) has a block at this level
        tm.Voxels = vox;

        var (w, h, px) = MapImageRenderer.Render(tm, new MapImageOptions
        {
            Scale = 1, Mode = TerrainColorizer.ModeBlocks, LayerY = 5, TransparentBackground = true, Hillshade = false
        });
        Assert.Equal((4, 4), (w, h));
        Assert.Equal(255, px[3]);        // (0,0) has a block
        Assert.Equal(0, px[7]);          // (1,0) is air at the level -> transparent
        _ = h;
        Assert.NotEqual(0, px[0] + px[1] + px[2]);
    }
}
