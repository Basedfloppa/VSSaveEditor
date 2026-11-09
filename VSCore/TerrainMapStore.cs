using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ZstdSharp;

namespace VSCore;

/// <summary>
///     Binary form of one built terrain tile, for the on-disk tile cache.
///     The point is to stop paying the build twice: probing the chunks of a tile costs 1.5–3.5 s, while writing
///     it out and reading it back costs a fraction of that. What is stored is exactly the display data —
///     the live objects (block registry, on-demand voxel source) are re-attached on load and are not written.
///     The format is versioned: a cache written by another build is a miss, never a parse of garbage.
/// </summary>
public static class TerrainMapStore
{
    /// <summary>Bumped whenever the layout below changes: an old cache file is then simply a miss.</summary>
    public const int FormatVersion = 1;

    /// <summary>Refuse absurd sizes before allocating: a tile that would need more than this is not cached
    /// (the real ones are 10–16M columns, that is ~180 MB raw).</summary>
    private const long MaxPayload = 1_500_000_000L;

    internal static readonly byte[] Magic = Encoding.ASCII.GetBytes("VSMAP\0");

    /// <summary>Writes the tile. The stream is left open (the cache writes through a temp file).</summary>
    public static void Write(Stream stream, TerrainMap tm)
    {
        long size = PayloadSize(tm);
        if (size > MaxPayload)
            throw new InvalidOperationException($"map tile too large to cache: {tm.Width}x{tm.Height} ({size} B)");

        var payload = new byte[size];
        using (var bw = new BinaryWriter(new MemoryStream(payload, 0, payload.Length, true), Encoding.UTF8))
        {
            bw.Write(tm.MinX);
            bw.Write(tm.MinZ);
            bw.Write(tm.Width);
            bw.Write(tm.Height);
            bw.Write((int)tm.Needs);
            bw.Write(tm.MaxSurface);
            bw.Write(tm.MinSurface);
            bw.Write(tm.MinWorldY);
            bw.Write(tm.MaxWorldY);
            bw.Write(tm.YearRel);
            bw.Write(tm.SeaLevel);

            WriteUshorts(bw, tm.SurfaceY);
            WriteUshorts(bw, tm.BottomY);
            WriteUshorts(bw, tm.TopBlock);
            WriteUshorts(bw, tm.MapY);
            WriteUshorts(bw, tm.MapBlock);
            WriteInts(bw, tm.WaterDepth);
            WriteBytes(bw, tm.Shadow);

            if (tm.Pyramid is { } py)
            {
                bw.Write(1);
                bw.Write(py.MinX);
                bw.Write(py.MinZ);
                bw.Write(py.Width);
                bw.Write(py.Height);
                bw.Write(py.CellSize);
                bw.Write(py.Mode);
                bw.Write(py.Pixels.Length);
                bw.Write(py.Pixels);
            }
            else
            {
                bw.Write(0);
            }

            bw.Write(tm.PresentChunks.Count);
            foreach (var (cx, cz) in tm.PresentChunks)
            {
                bw.Write(cx);
                bw.Write(cz);
            }

            bw.Write(tm.ChiselMaterial.Count);
            foreach (var kv in tm.ChiselMaterial)
            {
                bw.Write(kv.Key);
                bw.Write(kv.Value);
            }
        }

        byte[] compressed;
        // Level 1: the height/block arrays are smooth, so most of the win is already there, and the write
        // happens on the background warm path where CPU time is the scarce resource.
        using (var c = new Compressor(1)) compressed = c.Wrap(payload).ToArray();

        var header = new BinaryWriter(stream, Encoding.UTF8, true);
        header.Write(Magic);
        header.Write(FormatVersion);
        header.Write(payload.Length);
        header.Write(compressed.Length);
        header.Write(compressed);
        header.Flush();
    }

