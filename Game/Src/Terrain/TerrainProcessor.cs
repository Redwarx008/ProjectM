using Core;
using Godot;
using ProjectM;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using NodeSelectedInfo = TerrainQuadTree.NodeSelectedInfo;

internal class TerrainProcessor : IDisposable
{
    #region Compute Resources

    private GDBuffer _planeIndirectBuffer = null!;
    private GDBuffer _planeInstancedBuffer = null!;
    private GDBuffer _skirtIndirectBuffer = null!;
    private GDBuffer _skirtInstancedBuffer = null!;
    private GDBuffer _terrainParamsBuffer = null!;
    private GDBuffer _nodeSelectedInfoBuffer = null!;

    private Rid _computePipeline;
    private Rid _computeSet;
    private Rid _computeShader;
    
    #endregion

    private int _maxLodNodeX;
    private int _maxLodNodeY;
    private int _leafNodeX;
    private int _leafNodeY;
    private int _lodCount;

    private readonly Callable _dispatchCallable;

    private Terrain? _terrain;

    private TerrainQuadTree? _quadTree;

    private TerrainMesh? _planeMesh;

    private TerrainMesh? _skirtMesh;

    private VirtualTexture? _geometricVT; // height or normal

    private NodeSelectedInfo[] _currentSelectedNodes;

    private ArrayPool<NodeSelectedInfo> _arrayPool;

    public bool Inited { get; private set; } = false;

    public static readonly int MaxNodeInSelect = 1000;

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

    public TerrainProcessor(Terrain terrain, TerrainMesh? planeMesh, TerrainMesh? skirtMesh)
    {
        _terrain = terrain;
        _planeMesh = planeMesh;
        _skirtMesh = skirtMesh;
        _geometricVT = terrain.Data.GeometricVT;
        _arrayPool = ArrayPool<NodeSelectedInfo>.Create(MaxNodeInSelect, 2);
        _currentSelectedNodes = _arrayPool.Rent(MaxNodeInSelect);
        CalcLodParameters(terrain.Data.GeometricWidth, terrain.Data.GeometricHeight, (int)terrain.LeafNodeSize);
        CreateQuadTree(terrain);
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            InitPipeline(terrain);
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
            nodeSelectedInfos = _currentSelectedNodes,
            planes = planes,
            viewerPos = camera.GlobalPosition,
            tolerableError = _terrain!.TolerableError,
            nodeSelectedCount = 0,
            heightScale = _terrain!.HeightScale
        };
        _quadTree.Select(ref selectDesc);
        UpdateVirtualTexture(selectDesc.nodeSelectedInfos, selectDesc.nodeSelectedCount);
        SubmitForRender(selectDesc.nodeSelectedCount);
    }

    private void SubmitForRender(int selectedCount)
    {
        if (selectedCount <= 0) return;

        var arrayToSubmit = _currentSelectedNodes;

        _currentSelectedNodes = _arrayPool.Rent(MaxNodeInSelect);

        RenderingServer.CallOnRenderThread(Callable.From(()=>
        {
            Dispatch(arrayToSubmit, selectedCount);
            _arrayPool.Return(arrayToSubmit, clearArray: false);
        })); //todo: 直接提交DispatchOneBatch当Callable.Bind可用时，
    }
    private void Dispatch(
        NodeSelectedInfo[] selected, int selectedCount)
    {
        RenderingDevice rd = RenderingServer.GetRenderingDevice();

        rd.BufferClear(_planeIndirectBuffer, 4, 4);
        rd.BufferClear(_skirtIndirectBuffer, 4, 4);

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
    
    private void InitPipeline(Terrain terrain)
    {   
        Debug.Assert(_planeMesh != null && _skirtMesh != null);

        _planeInstancedBuffer = GDBuffer.CreateManaged(_planeMesh.GetInstanceBuffer());
        _planeIndirectBuffer = GDBuffer.CreateManaged(_planeMesh.GetDrawIndirectBuffer());
        _skirtInstancedBuffer = GDBuffer.CreateManaged(_skirtMesh.GetInstanceBuffer());
        _skirtIndirectBuffer = GDBuffer.CreateManaged(_skirtMesh.GetDrawIndirectBuffer());

        RenderingDevice rd = RenderingServer.GetRenderingDevice();

        uint meshIndexCount = terrain.LeafNodeSize * terrain.LeafNodeSize * 6;
        ReadOnlySpan<byte> indexCount = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref meshIndexCount, 1));
        rd.BufferUpdate(_planeIndirectBuffer, 0, sizeof(uint), indexCount);
        rd.BufferUpdate(_skirtIndirectBuffer, 0, sizeof(uint), indexCount);
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
            nodeSelectedInfoBufferSize = (uint)(sizeof(int) + 4 + MaxNodeInSelect * sizeof(NodeSelectedInfo)); // counter + padding + [NodeSelectedInfo] * n
        }
        _nodeSelectedInfoBuffer = GDBuffer.CreateStorage(nodeSelectedInfoBufferSize);

        // create pipeline
        _computeShader = ShaderHelper.CreateComputeShader("res://Shaders/Compute/TerrainCompute.glsl");

        var planeInstancedParamsBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 0
        };
        planeInstancedParamsBinding.AddId(_planeInstancedBuffer);

        var skirtInstancedParamsBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 1
        };
        skirtInstancedParamsBinding.AddId(_skirtInstancedBuffer);

        var planeIndirectBufferBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 2
        };
        planeIndirectBufferBinding.AddId(_planeIndirectBuffer);

        var skirtIndirectBufferBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 3
        };
        skirtIndirectBufferBinding.AddId(_skirtIndirectBuffer);

        var terrainParamsBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.UniformBuffer,
            Binding = 4
        };
        terrainParamsBinding.AddId(_terrainParamsBuffer);

        var nodeSelectedInfoBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 5
        };
        nodeSelectedInfoBinding.AddId(_nodeSelectedInfoBuffer);

        _computeSet = rd.UniformSetCreate(
        [
            planeInstancedParamsBinding, skirtInstancedParamsBinding, 
            planeIndirectBufferBinding, skirtIndirectBufferBinding, 
            terrainParamsBinding, nodeSelectedInfoBinding
        ], _computeShader, 0);

        _computePipeline = rd.ComputePipelineCreate(_computeShader);

    }
    private void CreateQuadTree(Terrain terrain)
    {
        Debug.Assert(terrain.ActiveCamera != null);
        Debug.Assert(terrain.Data.MinMaxErrorMaps != null);
        float horizontalFov = 75f;
        float viewPortWidth = terrain.ViewPortWidth;
        float tanHalfFov = MathF.Tan(0.5f * horizontalFov * MathF.PI / 180f);
        float K = viewPortWidth / (2 * tanHalfFov);
        _quadTree = new TerrainQuadTree((int)terrain.LeafNodeSize, K, terrain.Data.MinMaxErrorMaps);
        //_quadTree = new TerrainQuadTree((int)terrain.LeafNodeSize, K,
        //    MinMaxErrorMap.CreateDefault((int)terrain.LeafNodeSize, terrain.Data.GeometricWidth, terrain.Data.GeometricHeight, _lodCount));
    }

    public void Dispose()
    {
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            _planeIndirectBuffer.Dispose();

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
        int lodOffset = _terrain.Data.HeightmapLodOffset;
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