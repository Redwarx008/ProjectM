using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectM;

internal sealed unsafe class NativePageBufferOwner : MemoryManager<byte>
{
    private PageBufferAllocator? _allocator;
    private byte* _ptr;
    private readonly int _length;

    public NativePageBufferOwner(PageBufferAllocator allocator, byte* ptr, int length)
    {
        _allocator = allocator;
        _ptr = ptr;
        _length = length;
    }

    public override Span<byte> GetSpan()
    {
        if (_ptr == null)
            throw new ObjectDisposedException(nameof(NativePageBufferOwner));

        return new Span<byte>(_ptr, _length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if ((uint)elementIndex > (uint)_length)
            throw new ArgumentOutOfRangeException(nameof(elementIndex));

        return new MemoryHandle(_ptr + elementIndex);
    }

    public override void Unpin()
    {
        // no-op (unmanaged memory)
    }

    protected override void Dispose(bool disposing)
    {
        if (_ptr != null)
        {
            _allocator!.Return(_ptr);
            _ptr = null;
            _allocator = null;
        }
    }
}


internal sealed unsafe class PageBufferAllocator : IDisposable
{
    private readonly int _bytesPerPage;
    private readonly int _maxCount;

    private readonly ConcurrentBag<nint> _pool = new();
    private int _allocated;

    public PageBufferAllocator(int bytesPerPage, int maxCount)
    {
        _bytesPerPage = bytesPerPage;
        _maxCount = maxCount;
    }

    public IMemoryOwner<byte> Rent()
    {
        if (_pool.TryTake(out var p))
        {
            return new NativePageBufferOwner(this, (byte*)p, _bytesPerPage);
        }

        if (Interlocked.Increment(ref _allocated) <= _maxCount)
        {
            nint mem = (nint)NativeMemory.Alloc((nuint)_bytesPerPage);
            return new NativePageBufferOwner(this, (byte*)mem, _bytesPerPage);
        }

        Interlocked.Decrement(ref _allocated);
        throw new OutOfMemoryException();
    }

    internal void Return(byte* ptr)
    {
        _pool.Add((nint)ptr);
    }

    public void Dispose()
    {
        while (_pool.TryTake(out var p))
        {
            NativeMemory.Free((void*)p);
        }
    }
}