    /// <summary>Reads a tile back. Returns null for a foreign/corrupt/truncated file — the caller treats that
    /// as a cache miss and builds the tile, so a bad cache can never break opening a save.</summary>
    public static TerrainMap? Read(Stream stream, VSSaveFile? save, ChunkArea? area)
    {
        try
        {
            var br = new BinaryReader(stream, Encoding.UTF8, true);
            var magic = br.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length) return null;
            for (int i = 0; i < Magic.Length; i++) if (magic[i] != Magic[i]) return null;
            if (br.ReadInt32() != FormatVersion) return null;
            int rawLen = br.ReadInt32(), compLen = br.ReadInt32();
            if (rawLen <= 0 || compLen <= 0 || rawLen > MaxPayload || compLen > MaxPayload) return null;
            var compressed = br.ReadBytes(compLen);
            if (compressed.Length != compLen) return null;

            var payload = new byte[rawLen];
            using (var d = new Decompressor())
            {
                int n = d.Unwrap(compressed, payload);
                if (n != rawLen) return null;
            }

            using var ms = new MemoryStream(payload, 0, payload.Length, false);
            using var p = new BinaryReader(ms, Encoding.UTF8);
            int minX = p.ReadInt32(), minZ = p.ReadInt32(), w = p.ReadInt32(), h = p.ReadInt32();
            if (w <= 0 || h <= 0) return null;
            TerrainMap.EnsureAreaFits(w, h);
            int cells = checked(w * h);

            int needsRaw = p.ReadInt32();
            if ((needsRaw & ~(int)TerrainNeeds.All) != 0) return null;   // unknown flags: another build's format
            var needs = (TerrainNeeds)needsRaw;

            var tm = TerrainMap.Allocate(minX, minZ, w, h, needs);
            tm.MaxSurface = p.ReadInt32();
            tm.MinSurface = p.ReadInt32();
            tm.MinWorldY = p.ReadInt32();
            tm.MaxWorldY = p.ReadInt32();
            tm.YearRel = p.ReadDouble();
            tm.SeaLevel = p.ReadInt32();

            tm.SurfaceY = ReadUshorts(p, cells) ?? throw new InvalidDataException("SurfaceY");
            tm.BottomY = ReadUshorts(p, cells) ?? throw new InvalidDataException("BottomY");
            tm.TopBlock = ReadUshorts(p, cells) ?? throw new InvalidDataException("TopBlock");
            // Optional arrays must be present exactly when the render mode asked for them; anything else
            // means the file does not belong to this map and the map would silently draw the wrong thing.
            var mapY = ReadUshorts(p, cells);
            var mapBlock = ReadUshorts(p, cells);
            var water = ReadInts(p, cells);
            var shadow = ReadBytes(p, cells);
            if ((tm.MapY is not null) != (mapY is not null)) return null;
            if ((tm.WaterDepth is not null) != (water is not null)) return null;
            tm.MapY = mapY;
            tm.MapBlock = mapBlock;
            tm.WaterDepth = water;
            tm.Shadow = shadow;

            if (p.ReadInt32() == 1)
            {
                int pxMinX = p.ReadInt32(), pxMinZ = p.ReadInt32();
                int pw = p.ReadInt32(), ph = p.ReadInt32(), cellSize = p.ReadInt32(), mode = p.ReadInt32();
                int pxLen = p.ReadInt32();
                if (pw <= 0 || ph <= 0 || pxLen != pw * ph * 4) return null;
                var pixels = p.ReadBytes(pxLen);
                if (pixels.Length != pxLen) return null;
                tm.SetPyramid(TerrainPyramid.Restore(pxMinX, pxMinZ, pw, ph, cellSize, mode, pixels));
            }

            int presentCount = p.ReadInt32();
            if (presentCount < 0 || presentCount > cells + 1_000_000) return null;
            for (int i = 0; i < presentCount; i++) tm.PresentChunks.Add((p.ReadInt32(), p.ReadInt32()));

            int chiselCount = p.ReadInt32();
            if (chiselCount < 0 || chiselCount > cells) return null;
            for (int i = 0; i < chiselCount; i++) tm.ChiselMaterial[p.ReadInt64()] = p.ReadInt32();

            if (save is not null) tm.Attach(save, area);   // null only in tests: the live objects stay unbound
            return tm;
        }
        catch (Exception ex)
        {
            // A half-written or foreign file is a miss, not an error path: the tile is simply rebuilt.
            // The line is still printed — a cache that always misses must not look like a cache hit.
            ScanDiagnostics.Warn("map tile cache read failed (rebuilding the tile)", ex);
            return null;
        }
    }

    private static long PayloadSize(TerrainMap tm)
    {
        int cells = tm.Width * tm.Height;
        long size = 4 * 4 + 4 + 4 * 4 + 8 + 4;             // window, needs, relief/Y bounds, year, sea level
        size += WithCount(cells * 2L) * 3;                  // surface / bottom / top block
        size += tm.MapY is null ? 4 : WithCount(cells * 2L);
        size += tm.MapBlock is null ? 4 : WithCount(cells * 2L);
        size += tm.WaterDepth is null ? 4 : WithCount(cells * 4L);
        size += tm.Shadow is null ? 4 : WithCount(tm.Shadow.Length);
        size += 4;                                          // pyramid present flag
        if (tm.Pyramid is { } py) size += 6 * 4 + 4 + py.Pixels.Length;
        size += 4 + tm.PresentChunks.Count * 8L;
        size += 4 + tm.ChiselMaterial.Count * 12L;
        return size;
    }

    private static long WithCount(long bytes) => 4 + bytes;

    private static void WriteUshorts(BinaryWriter bw, ushort[]? a)
    {
        if (a is null) { bw.Write(-1); return; }
        bw.Write(a.Length);
        bw.Write(MemoryMarshal.AsBytes(a.AsSpan()));
    }

    private static void WriteInts(BinaryWriter bw, int[]? a)
    {
        if (a is null) { bw.Write(-1); return; }
        bw.Write(a.Length);
        bw.Write(MemoryMarshal.AsBytes(a.AsSpan()));
    }

    private static void WriteBytes(BinaryWriter bw, byte[]? a)
    {
        if (a is null) { bw.Write(-1); return; }
        bw.Write(a.Length);
        bw.Write(a);
    }

    private static ushort[]? ReadUshorts(BinaryReader br, int cells)
    {
        int n = br.ReadInt32();
        if (n == -1) return null;
        if (n != cells) throw new InvalidDataException($"ushort[] length {n} != {cells}");
        return ReadRaw<ushort>(br, n);
    }

    private static int[]? ReadInts(BinaryReader br, int cells)
    {
        int n = br.ReadInt32();
        if (n == -1) return null;
        if (n != cells) throw new InvalidDataException($"int[] length {n} != {cells}");
        return ReadRaw<int>(br, n);
    }

    private static byte[]? ReadBytes(BinaryReader br, int cells)
    {
        int n = br.ReadInt32();
        if (n == -1) return null;
        var a = br.ReadBytes(n);
        if (a.Length != n) throw new InvalidDataException("short byte[]");
        return a;
    }

    private static T[] ReadRaw<T>(BinaryReader br, int n) where T : unmanaged
    {
        int bytes = checked(n * System.Runtime.CompilerServices.Unsafe.SizeOf<T>());
        var raw = br.ReadBytes(bytes);
        if (raw.Length != bytes) throw new InvalidDataException("short array");
        return MemoryMarshal.Cast<byte, T>(raw.AsSpan()).ToArray();
    }
}
