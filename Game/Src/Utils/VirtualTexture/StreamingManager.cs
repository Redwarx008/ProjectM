using Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectM;

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