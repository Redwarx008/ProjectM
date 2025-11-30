using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

using NodeSelectedInfo = TerrainQuadTree.NodeSelectedInfo;
using NodeSubdivisionInfo = TerrainQuadTree.NodeSubdivisionInfo;

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

    #region Build Lodmap Resources

    public GDTexture2D? LodMap { get; private set; }
    
    private Rid _buildLodMapPipeline;
    private Rid _buildLodMapSet0;
    private Rid _buildLodMapShader;

    #endregion

    #region Build SubdivisionMap Resource
    private GDBuffer _nodeDescriptorBuffer = null!;
    private GDBuffer _nodeDescriptorLocationInfoBuffer = null!;
    private GDBuffer _nodeSubdividedInfoBuffer = null!;

    private Rid _buildSubdivisionMapPipeline;
    private Rid _buildSubdivisionMapSet0;
    private Rid _buildSubdivisionMapShader;
    #endregion

    private int _maxLodNodeX;
    private int _maxLodNodeY;
    private int _leafNodeX;
    private int _leafNodeY;
    private int _lodCount;

    private readonly Callable _cachedDispatchCallback;

    private TerrainQuadTree? _quadTree;

    private TerrainMesh? _planeMesh;

    private NodeSelectedInfo[] _nodeSelectedInfos => _backSelected;
    private NodeSubdivisionInfo[] _nodeSubdivisionInfos => _backSubdivision;

    private NodeSelectedInfo[] _frontSelected = new NodeSelectedInfo[Constants.MaxNodeInSelect];
    private NodeSelectedInfo[] _backSelected = new NodeSelectedInfo[Constants.MaxNodeInSelect];


    private NodeSubdivisionInfo[] _frontSubdivision = new NodeSubdivisionInfo[Constants.MaxNodeInSelect];
    private NodeSubdivisionInfo[] _backSubdivision = new NodeSubdivisionInfo[Constants.MaxNodeInSelect];

    // 保护交换操作的锁（仅在主线程使用，但仍保守使用）
    private readonly object _bufferLock = new();

    // 渲染线程将在 callback 中读取这两个引用（volatile 保证引用的原子读取/写入可见性）
    private volatile NodeSelectedInfo[]? _dispatchSelected;
    private volatile NodeSubdivisionInfo[]? _dispatchSubdivision;

    private volatile int _dispatchSelectedCount;
    private volatile int _dispatchSubdivisionCount;

    public float TolerableError { get; set; }

    public bool Inited { get; private set; } = false;

    #region Data struct Definition

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TerrainParams
    {
        public uint HeightmapSizeX;
        public uint HeightmapSizeY;
        public float HeightScale;
        public uint LeafNodeSize;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct NodeDescriptor
    {
        public uint Branch; // Indicates whether this quadtree node is divided
    }


    #endregion




    public void Init(Terrain terrain, TerrainMesh? mesh, MapDefinition definition)
    {
        Debug.Assert(terrain.Data.Heightmap != null);
        _planeMesh = mesh;
        CalcLodParameters((int)terrain.Data.Heightmap.Width, (int)terrain.Data.Heightmap.Height);
        InitBuildSubdivisionMapPipeline();
        InitBuildLodMapPipeline();
        InitDrawPipeline(terrain, definition);
        CreateQuadTree(terrain, definition);
        TolerableError = definition.TerrainTolerableError;
        Inited = true;
    }
    public TerrainProcessor()
    {
        _cachedDispatchCallback = Callable.From(() =>
        {
            // 渲染线程执行：读取 stable front
            var sel = _dispatchSelected;
            var sub = _dispatchSubdivision;
            var selN = _dispatchSelectedCount;
            var subN = _dispatchSubdivisionCount;

            // **永远只读，不写，不改数组内容**
            if (sel != null && sub != null)
                Dispatch(sel, selN, sub, subN);
        });
    }
    public void Process(Camera3D camera)
    {
        Debug.Assert(_quadTree != null);
        // todo: 自己计算Frustum避免堆分配造成GC压力
        ReadOnlySpan<Plane> planes = camera.GetFrustum().ToArray();
        var selectDesc = new TerrainQuadTree.SelectDesc
        {
            NodeSelectedInfos = _nodeSelectedInfos,
            NodeSubdivisionInfos = _nodeSubdivisionInfos,
            Planes = planes,
            ViewerPos = camera.GlobalPosition,
            TolerableError = TolerableError,
            NodeSelectedCount = 0,
            NodeSubdivisionCount = 0,
        };
        _quadTree.Select(ref selectDesc);

        SubmitForRender(selectDesc.NodeSelectedCount, selectDesc.NodeSubdivisionCount);
    }

    // 交换并提交给渲染线程（仅在主线程调用）
    private void SubmitForRender(int selectedCount, int subdivisionCount)
    {
        // 锁住交换 —— 确保可见性与原子性
        lock (_bufferLock)
        {
            // 交换 Selected
            (_frontSelected, _backSelected) = (_backSelected, _frontSelected);

            // 交换 Subdivision
            (_frontSubdivision, _backSubdivision) = (_backSubdivision, _frontSubdivision);

            // 设置渲染线程可读的稳定 front 引用（volatile 写）
            _dispatchSelected = _frontSelected;
            _dispatchSubdivision = _frontSubdivision;
            _dispatchSelectedCount = selectedCount;
            _dispatchSubdivisionCount = subdivisionCount;
        }

        RenderingServer.CallOnRenderThread(_cachedDispatchCallback);
    }
    private void Dispatch(
        NodeSelectedInfo[] selected, int selectedCount,
        NodeSubdivisionInfo[] subdivision, int subdivisionCount)
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
        unsafe
        {
            ReadOnlySpan<byte> counterBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref subdivisionCount, sizeof(int)));
            rd.BufferUpdate(_nodeSubdividedInfoBuffer, 0, sizeof(int), counterBytes);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(subdivision.AsSpan(0, subdivisionCount));
            rd.BufferUpdate(_nodeSubdividedInfoBuffer, 8, (uint)(sizeof(NodeSubdivisionInfo) * subdivisionCount), bytes);
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

        // build SubdivisionMap pass
        if (subdivisionCount > 0)
        {
            var computeList = rd.ComputeListBegin();
            rd.ComputeListBindComputePipeline(computeList, _buildSubdivisionMapPipeline);
            rd.ComputeListBindUniformSet(computeList, _buildSubdivisionMapSet0, 0);
            rd.ComputeListDispatch(computeList, (uint)(Math.Ceiling(subdivisionCount / 64f)), 1, 1);
            rd.ComputeListEnd();
        }

        // build lod map pass
        {
            var computeList = rd.ComputeListBegin();
            rd.ComputeListBindComputePipeline(computeList, _buildLodMapPipeline);
            rd.ComputeListBindUniformSet(computeList, _buildLodMapSet0, 0);
            ReadOnlySpan<byte> pushConstantBytes = MemoryMarshal.AsBytes<int>([_lodCount, 0, 0, 0]);
            rd.ComputeListSetPushConstant(computeList, pushConstantBytes, (uint)pushConstantBytes.Length);
            uint nDisPatch = (uint)Math.Ceiling(_leafNodeX / 8f);
            rd.ComputeListDispatch(computeList, nDisPatch, nDisPatch, 1);
            rd.ComputeListEnd();
        }
    }

    private void CalcLodParameters(int mapRasterSizeX, int mapRasterSizeY)
    {
        int minLength = Math.Min(mapRasterSizeX, mapRasterSizeY);
        _lodCount = (int)Math.Log2(minLength) - (int)Math.Log2(Constants.LeafNodeSize);
        int topNodeSize = (int)(Constants.LeafNodeSize * (int)Math.Pow(2, _lodCount - 1));
        _maxLodNodeX = (int)Math.Ceiling(mapRasterSizeX / (float)topNodeSize);
        _maxLodNodeY = (int)Math.Ceiling(mapRasterSizeY / (float)topNodeSize);
        _leafNodeX = (int)MathF.Ceiling((mapRasterSizeX - 1) / (float)Constants.LeafNodeSize);
        _leafNodeY = (int)MathF.Ceiling((mapRasterSizeY - 1) / (float)Constants.LeafNodeSize);
    }
    
    private void InitDrawPipeline(Terrain terrain, MapDefinition definition)
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

        uint nodeSelectedInfoBufferSize;
        unsafe
        {
            nodeSelectedInfoBufferSize = (uint)(sizeof(int) + 4 + Constants.MaxNodeInSelect * sizeof(NodeSelectedInfo)); // counter + padding + [NodeSelectedInfo] * n
        }
        _nodeSelectedInfoBuffer = GDBuffer.CreateStorage(nodeSelectedInfoBufferSize);

        var terrainParams = new TerrainParams()
        {
            HeightmapSizeX = heightmap.Width,
            HeightmapSizeY = heightmap.Height,
            HeightScale = definition.TerrainHeightScale,
            LeafNodeSize = terrain.LeafNodeSize,
        };

        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref terrainParams, 1));
            _terrainParamsBuffer = GDBuffer.CreateUniform((uint)bytes.Length, bytes);
        }

        // create pipeline
        _computeShader = ShaderHelper.CreateComputeShader("res://Shaders/Compute/TerrainCompute.glsl");

        var instancedParamsBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 0
        };
        instancedParamsBinding.AddId(_instancedParamsBuffer);

        var terrainParamsBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.UniformBuffer,
            Binding = 1
        };
        terrainParamsBinding.AddId(_terrainParamsBuffer);

        var drawIndirectBufferBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 2
        };
        drawIndirectBufferBinding.AddId(_drawIndirectBuffer);

        var nodeSelectedInfoBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 3
        };
        nodeSelectedInfoBinding.AddId(_nodeSelectedInfoBuffer);

        _computeSet = rd.UniformSetCreate(
        [
            instancedParamsBinding, terrainParamsBinding, drawIndirectBufferBinding, nodeSelectedInfoBinding
        ], _computeShader, 0);

        _computePipeline = rd.ComputePipelineCreate(_computeShader);

    }

    private void InitBuildSubdivisionMapPipeline()
    {
        // build NodeDescriptorLocation buffer
        var nodeIndexOffsetPerLod = new uint[Constants.MaxLodLevel];
        var nodeCountPerLod = new uint[Constants.MaxLodLevel * 2];
        uint offset = 0;
        for (int lod = _lodCount - 1; lod >= 0; --lod)
        {
            int nodeSize = 1 << lod;
            uint nodeCountX = (uint)MathF.Ceiling((float)_leafNodeX / (float)nodeSize);
            uint nodeCountY = (uint)Math.Ceiling((float)_leafNodeY / (float)nodeSize);
            nodeCountPerLod[lod * 2] = nodeCountX;
            nodeCountPerLod[lod * 2 + 1] = nodeCountY;

            nodeIndexOffsetPerLod[lod] = offset;
            offset += nodeCountX * nodeCountY;
        }

        var data = new uint[nodeIndexOffsetPerLod.Length + nodeCountPerLod.Length];
        Array.Copy(nodeIndexOffsetPerLod, 0, data, 0, nodeIndexOffsetPerLod.Length);
        Array.Copy(nodeCountPerLod, 0, data, nodeIndexOffsetPerLod.Length, nodeCountPerLod.Length);
        ReadOnlySpan<byte> dataBytes = MemoryMarshal.AsBytes<uint>(data);
        _nodeDescriptorLocationInfoBuffer = GDBuffer.CreateUniform((uint)dataBytes.Length, dataBytes);

        unsafe
        {
            _nodeDescriptorBuffer = GDBuffer.CreateStorage(offset * (uint)sizeof(NodeDescriptor));
        }

        // build NodeSubdividedInfoBuffer
        uint nodeSubdividedInfoBufferSize;
        unsafe
        {
            nodeSubdividedInfoBufferSize = (uint)(sizeof(int) + 4 + Constants.MaxNodeInSelect * sizeof(NodeSubdivisionInfo));
        }
        _nodeSubdividedInfoBuffer = GDBuffer.CreateStorage(nodeSubdividedInfoBufferSize);

        // create binding 
        var nodeDescriptorLocationInfoBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.UniformBuffer,
            Binding = 0
        };
        nodeDescriptorLocationInfoBinding.AddId(_nodeDescriptorLocationInfoBuffer);

        var nodeDescriptorBufferBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 1
        };
        nodeDescriptorBufferBinding.AddId(_nodeDescriptorBuffer);

        var nodeSubdividedInfoBufferBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 2
        };
        nodeSubdividedInfoBufferBinding.AddId(_nodeSubdividedInfoBuffer);

        RenderingDevice rd = RenderingServer.GetRenderingDevice();

        _buildSubdivisionMapShader = ShaderHelper.CreateComputeShader("res://Shaders/Compute/SubdivisionMapCompute.glsl");

        _buildSubdivisionMapPipeline = rd.ComputePipelineCreate(_buildSubdivisionMapShader);

        _buildSubdivisionMapSet0 = rd.UniformSetCreate(
            [
                nodeDescriptorLocationInfoBinding, nodeDescriptorBufferBinding, nodeSubdividedInfoBufferBinding
            ], _buildSubdivisionMapShader, 0);
    }
    
    private void InitBuildLodMapPipeline()
    {
        Debug.Assert(_nodeDescriptorLocationInfoBuffer.Rid != Constants.NullRid);
        Debug.Assert(_nodeDescriptorBuffer.Rid != Constants.NullRid);
        CreateLodMap();

        var lodMapBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.Image,
            Binding = 0
        };
        lodMapBinding.AddId(LodMap!.Rid);

        var nodeDescriptorLocationInfoBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.UniformBuffer,
            Binding = 1
        };
        nodeDescriptorLocationInfoBinding.AddId(_nodeDescriptorLocationInfoBuffer);

        var nodeDescriptorBufferBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 2
        };
        nodeDescriptorBufferBinding.AddId(_nodeDescriptorBuffer);

        _buildLodMapShader = ShaderHelper.CreateComputeShader("res://Shaders/Compute/LodMapCompute.glsl");
        Debug.Assert(_buildLodMapShader != Constants.NullRid);

        RenderingDevice rd = RenderingServer.GetRenderingDevice();

        _buildLodMapSet0 =
            rd.UniformSetCreate([lodMapBinding, nodeDescriptorLocationInfoBinding, nodeDescriptorBufferBinding],
                _buildLodMapShader, 0);
        Debug.Assert(_buildLodMapSet0 != Constants.NullRid);

        _buildLodMapPipeline = rd.ComputePipelineCreate(_buildLodMapShader);
    }
    
    private void CreateLodMap()
    {
        Debug.Assert(_planeMesh != null && _planeMesh.Material != null);
        RenderingDevice rd = RenderingServer.GetRenderingDevice();

        var desc = new GDTexture2DDesc()
        {
            Width = (uint)_leafNodeX,
            Height = (uint)_leafNodeY,
            Format = RenderingDevice.DataFormat.R8Unorm,
            Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit |
                        RenderingDevice.TextureUsageBits.CanCopyToBit |
                        RenderingDevice.TextureUsageBits.CanCopyFromBit
        };
        LodMap = GDTexture2D.Create(desc);
        _planeMesh.Material.SetShaderParameter("u_lodmap", LodMap.ToTexture2d());
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
        _drawIndirectBuffer.Dispose();

        _nodeSelectedInfoBuffer.Dispose();
        _terrainParamsBuffer.Dispose();
        
        _nodeDescriptorLocationInfoBuffer.Dispose();
        _nodeDescriptorBuffer.Dispose();
        if (LodMap != null)
        {
            LodMap.Dispose();
        }

        RenderingDevice rd = RenderingServer.GetRenderingDevice();
        rd.FreeRid(_computePipeline);
        rd.FreeRid(_computeShader);
        rd.FreeRid(_buildLodMapPipeline);
        rd.FreeRid(_buildLodMapShader);
        rd.FreeRid(_buildSubdivisionMapPipeline);
        rd.FreeRid(_buildSubdivisionMapShader);
    }
}