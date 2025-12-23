using Core;
using Godot;
using ProjectM;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using NodeSelectedInfo = TerrainQuadTree.NodeSelectedInfo;

internal class TerrainProcessor : IDisposable
{
    #region Compute Resources

    private GDBuffer _drawIndirectBuffer = null!;
    private GDBuffer _nodeSelectedInfoBuffer = null!;
    private GDBuffer _instancedParamsBuffer = null!;
    private GDBuffer _terrainParamsBuffer = null!;

    private Rid _computePipeline;
    private Rid _computeSet;
    private Rid _computeShader;
    
    #endregion

    private int _maxLodNodeX;
    private int _maxLodNodeY;
    private int _leafNodeX;
    private int _leafNodeY;
    private int _lodCount;

    private readonly Callable _cachedDispatchCallback;

    private Terrain? _terrain;

    private TerrainQuadTree? _quadTree;

    private TerrainMesh? _planeMesh;

    private VirtualTexture? _geometricVT; // height or normal

    private NodeSelectedInfo[] _nodeSelectedInfos => _backSelected;

    private NodeSelectedInfo[] _frontSelected = new NodeSelectedInfo[Constants.MaxNodeInSelect];
    private NodeSelectedInfo[] _backSelected = new NodeSelectedInfo[Constants.MaxNodeInSelect];

    // 保护交换操作的锁（仅在主线程使用，但仍保守使用）
    private readonly object _bufferLock = new();

    // 渲染线程将在 callback 中读取这两个引用（volatile 保证引用的原子读取/写入可见性）
    private volatile NodeSelectedInfo[]? _dispatchSelected;

    private volatile int _dispatchSelectedCount;

    public float TolerableError { get; set; }

    public bool Inited { get; private set; } = false;

    #region Data struct Definition

    [StructLayout(LayoutKind.Sequential)]
    private struct NodeDescriptor
    {
        public uint Branch; // Indicates whether this quadtree node is divided
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TerrainParams
    {
        public uint LeafNodeSize;
        private uint _padding0;
        private uint _padding1;
        private uint _padding2;
    }

    #endregion

