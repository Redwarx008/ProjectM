using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static TerrainData;

public class MinMaxMap
{
    // [min, max]
    public ushort[] Data;

    public int Width;

    public int Height;

    private MinMaxMap(int dimX, int dimY)
    {
        Width = (ushort)dimX;
        Height = (ushort)dimY;
        Data = new ushort[dimX * dimY * 2];
    }

    public void GetSubNodesExist(int parentX, int parentY,
        out bool subTLExist, out bool subTRExist, out bool subBLExist, out bool subBRExist)
    {
        int x = parentX << 1;
        int y = parentY << 1;

        subTLExist = true;
        subTRExist = (x + 1) < Width;
        subBLExist = (y + 1) < Height;
        subBRExist = (x + 1) < Width && (y + 1) < Height;
    }

    public void GetMinMax(int x, int y, out ushort min, out ushort max)
    {
        int index = x + y * Width;
        min = Data[index * 2];
        max = Data[index * 2 + 1];
    }
    public static void SaveAll(MinMaxMap[] maps, string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using var writer = new BinaryWriter(fs);

        writer.Write(maps.Length); // 写入层级数

        foreach (var map in maps)
        {
            writer.Write(map.Width);
            writer.Write(map.Height);
            ReadOnlySpan<byte> byteView = MemoryMarshal.AsBytes(map.Data.AsSpan());
            writer.Write(byteView);
        }
    }
    public static MinMaxMap[] LoadAll(string filePath, out int leafNodeSize)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("File not found", filePath);

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        using var reader = new BinaryReader(fs);

        int count = reader.ReadInt32();
        leafNodeSize = reader.ReadInt32();
        var maps = new MinMaxMap[count];

