using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectM;

internal sealed class PageBufferOwner : IMemoryOwner<byte>
{
    private PageBufferAllocator? _allocator;
    private byte[]? _buffer;

    public PageBufferOwner(PageBufferAllocator allocator, byte[] buffer)
    {
        _allocator = allocator;
        _buffer = buffer;
    }

    public Memory<byte> Memory => _buffer!; 

    public void Dispose()
    {
        if (_buffer != null)
        {
            _allocator!.Return(_buffer);
            _buffer = null;
            _allocator = null;
        }
    }
}
internal sealed class PageBufferAllocator : IDisposable
{
    private readonly int _bytesPerPage;
    private readonly ConcurrentBag<byte[]> _pool = [];
    private readonly int _maxCount;
    private int _allocated;

    public PageBufferAllocator(int bytesPerPage, int maxCount)
    {
        _bytesPerPage = bytesPerPage;
        _maxCount = maxCount;
    }

    public IMemoryOwner<byte> Rent()
    {
        if (_pool.TryTake(out var buffer))
        {
            return new PageBufferOwner(this, buffer);
        }

        if (Interlocked.Increment(ref _allocated) <= _maxCount)
        {
            return new PageBufferOwner(this, new byte[_bytesPerPage]);
        }

        Interlocked.Decrement(ref _allocated);
        throw new OutOfMemoryException("[VT] Page buffer pool exhausted.");
    }

    internal void Return(byte[] buffer)
    {
        _pool.Add(buffer);
    }

    public void Dispose()
    {
        _pool.Clear();
    }
}