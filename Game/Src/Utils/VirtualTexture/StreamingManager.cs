using Core;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Godot.HttpRequest;

namespace ProjectM;

internal class StreamingManager : IDisposable
{
    // 文件路径 -> 加载器实例
    private PageLoader[] _loaders;

    private ConcurrentQueue<(VirtualPageID id, IMemoryOwner<byte>? data)> _loadedQueue = new();

    private bool _isRunning = true;
    private Thread _ioThread;

    private BlockingCollection<VirtualPageID> _requestQueue = [];

    private (VirtualPageID id, IMemoryOwner<byte>? data)[] _stageDatas;

    public event Action<Span<(VirtualPageID id, IMemoryOwner<byte>? data)>> LoadComplete = null!;

    public StreamingManager(ReadOnlySpan<VirtualTextureDesc> vtDesc)
    {
        _ioThread = new Thread(IOThreadLoop)
        {
            IsBackground = true
        };
        _ioThread.Start();
        _loaders= new PageLoader[vtDesc.Length];
        _stageDatas = new (VirtualPageID id, IMemoryOwner<byte>? data)[vtDesc.Length];
        for (int i = 0; i < vtDesc.Length; ++i)
        {
            _loaders[i] = new PageLoader(vtDesc[i].filePath);
        }
    }

    public void RequestPage(VirtualPageID id)
    {
        _requestQueue.Add(id);
    }

    public PageLoader.VTHeader GetVTHeader()
    {
        for (int i = 1; i < _loaders.Length; ++i)
        {
            if (_loaders[i].Header.width != _loaders[i - 1].Header.width || _loaders[i].Header.height != _loaders[i - 1].Header.height)
            {
                Logger.Error($"[VT] : The width or height of virtual texture ({_loaders[i].FilePath}) are different from virtual texture ({_loaders[i - 1].FilePath}).\r\n");
            }

            if (_loaders[i].Header.mipmaps != _loaders[i - 1].Header.mipmaps)
            {
                Logger.Error($"[VT] : The mipmaps virtual texture ({_loaders[i].FilePath}) are different from virtual texture ({_loaders[i - 1].FilePath}).\r\n");
            }
        }
        return _loaders[0].Header;
    }

    private void IOThreadLoop()
    {
        while (_isRunning)
        {
            try
            {
                VirtualPageID req = _requestQueue.Take();

                for(int i = 0; i < _loaders.Length; ++i)
                {
                    var data = _loaders[i].LoadPage(req.mip, req.x, req.y);

                    _loadedQueue.Enqueue((req, data));
                    Debug.WriteLineIf(data is null, $"[VT] Load failed for page {req})");
                }

            }
            catch (InvalidOperationException)
            {
                // Collection completed
                break;
            }
            catch (Exception e)
            {
                Logger.Error($"[IO Thread] Error: {e.Message}");
            }
        }
    }

    public void Update()
    {
        int batchSize = _loaders.Length;

        while (_loadedQueue.Count >= batchSize)
        {
            for (int i = 0; i < batchSize; i++)
            {
                if (_loadedQueue.TryDequeue(out var item))
                {
                    _stageDatas[i] = item;
                }
            }

#if DEBUG
            var firstId = _stageDatas[0].id;
            if (_stageDatas[batchSize - 1].id != firstId)
            {
                Logger.Error($"[StreamingManager] Batch Mismatch! Expected {_stageDatas[0].id} but got {_stageDatas[batchSize - 1].id}");
            }
#endif

            LoadComplete.Invoke(_stageDatas.AsSpan());
        }
    }

    public void Dispose()
    {
        _isRunning = false;
        _requestQueue.CompleteAdding();

        foreach (var loader in _loaders)
        {
            loader.Dispose();
        }
    }
}