    public TerrainProcessor(Terrain terrain, TerrainMesh? mesh, MapDefinition definition)
    {
        _terrain = terrain;
        _planeMesh = mesh;
        _geometricVT = terrain.GeometricVT;
        TolerableError = definition.TerrainTolerableError;
        _cachedDispatchCallback = Callable.From(() =>
        {
            // 渲染线程执行：读取 stable front
            var sel = _dispatchSelected;
            var selN = _dispatchSelectedCount;

            // **永远只读，不写，不改数组内容**
            if (sel != null)
                Dispatch(sel, selN);
        });
        CalcLodParameters(terrain.Data.GeometricWidth, terrain.Data.GeometricHeight, (int)terrain.LeafNodeSize);
        CreateQuadTree(terrain, definition);
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            InitPipeline(terrain, definition);
            Inited = true;
        }));
    }
    public void Process(Camera3D camera)
    {
        Debug.Assert(_quadTree != null);
        // todo: 自己计算Frustum避免堆分配造成GC压力
        ReadOnlySpan<Plane> planes = camera.GetFrustum().ToArray();
        var selectDesc = new TerrainQuadTree.SelectDesc
        {
            nodeSelectedInfos = _nodeSelectedInfos,
            planes = planes,
            viewerPos = camera.GlobalPosition,
            tolerableError = TolerableError,
            nodeSelectedCount = 0,
        };
        _quadTree.Select(ref selectDesc);
        UpdateVirtualTexture(selectDesc.nodeSelectedInfos, selectDesc.nodeSelectedCount);
        SubmitForRender(selectDesc.nodeSelectedCount);
    }

    // 交换并提交给渲染线程（仅在主线程调用）
    private void SubmitForRender(int selectedCount)
    {
        // 锁住交换 —— 确保可见性与原子性
        lock (_bufferLock)
        {
            // 交换 Selected
            (_frontSelected, _backSelected) = (_backSelected, _frontSelected);

            // 设置渲染线程可读的稳定 front 引用（volatile 写）
            _dispatchSelected = _frontSelected;
            _dispatchSelectedCount = selectedCount;
        }

        RenderingServer.CallOnRenderThread(_cachedDispatchCallback);
    }
    private void Dispatch(
        NodeSelectedInfo[] selected, int selectedCount)
    {
        RenderingDevice rd = RenderingServer.GetRenderingDevice();

        rd.BufferClear(_drawIndirectBuffer, 4, 4);

        unsafe
        {
            ReadOnlySpan<byte> counterBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref selectedCount, sizeof(int)));
            rd.BufferUpdate(_nodeSelectedInfoBuffer, 0, sizeof(int), counterBytes);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(selected.AsSpan(0, selectedCount));
            rd.BufferUpdate(_nodeSelectedInfoBuffer, 8, (uint)(sizeof(NodeSelectedInfo) * selectedCount), bytes);
        }

        // set draw info pass
        
        if (selectedCount > 0)
        {
            var computeList = rd.ComputeListBegin();
            rd.ComputeListBindComputePipeline(computeList, _computePipeline);
            rd.ComputeListBindUniformSet(computeList, _computeSet, 0);
            rd.ComputeListDispatch(computeList, (uint)(Math.Ceiling(selectedCount / 64f)), 1, 1);
            rd.ComputeListEnd();
        }
    }

    private void CalcLodParameters(int mapRasterSizeX, int mapRasterSizeY, int leafNodeSize)
    {
        int minLength = Math.Min(mapRasterSizeX, mapRasterSizeY);
        _lodCount = (int)Math.Log2(minLength) - (int)Math.Log2(leafNodeSize);
        int topNodeSize = (int)(leafNodeSize * (int)Math.Pow(2, _lodCount - 1));
        _maxLodNodeX = (int)Math.Ceiling(mapRasterSizeX / (float)topNodeSize);
        _maxLodNodeY = (int)Math.Ceiling(mapRasterSizeY / (float)topNodeSize);
        _leafNodeX = (int)MathF.Ceiling((mapRasterSizeX - 1) / (float)leafNodeSize);
        _leafNodeY = (int)MathF.Ceiling((mapRasterSizeY - 1) / (float)leafNodeSize);
    }
    
    private void InitPipeline(Terrain terrain, MapDefinition definition)
    {   
        Debug.Assert(_planeMesh != null);
        Debug.Assert(terrain.Data.Heightmap != null);

        GDTexture2D heightmap = terrain.Data.Heightmap;
        _instancedParamsBuffer = GDBuffer.CreateManaged(_planeMesh.GetInstanceBuffer());
        _drawIndirectBuffer = GDBuffer.CreateManaged(_planeMesh.GetDrawIndirectBuffer());

        RenderingDevice rd = RenderingServer.GetRenderingDevice();

        uint meshIndexCount = terrain.LeafNodeSize * terrain.LeafNodeSize * 6;
        ReadOnlySpan<byte> indexCount = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref meshIndexCount, 1));
        rd.BufferUpdate(_drawIndirectBuffer, 0, sizeof(uint), indexCount);

        // create Buffers

        var terrainParams = new TerrainParams()
        {
            LeafNodeSize = terrain.LeafNodeSize,
        };

        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref terrainParams, 1));
            _terrainParamsBuffer = GDBuffer.CreateUniform((uint)bytes.Length, bytes);
        }

        uint nodeSelectedInfoBufferSize;
        unsafe
        {
            nodeSelectedInfoBufferSize = (uint)(sizeof(int) + 4 + Constants.MaxNodeInSelect * sizeof(NodeSelectedInfo)); // counter + padding + [NodeSelectedInfo] * n
        }
        _nodeSelectedInfoBuffer = GDBuffer.CreateStorage(nodeSelectedInfoBufferSize);

        // create pipeline
        _computeShader = ShaderHelper.CreateComputeShader("res://Shaders/Compute/TerrainCompute.glsl");

        var instancedParamsBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 0
        };
        instancedParamsBinding.AddId(_instancedParamsBuffer);

        var drawIndirectBufferBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 1
        };
        drawIndirectBufferBinding.AddId(_drawIndirectBuffer);

        var terrainParamsBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.UniformBuffer,
            Binding = 2
        };
        terrainParamsBinding.AddId(_terrainParamsBuffer);

        var nodeSelectedInfoBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 3
        };
        nodeSelectedInfoBinding.AddId(_nodeSelectedInfoBuffer);

        _computeSet = rd.UniformSetCreate(
        [
            instancedParamsBinding, drawIndirectBufferBinding, terrainParamsBinding, nodeSelectedInfoBinding
        ], _computeShader, 0);

        _computePipeline = rd.ComputePipelineCreate(_computeShader);

    }
    private void CreateQuadTree(Terrain terrain, MapDefinition definition)
    {
        Debug.Assert(terrain.ActiveCamera != null);
        Debug.Assert(terrain.Data.MinMaxErrorMaps != null);
        float horizontalFov = 75f;
        float viewPortWidth = terrain.ViewPortWidth;
        float tanHalfFov = MathF.Tan(0.5f * horizontalFov * MathF.PI / 180f);
        float K = viewPortWidth / (2 * tanHalfFov);
        _quadTree = new TerrainQuadTree((int)terrain.LeafNodeSize, K, terrain.Data.MinMaxErrorMaps);
    }

    public void Dispose()
    {
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            _drawIndirectBuffer.Dispose();

            _nodeSelectedInfoBuffer.Dispose();

            RenderingDevice rd = RenderingServer.GetRenderingDevice();
            //rd.FreeRid(_computePipeline);
            rd.FreeRid(_computeShader);
        }));
    }

    #region Process Virtual Texture

    private void UpdateVirtualTexture(NodeSelectedInfo[] nodeSelectedList, int nodeSelectedCount)
    {
        Debug.Assert(_geometricVT != null);
        for (int i = 0; i < nodeSelectedCount; ++i)
        {
            var nodeInfo = nodeSelectedList[i];
            RequestPageForNode((int)nodeInfo.lodLevel, (int)nodeInfo.x, (int)nodeInfo.y);
        }
        _geometricVT.Update();
    }

    private void RequestPageForNode(int lod, int nodeX, int nodeY)
    {
        Debug.Assert(_geometricVT != null);
        Debug.Assert(_terrain != null);
        int lodOffset = _terrain.HeightmapLodOffset;
        int pageMip = Math.Max(lod - lodOffset, 0);
        int nodeSize = (int)_terrain.LeafNodeSize << lod;
        int x = nodeX * nodeSize;
        int y = nodeY * nodeSize;
        int pageSizeInL0Raster = _geometricVT.TileSize << pageMip;
        int pageX = x / pageSizeInL0Raster;
        int pageY = y / pageSizeInL0Raster;
        var id = new VirtualPageID()
        {
            x = pageX,
            y = pageY,
            mip = pageMip,
        };
        _geometricVT.RequestPage(id);
    }



    #endregion
}