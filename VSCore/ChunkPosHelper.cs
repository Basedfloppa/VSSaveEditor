namespace VSCore;

/// <summary>Chunk position encoding in `chunk.position` (decoded from Vintagestory.Common.Database/ChunkPos, Q2).</summary>
public static class ChunkPosHelper
{
    public const uint ChunkMask = 0x3FFFFF; // 22 bits
    public const uint YMask = 511;          // 9 bits

    /// <summary>Decode chunk index -> (X, Y, Z) chunk coordinates (Overworld, dim 0).</summary>
    public static (int X, int Y, int Z) DecodeIndex(ulong index)
    {
        int x = (int)(index & ChunkMask);
        int z = (int)((index >> 27) & ChunkMask);
        int y = (int)((index >> 54) & YMask);
        return (x, y, z);
    }

    public static ulong ToIndex(int x, int y, int z)
        => ((uint)x) | ((ulong)(uint)z << 27) | ((ulong)(uint)y << 54);

    /// <summary>Local block index inside a chunk: index = x + z*32 + y*1024.</summary>
    public static int Index3D(int x, int y, int z) => x + z * 32 + y * 1024;
    public static (int x, int y, int z) Unpack(int index3d)
        => (index3d % 32, index3d / 1024, (index3d / 32) % 32);
}