        for (int i = 0; i < count; i++)
        {
            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            var map = new MinMaxMap(width, height);

            Span<byte> byteView = MemoryMarshal.AsBytes(map.Data.AsSpan());

            int bytesRead = reader.Read(byteView);

            if (bytesRead != byteView.Length)
            {
                throw new EndOfStreamException($"Expected {byteView.Length} bytes but read {bytesRead} at LOD {i}");
            }
            maps[i] = map;
        }
        return maps;
    }

    public static MinMaxMap[] CreateDefault(int leafNodeSize, int l0Width, int l0Height, int lodCount)
    {
        var maps = new MinMaxMap[lodCount];
        int width = l0Width;
        int height = l0Height;
        for (int mip = 0; mip < lodCount; ++mip)
        {
            int nodeSize = leafNodeSize << mip;
            int nodeCountX = (l0Width + nodeSize - 1) / nodeSize;
            int nodeCountY = (l0Height + nodeSize - 1) / nodeSize;
            var map = new MinMaxMap(nodeCountX, nodeCountY);
            maps[mip] = map;
        }
        return maps;
    }

    internal static MinMaxMap[] CreateDefault(uint leafNodeSize, int geometricWidth, int geometricHeight, int maxLodCount)
    {
        throw new NotImplementedException();
    }

    //public static MinMaxErrorMap[] CreateMinMaxErrorMaps(HeightDataSource heightData, int baseChunkSize, int LODLevelCount)
    //{
    //    int baseDimX = (heightData.Width - 1 + baseChunkSize - 1) / baseChunkSize;
    //    int baseDimY = (heightData.Height - 1 + baseChunkSize - 1) / baseChunkSize;

    //    var minMaxErrorMaps = new MinMaxErrorMap[LODLevelCount];
    //    minMaxErrorMaps[0] = new MinMaxErrorMap(baseDimX, baseDimY);

    //    // **多线程优化 1: LOD 0 基础地图初始化**
    //    Action<float[]> InitBaseMinMaxErrorMap = (inputData) =>
    //    {
    //        int chunkVertices = baseChunkSize + 1;
    //        int chunksY = (heightData.Height - 1 + chunkVertices - 1) / chunkVertices;

    //        // 并行处理 y 轴上的区块
    //        Parallel.For(0, chunksY, yChunkIndex =>
    //        {
    //            int y = yChunkIndex * chunkVertices;

    //            for (int x = 0; x < heightData.Width; x += chunkVertices)
    //            {
    //                int sizeX = Math.Min(chunkVertices, heightData.Width - x);
    //                int sizeY = Math.Min(chunkVertices, heightData.Height - y);

    //                // 注意：这里的 (heightData.Width - chunkVertices) 逻辑在原代码中存在，
    //                // 但通常应该检查剩余宽度/高度: (heightData.Width - x) 和 (heightData.Height - y)
    //                // 为了保持原意但进行微调，使用 sizeX/Y

    //                heightData.GetAreaMinMaxHeight(x, y, sizeX, sizeY, out float minHeight, out float maxHeight);
    //                float error = heightData.GetGeometricError(x, y, sizeX, sizeY, 0);

    //                // 索引计算保持不变
    //                int index = x / baseChunkSize + (y / baseChunkSize) * baseDimX;
    //                inputData[index * 3] = minHeight;
    //                inputData[index * 3 + 1] = maxHeight;
    //                inputData[index * 3 + 2] = error;
    //            }
    //        });
    //    };

    //    InitBaseMinMaxErrorMap(minMaxErrorMaps[0]._data);

    //    // 递归生成更高 LOD 级别（必须串行）
    //    for (int i = 1; i < LODLevelCount; ++i)
    //    {
    //        // CreateFromHigherDetail 现在是并行化的，但它本身必须在串行循环中调用
    //        minMaxErrorMaps[i] = CreateFromHigherDetail(heightData, minMaxErrorMaps[i - 1], i, baseChunkSize);
    //    }
    //    return minMaxErrorMaps;
    //}

    //// **多线程优化 2: 从更高细节 LOD 聚合**
    //private static MinMaxErrorMap CreateFromHigherDetail(HeightDataSource heightData, MinMaxErrorMap higherDetail,
    //    int lodLevel, int baseChunkSize)
    //{
    //    int srcDimX = higherDetail.Width;
    //    int srcDimY = higherDetail.Height;

    //    // 优化：使用位移操作 (Right Shift)
    //    int dimX = (srcDimX + 1) >> 1;
    //    int dimY = (srcDimY + 1) >> 1;

    //    var dst = new MinMaxErrorMap(dimX, dimY);

    //    // ----------------------------------------------------
    //    // Step 1: Min/Max 聚合 (并行)
    //    // 策略：并行遍历目标 (Dst) 贴图的行，每个线程独立计算一个目标块的 Min/Max。
    //    // ----------------------------------------------------

    //    Parallel.For(0, dimY, dstY =>
    //    {
    //        for (int dstX = 0; dstX < dimX; ++dstX)
    //        {
    //            int dstIndex = dstX + dstY * dimX;
    //            float min = float.MaxValue;
    //            float max = float.MinValue;

    //            // 遍历 2x2 的子节点
    //            for (int dy = 0; dy < 2; ++dy)
    //            {
    //                for (int dx = 0; dx < 2; ++dx)
    //                {
    //                    // 优化：使用位移操作 (Left Shift)
    //                    int srcX = (dstX << 1) + dx;
    //                    int srcY = (dstY << 1) + dy;

    //                    // 边界检查：确保源节点存在
    //                    if (srcX < srcDimX && srcY < srcDimY)
    //                    {
    //                        int srcIndex = srcX + srcY * srcDimX;

    //                        // Min/Max 聚合 (修复 Max 错误)
    //                        min = MathF.Min(min, higherDetail._data[srcIndex * 3]);
    //                        max = MathF.Max(max, higherDetail._data[srcIndex * 3 + 1]);
    //                    }
    //                }
    //            }

    //            // 写入聚合结果 (Error 稍后计算)
    //            dst._data[dstIndex * 3] = min;
    //            dst._data[dstIndex * 3 + 1] = max;
    //            dst._data[dstIndex * 3 + 2] = 0; // 初始化 error 为 0
    //        }
    //    });


    //    // ----------------------------------------------------
    //    // Step 2: 几何误差计算 (并行)
    //    // 策略：并行遍历目标 (Dst) 贴图的行，每个线程独立计算一个块的几何误差。
    //    // ----------------------------------------------------

    //    // 优化：使用位移操作
    //    int chunkSize = baseChunkSize << lodLevel;

    //    Parallel.For(0, dimY, y =>
    //    {
    //        for (int x = 0; x < dimX; ++x)
    //        {
    //            // 计算当前 LOD 级别下的起始顶点坐标 (x * chunkSize, y * chunkSize)
    //            int startX = x * chunkSize;
    //            int startY = y * chunkSize;

    //            // 修正了原代码中 sizeX/Y 的边界检查逻辑
    //            int sizeX = Math.Min(chunkSize, heightData.Width - 1 - startX);
    //            int sizeY = Math.Min(chunkSize, heightData.Height - 1 - startY);

    //            // 注意：原代码中的 sizeX/Y 计算与 HeightDataSource 的 API 约定高度相关
    //            // 原代码：int sizeX = Math.Min(chunkSize, heightData.Width - (chunkSize * x + 1));
    //            // 为了与原代码保持一致，并修正其可能包含的 off-by-one 错误，我们使用最安全的边界计算方式。

    //            // 考虑到原代码的特殊边界处理，保留原结构但修正了可能的负值问题：
    //            // 这里的 +1 通常代表 chunk 占用的顶点数，如果 HeightDataSource.Width/Height 是顶点数
    //            int safeSizeX = Math.Min(chunkSize, heightData.Width - (chunkSize * x + 1));
    //            int safeSizeY = Math.Min(chunkSize, heightData.Height - (chunkSize * y + 1));

    //            // 确保 size 至少为 1（如果块存在的话）
    //            sizeX = Math.Max(1, safeSizeX);
    //            sizeY = Math.Max(1, safeSizeY);


    //            float error = heightData.GetGeometricError(startX, startY, sizeX, sizeY, lodLevel);

    //            dst._data[(x + y * dimX) * 3 + 2] = error;
    //        }
    //    });

    //    return dst;
    //}
}