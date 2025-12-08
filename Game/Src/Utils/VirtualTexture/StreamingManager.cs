using Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectM;

internal struct VTInfo
{
    public int width;
    public int height;
    public int tileSize;
    public int padding;
    public int mipmaps;
}
internal class StreamingManager : IDisposable
{
    // 文件路径 -> 加载器实例
    private List<PageLoader> _loaders = [];

    private ConcurrentQueue<(int textureId, VirtualPageID id, byte[] data)> _loadedQueue = new();

    private bool _isRunning = true;
    private Thread _ioThread;

    private BlockingCollection<VirtualPageID> _requestQueue = [];

    public StreamingManager()
    {
        // 启动后台 I/O 线程
        _ioThread = new Thread(IOThreadLoop)
        {
            IsBackground = true
        };
        _ioThread.Start();
    }

    public void RegisterFile(string filePath)
    {
        _loaders.Add(new PageLoader(filePath));
    }

    public void RequestPage(VirtualPageID id)
    {
        _requestQueue.Add(id);
    }

    public VTInfo GetVTInfo()
    {
        Debug.Assert(_loaders.Count > 0);
        for (int i = 1; i < _loaders.Count; ++i)
        {
            if (_loaders[i].Header.Width != _loaders[i - 1].Header.Width || _loaders[i].Header.Height != _loaders[i - 1].Header.Height)
            {
                Logger.Error($"[VT] : The width or height of virtual texture ({_loaders[i].FilePath}) are different from virtual texture ({_loaders[i - 1].FilePath}).\r\n");
            }

            if (_loaders[i].Header.Mipmaps != _loaders[i - 1].Header.Mipmaps)
            {
                Logger.Error($"[VT] : The mipmaps virtual texture ({_loaders[i].FilePath}) are different from virtual texture ({_loaders[i - 1].FilePath}).\r\n");
            }
        }
        return new VTInfo()
        {
            width = _loaders[0].Header.Width,
            height = _loaders[0].Header.Height,
            tileSize = _loaders[0].Header.TileSize,
            mipmaps = _loaders[0].Header.Mipmaps,
            padding = _loaders[0].Header.Padding,
        };
    }

    private void IOThreadLoop()
    {
        while (_isRunning)
        {
            try
            {
                // 阻塞直到有请求
                VirtualPageID req = _requestQueue.Take();

                for(int i = 0; i < _loaders.Count; ++i)
                {
                    // 执行读取 (LoadPage 内部有文件锁，是安全的)
                    byte[]? data = _loaders[i].LoadPage(req.mip, req.x, req.y);

                    if (data != null)
                    {
                        _loadedQueue.Enqueue((i, req, data));
                    }
                    else
                    {
                        // 处理读取失败（越界或文件错误）
                        // 实际项目中可能需要生成一个空的 fallback 数据
                    }
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

    public bool TryGetLoadedPage(out (int textureId, VirtualPageID id, byte[] data) result)
    {
        return _loadedQueue.TryDequeue(out result);
    }

    public void Dispose()
    {
        _isRunning = false;
        _requestQueue.CompleteAdding();

        foreach (var loader in _loaders)
        {
            loader.Dispose();
        }
        _loaders.Clear();
    }
}