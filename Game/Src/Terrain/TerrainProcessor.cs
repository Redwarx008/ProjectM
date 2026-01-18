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
using static System.Runtime.InteropServices.JavaScript.JSType;

internal class TerrainProcessor : IDisposable
{
    #region Compute Resources

    private GDBuffer _dispatchIndirectBuffer = null!;
    private GDBuffer _drawIndirectBuffer = null!;
    private GDBuffer _instancedBuffer = null!;
    private GDBuffer _terrainParamsBuffer = null!;

    private GDBuffer _nodeListA = null!;
    private GDBuffer _nodeListB = null!;
    private GDBuffer _topNodeList = null!;

    private Rid _nodeSelectPipeline;
    private Rid _nodeSelectSet0;
    private Rid _nodeSelectSet1AsPing;
    private Rid _nodeSelectSet1AsPong;
    private Rid _nodeSelectShader;
    
    #endregion

    private int _lodCount;

    private readonly Callable _dispatchCallable;

    private Terrain? _terrain;

    private TerrainMesh? _mesh;

    private VirtualTexture? _geometricVT; // height or normal

    public bool Inited { get; private set; } = false;

    public static readonly int MaxNodeInSelect = 1000;

    #region Data struct Definition

    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants()
    {
        public Vector4 FrustumPlane0 = Vector4.Zero;
        public Vector4 FrustumPlane1 = Vector4.Zero;
        public Vector4 FrustumPlane2 = Vector4.Zero;
        public Vector4 FrustumPlane3 = Vector4.Zero;
        public Vector4 FrustumPlane4 = Vector4.Zero;
        public Vector4 FrustumPlane5 = Vector4.Zero;
        public Vector4 CameraPosition = Vector4.Zero;
        public int LodLevel = 0;
        public int LodCount = 0;
        public float SubdivisionDistance = 0f;
        private int _padding0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TerrainParams
    {
        public uint heightmapSizeX;
        public uint heightmapSizeY;
        public float mapSizeX;
        public float mapSizeY;
        public float mapOffsetX;
        public float mapOffsetY;
        public float height;
        public float heightOffset;
        public uint patchSize;
        private uint padding;
    }

    #endregion

    private PushConstants _pushConstants;

    private float[] _lodRanges;

    public TerrainProcessor(Terrain terrain, TerrainMesh? mesh)
    {
        _terrain = terrain;
        _mesh = mesh;
        _geometricVT = terrain.Data.GeometricVT;
        CalcLodParameters(terrain.Data.HeightmapDimX, terrain.Data.HeightmapDimY, (int)terrain.PatchSize);
        _dispatchCallable = Callable.From(Dispatch);
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            InitPipeline(terrain);
            Inited = true;
        }));

    }
    public void Process(Camera3D camera)
    {
        // todo: 自己计算Frustum避免堆分配造成GC压力
        UpdateFrustumAndCamera(camera);
        _pushConstants.LodCount = _lodCount;
        RenderingServer.CallOnRenderThread(_dispatchCallable);
    }

    private void Dispatch()
    {
        RenderingDevice rd = RenderingServer.GetRenderingDevice();

        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.Cast<int, Byte>([(_topNodeX * _topNodeY), 1, 1]);
            rd.BufferUpdate(_dispatchIndirectBuffer, 0, sizeof(uint) * 3, bytes);
        }
        rd.BufferClear(_drawIndirectBuffer, 4, 4);

        GDBuffer consumeList = _nodeListA;
        GDBuffer appendList = _nodeListB;
        rd.BufferCopy(_topNodeList, consumeList, 0, 0, _topNodeList.SizeBytes);
        rd.BufferClear(appendList, 0, sizeof(int));

        ReadOnlySpan<Rid> set1S = [_nodeSelectSet1AsPing, _nodeSelectSet1AsPong];
        for (int i = 0; i < _lodCount; ++i)
        {
            _pushConstants.LodLevel = _lodCount - i - 1;

            var computeList = rd.ComputeListBegin();
            rd.ComputeListBindComputePipeline(computeList, _nodeSelectPipeline);
            rd.ComputeListBindUniformSet(computeList, _nodeSelectSet0, 0);
            rd.ComputeListBindUniformSet(computeList, set1S[i % 2], 1);
            ReadOnlySpan<byte> pushConstantBytes =
                MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref _pushConstants, 1));
            rd.ComputeListSetPushConstant(computeList, pushConstantBytes, (uint)pushConstantBytes.Length);
            rd.ComputeListDispatchIndirect(computeList, _dispatchIndirectBuffer, 0);
            rd.ComputeListEnd();

            rd.BufferCopy(appendList, _dispatchIndirectBuffer, 0, 0, sizeof(uint));

            (consumeList, appendList) = (appendList, consumeList);
        }
    }

    private void UpdateFrustumAndCamera(Camera3D camera)
    {
        var frustumPlanes = camera.GetFrustum().ToArray();
        _pushConstants.FrustumPlane0 = frustumPlanes[0].ToVector4();
        _pushConstants.FrustumPlane1 = frustumPlanes[1].ToVector4();
        _pushConstants.FrustumPlane2 = frustumPlanes[2].ToVector4();
        _pushConstants.FrustumPlane3 = frustumPlanes[3].ToVector4();
        _pushConstants.FrustumPlane4 = frustumPlanes[4].ToVector4();
        _pushConstants.FrustumPlane5 = frustumPlanes[5].ToVector4();
        Vector3 position = camera.GlobalPosition;
        _pushConstants.CameraPosition = new Vector4(position.X, position.Y, position.Z, 1);
    }

    private void CalcLodParameters(int mapRasterSizeX, int mapRasterSizeY, int leafNodeSize)
    {
        int minLength = Math.Min(mapRasterSizeX, mapRasterSizeY);
        int topNodeSize = (int)(leafNodeSize * (int)Math.Pow(2, _lodCount - 1));
        _topNodeX = (int)Math.Ceiling(mapRasterSizeX / (float)topNodeSize);
        _topNodeY = (int)Math.Ceiling(mapRasterSizeY / (float)topNodeSize);
        _leafNodeX = (int)MathF.Ceiling((mapRasterSizeX - 1) / (float)leafNodeSize);
        _leafNodeY = (int)MathF.Ceiling((mapRasterSizeY - 1) / (float)leafNodeSize);
    }

    private void CalculateViewRange(Span<float> viewRangePerLevel)
    {
        Debug.Assert(_terrain != null);
        //Initialize visible range per level.
        float lodNear = 0;
        float lodFar = _terrain.ViewRange;
        float detailBalance = 2;
        int layerCount = _lodCount;
        float currentDetailBalance = 1f;

        Span<float> lODVisRangeDistRatios = stackalloc float[layerCount];
        for (int i = 0; i < layerCount - 1; i++)
        {
            lODVisRangeDistRatios[i] = currentDetailBalance;
            if (i == 0)
            {
                lODVisRangeDistRatios[i] = 0.9f;
            }
            currentDetailBalance *= detailBalance;
        }

        lODVisRangeDistRatios[layerCount - 1] = currentDetailBalance;
        for (int i = 0; i < layerCount; i++)
        {
            lODVisRangeDistRatios[i] /= currentDetailBalance;
        }
        viewRangePerLevel = new float[layerCount];
        for (int i = 0; i < layerCount; i++)
        {
            viewRangePerLevel[i] = lodNear + lODVisRangeDistRatios[i] * (lodFar - lodNear);
        }
    }

    private void InitPipeline(Terrain terrain)
    {   
        Debug.Assert(_mesh != null);
        Debug.Assert(terrain.Data.MinMaxMaps != null);

        _instancedBuffer = GDBuffer.CreateManaged(_mesh.GetInstanceBuffer());
        _drawIndirectBuffer = GDBuffer.CreateManaged(_mesh.GetDrawIndirectBuffer());

        RenderingDevice rd = RenderingServer.GetRenderingDevice();

        uint meshIndexCount = terrain.PatchSize * terrain.PatchSize * 6;
        ReadOnlySpan<byte> indexCount = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref meshIndexCount, 1));
        rd.BufferUpdate(_drawIndirectBuffer, 0, sizeof(uint), indexCount);
        // create Buffers
        _dispatchIndirectBuffer = GDBuffer.CreateStorage(sizeof(uint) * 3, null, true);
        uint maxNodeBufferSize =
            sizeof(uint) + 4 + (uint)MaxNodeInSelect * sizeof(uint) * 2; // counter + padding + [nodeX, nodeY] * n
        _nodeListA = GDBuffer.CreateStorage(maxNodeBufferSize);
        _nodeListB = GDBuffer.CreateStorage(maxNodeBufferSize);
        {
            int maxLodNodeSize = _topNodeX * _topNodeY;
            List<uint> maxLodNodeData = new List<uint>();
            maxLodNodeData.Add((uint)maxLodNodeSize);
            maxLodNodeData.Add(0); //padding, std430 is aligned according to the largest member boundary, here is 8
            for (uint y = 0; y < _topNodeY; ++y)
            {
                for (uint x = 0; x < _topNodeX; ++x)
                {
                    maxLodNodeData.Add(x);
                    maxLodNodeData.Add(y);
                }
            }

            ReadOnlySpan<byte> bytes = MemoryMarshal.Cast<uint, Byte>(CollectionsMarshal.AsSpan(maxLodNodeData));
            _topNodeList = GDBuffer.CreateStorage((uint)bytes.Length, bytes);
        }

        var terrainParams = new TerrainParams()
        {
            patchSize = terrain.PatchSize,
            heightmapSizeX = (uint)terrain.Data.HeightmapDimX,
            heightmapSizeY = (uint)terrain.Data.HeightmapDimY,
            height = terrain.Height
        };

        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref terrainParams, 1));
            _terrainParamsBuffer = GDBuffer.CreateUniform((uint)bytes.Length, bytes);
        }


        // create pipeline
        _nodeSelectShader = ShaderHelper.CreateComputeShader("res://Shaders/Compute/TerrainNodeSelect.glsl");

        var minMaxErrorMapsBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.Image,
            Binding = 0
        };

        foreach (var minMaxErrorMap in terrain.Data.MinMaxMaps)
        {
            minMaxErrorMapsBinding.AddId(minMaxErrorMap.Rid);
        }

        // Fill the remaining slots.
        for (int i = terrain.Data.MinMaxMaps.Length; i < Terrain.MaxLodCount; i++)
        {
            minMaxErrorMapsBinding.AddId(terrain.Data.MinMaxMaps[0].Rid);
        }

        var instancedBufferBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 1
        };
        instancedBufferBinding.AddId(_instancedBuffer);

        var terrainParamsBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.UniformBuffer,
            Binding = 2
        };
        terrainParamsBinding.AddId(_terrainParamsBuffer);

        var drawIndirectBufferBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 3
        };
        drawIndirectBufferBinding.AddId(_drawIndirectBuffer);

        _nodeSelectSet0 = rd.UniformSetCreate(
        [
            minMaxErrorMapsBinding, instancedBufferBinding, terrainParamsBinding, drawIndirectBufferBinding,
        ], _nodeSelectShader, 0);
        // create ping-pong buffer binding
        var consumeListBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 0
        };
        consumeListBinding.AddId(_nodeListA);

        var appendListBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = 1
        };
        appendListBinding.AddId(_nodeListB);
        _nodeSelectSet1AsPing = rd.UniformSetCreate([consumeListBinding, appendListBinding], _nodeSelectShader, 1);
        consumeListBinding.ClearIds();
        consumeListBinding.AddId(_nodeListB);
        appendListBinding.ClearIds();
        appendListBinding.AddId(_nodeListA);

        _nodeSelectSet1AsPong = rd.UniformSetCreate([consumeListBinding, appendListBinding], _nodeSelectShader, 1);
        _nodeSelectPipeline = rd.ComputePipelineCreate(_nodeSelectShader);

    }
    public void Dispose()
    {
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            _drawIndirectBuffer.Dispose();
            _dispatchIndirectBuffer.Dispose();

            _nodeListA.Dispose();
            _nodeListB.Dispose();
            _topNodeList.Dispose();
            RenderingDevice rd = RenderingServer.GetRenderingDevice();
            //rd.FreeRid(_computePipeline);
            rd.FreeRid(_nodeSelectShader);
        }));
    }

    #region Process Virtual Texture

    //private void UpdateVirtualTexture(NodeSelectedInfo[] nodeSelectedList, int nodeSelectedCount)
    //{
    //    Debug.Assert(_geometricVT != null);
    //    for (int i = 0; i < nodeSelectedCount; ++i)
    //    {
    //        var nodeInfo = nodeSelectedList[i];
    //        RequestPageForNode((int)nodeInfo.lodLevel, (int)nodeInfo.x, (int)nodeInfo.y);
    //    }
    //    _geometricVT.Update();
    //}

    //private void RequestPageForNode(int lod, int nodeX, int nodeY)
    //{
    //    Debug.Assert(_geometricVT != null);
    //    Debug.Assert(_terrain != null);
    //    int lodOffset = _terrain.Data.HeightmapLodOffset;
    //    int pageMip = Math.Max(lod - lodOffset, 0);
    //    int nodeSize = (int)_terrain.LeafNodeSize << lod;
    //    int x = nodeX * nodeSize;
    //    int y = nodeY * nodeSize;
    //    int pageSizeInL0Raster = _geometricVT.TileSize << pageMip;
    //    int pageX = x / pageSizeInL0Raster;
    //    int pageY = y / pageSizeInL0Raster;
    //    var id = new VirtualPageID()
    //    {
    //        x = pageX,
    //        y = pageY,
    //        mip = pageMip,
    //    };
    //    _geometricVT.RequestPage(id);
    //}



    #endregion
}