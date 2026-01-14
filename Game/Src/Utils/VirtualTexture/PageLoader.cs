using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ProjectM;

internal class PageLoader : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct VTHeader
    {
        public int Width;
        public int Height;
        public int TileSize;    // T (不含 Padding 的大小)
        public int Padding;     // P
        public int BytesPerPixel;
        public int Mipmaps;
    }

    private readonly SafeFileHandle _fileHandle; 
    private readonly long _fileLength;

    // 元数据

    public VTHeader Header => _header;

    private VTHeader _header;

    public string FilePath { get; init; }

    // 每一层 Mip 在文件中的起始字节偏移量
    private long[]? _mipLevelFileOffsets;

    // 每一层 Mip 在 X 轴方向的图块数量 (用于计算索引)
    private int[]? _mipLevelTilesX;

    // 单个图块（包含 Padding）的字节大小
    private int _bytesPerTile;
    private int _paddedSize;

    public int PaddedTileSize => _paddedSize;
    public int RawTileSize => _bytesPerTile;

    private PageBufferAllocator _bufferAllocator;

    public PageLoader(string filePath, int maxPageLoadedCount)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"VT file not found: {filePath}");

        FilePath = filePath;
        _fileHandle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        _fileLength = RandomAccess.GetLength(_fileHandle);
        ReadHeaderAndPrecalculateOffsets();
        _bufferAllocator = new PageBufferAllocator(_bytesPerTile, 999);
    }

    private void ReadHeaderAndPrecalculateOffsets()
    {
        // 1. 读取 Header
        int headerSize = Marshal.SizeOf<VTHeader>();
        Span<byte> byteView = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref _header, 1));
        RandomAccess.Read(_fileHandle, byteView, 0);

        // 2. 准备预计算数据
        _mipLevelFileOffsets = new long[Header.Mipmaps];
        _mipLevelTilesX = new int[Header.Mipmaps];

        _paddedSize = Header.TileSize + Header.Padding * 2;
        _bytesPerTile = _paddedSize * _paddedSize * Header.BytesPerPixel;

        // 3. 模拟写入过程，计算偏移量
        long currentOffset = headerSize; // 从 Header 之后开始

        int currentW = Header.Width;
        int currentH = Header.Height;

        for (int level = 0; level < Header.Mipmaps; level++)
        {
            _mipLevelFileOffsets[level] = currentOffset;

            // 计算当前层级的图块数量 (逻辑必须与 VTProcessor 完全一致)
            int nTilesX = (int)Math.Ceiling(currentW / (float)Header.TileSize);
            int nTilesY = (int)Math.Ceiling(currentH / (float)Header.TileSize);

            _mipLevelTilesX[level] = nTilesX;

            // 下一个 Mip Level 的起始位置 = 当前位置 + (图块总数 * 单个图块大小)
            long levelSize = (long)nTilesX * nTilesY * _bytesPerTile;
            currentOffset += levelSize;

            // 计算下一级 Mip 的尺寸
            if (level < Header.Mipmaps - 1)
            {
                currentW = Math.Max(1, (int)Math.Ceiling(currentW * 0.5));
                currentH = Math.Max(1, (int)Math.Ceiling(currentH * 0.5));
            }
        }
    }

    /// <summary>
    /// 读取指定的图块数据。
    /// 该方法是线程安全的。
    /// </summary>
    /// <param name="mip">Mip Level</param>
    /// <param name="x">Tile X Index</param>
    /// <param name="y">Tile Y Index</param>
    /// <returns>包含像素数据的字节数组，如果索引无效返回 null</returns>
    public IMemoryOwner<byte>? LoadPage(int mip, int x, int y)
    {
        Debug.Assert(_mipLevelTilesX != null);
        Debug.Assert(_mipLevelFileOffsets != null);
        // 1. 基础校验
        if (mip < 0 || mip >= Header.Mipmaps) return null;
        if (x < 0 || x >= _mipLevelTilesX[mip]) return null;

        // Y 轴校验需要重新计算该层的 TilesY，或者仅仅依赖文件长度保护
        // 为了性能，通常假设调用者逻辑正确，或者在这里额外计算 TilesY
        // int currentH = ... (如同构造函数里的计算);
        // int nTilesY = (int)Math.Ceiling(currentH / (float)Header.TileSize);
        // if (y >= nTilesY) return null;

        // 2. 计算文件偏移
        // Offset = MipBase + (Row * Stride + Col) * TileSize
        long mipBaseOffset = _mipLevelFileOffsets[mip];
        int tilesPerRow = _mipLevelTilesX[mip];

        long tileIndex = (long)y * tilesPerRow + x;
        long absoluteOffset = mipBaseOffset + tileIndex * _bytesPerTile;

        if (absoluteOffset + _bytesPerTile > _fileLength) return null;

        var owner = _bufferAllocator.Rent();
        var memory = owner.Memory;
        Debug.Assert(memory.Length == _bytesPerTile);
        int bytesRead = RandomAccess.Read(_fileHandle, memory.Span, absoluteOffset);

        if (bytesRead != _bytesPerTile)
        {
            owner.Dispose();
            return null;
        }
        return owner;// 由调用方 Dispose
    }

    public void Dispose()
    {
        _fileHandle?.Dispose();
    }
}