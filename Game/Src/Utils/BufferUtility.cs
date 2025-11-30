

using System;
using Godot;

public class GDBuffer : IDisposable
{
    public uint SizeBytes { get; private set; }
    public Rid Rid { get; private set; }
    private bool _managed = false;
    private GDBuffer()
    {
        
    }

    public static GDBuffer CreateStorage(uint sizeBytes, ReadOnlySpan<byte> data, bool useAsIndirect = false)
    {
        var buffer = new GDBuffer();
        RenderingDevice rd = RenderingServer.GetRenderingDevice();
        buffer.Rid = rd.StorageBufferCreate(sizeBytes, data, useAsIndirect == true ? RenderingDevice.StorageBufferUsage.Indirect : 0);
        buffer.SizeBytes = sizeBytes;
        buffer._managed = true;
        return buffer;
    }
    
    public static GDBuffer CreateStorage(uint sizeBytes, bool useAsIndirect = false)
    {
        RenderingDevice rd = RenderingServer.GetRenderingDevice();
        var buffer = new GDBuffer();
        buffer.Rid = rd.StorageBufferCreate(sizeBytes, null, useAsIndirect == true ? RenderingDevice.StorageBufferUsage.Indirect : 0);
        buffer.SizeBytes = sizeBytes;
        buffer._managed = true;
        return buffer;
    }

    public static GDBuffer CreateUniform(uint sizeBytes, ReadOnlySpan<byte> data)
    {
        RenderingDevice rd = RenderingServer.GetRenderingDevice();
        var buffer = new GDBuffer();
        buffer.Rid = rd.UniformBufferCreate(sizeBytes, data);
        buffer.SizeBytes = sizeBytes;
        buffer._managed = true;
        return buffer;
    }
    
    public static GDBuffer CreateUniform(uint sizeBytes)
    {
        RenderingDevice rd = RenderingServer.GetRenderingDevice();
        var buffer = new GDBuffer();
        buffer.Rid = rd.UniformBufferCreate(sizeBytes);
        buffer.SizeBytes = sizeBytes;
        buffer._managed = true;
        return buffer;
    }

    public static GDBuffer CreateManaged(Rid rid, uint sizeBytes = 0)
    {
        var buffer = new GDBuffer()
        {
            Rid = rid,
            SizeBytes = sizeBytes
        };
        buffer._managed = true;
        return buffer;
    }
    public static GDBuffer CreateUnmanaged(Rid rid, uint sizeBytes = 0)
    {
        var buffer = new GDBuffer()
        {
            Rid = rid,
            SizeBytes = sizeBytes
        };
        return buffer;
    }
    
    public void Dispose()
    {
        if (!_managed) return;
        RenderingDevice rd = RenderingServer.GetRenderingDevice();
        rd.FreeRid(Rid);
        GC.SuppressFinalize(this);
    }
    
    public static implicit operator Rid(GDBuffer buffer)
    {
        return buffer.Rid;  
    }
    
    // public static implicit operator GDBuffer(Rid buffer)
    // {
    //     return GDBuffer.CreateUnmanaged(buffer);
    // }
}
