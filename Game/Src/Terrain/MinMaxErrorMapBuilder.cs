using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace Abandoned;
public struct MinMaxErrorMap
{
    public void GetMinMaxError(int nodeX, int nodeY, out float min, out float max, out float geometricError)
    {
        Debug.Assert(nodeX >= 0 && nodeY >= 0 && nodeX < Width && nodeY < Height);
        Span<float> floats = MemoryMarshal.Cast<byte, float>(Data.AsSpan());
        int index = ((nodeY * (int)Width) + nodeX) * 4;
        min = floats[index];
        max = floats[index + 1];
        geometricError = floats[index + 2];
    }

    public void GetSubNodesExist(int parentX, int parentY,
    out bool subTLExist, out bool subTRExist, out bool subBLExist, out bool subBRExist)
    {
        int x = parentX * 2;
        int y = parentY * 2;

        subTLExist = true;
        subTRExist = (x + 1) < Width;
        subBLExist = (y + 1) < Height;
        subBRExist = (x + 1) < Width && (y + 1) < Height;
    }

    public uint Width { get; init; }
    public uint Height { get; init; }

    public byte[] Data { get; init; }
}

internal class MinMaxErrorMapBuilder
{
    private static int LeafNodeSize = 32;
    private static int MaxLodLevel = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct TerrainParams()
    {
        public float TerrainHeightScale = 0;
        public uint ChunkSize = 0;
        private uint _padding0 = 0;
        private uint _padding1 = 0;
    }
    public static int CalcLodCount(int mapRasterSizeX, int mapRasterSizeY)
    {
        int minLength = Math.Min(mapRasterSizeX, mapRasterSizeY);
        return (int)Math.Log2(minLength) - (int)Math.Log2(LeafNodeSize);
        // topNodeSize = (int)(Constants.ChunkSize * (int)Math.Pow(2, LodCount - 1));
        // _leafNodeCountX = (int)MathF.Ceiling((mapRasterSizeX - 1) / (float)Constants.ChunkSize);
        // _leafNodeCountY = (int)MathF.Ceiling((mapRasterSizeY - 1) / (float)Constants.ChunkSize);
    }

    public static List<GDTexture2D> BuildTextures(in GDTexture2D heightmap, float heightScale, uint chunkSize, int nLod = 0)
    {
        var minMaxErrorMapTextures = new List<GDTexture2D>();
        int width = (int)heightmap.Width;
        int height = (int)heightmap.Height;
        int lodCount = nLod == 0 ? CalcLodCount(width, height) : nLod;

        RenderingDevice rd = RenderingServer.GetRenderingDevice();

        Rid shaderRid = ShaderHelper.CreateComputeShader("res://Shaders/Compute/MinMaxErrorMapCompute.glsl");

        var terrainParams = new TerrainParams()
        {
            TerrainHeightScale = heightScale,
            ChunkSize = chunkSize,
        };

        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref terrainParams, 1));
        Rid parmBufferRid = rd.UniformBufferCreate((uint)bytes.Length, bytes);

        // create set 
        var parmabufferBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.UniformBuffer,
            Binding = 1
        };
        parmabufferBinding.AddId(parmBufferRid);

        var samplerState = new RDSamplerState()
        {
            MagFilter = RenderingDevice.SamplerFilter.Linear,
            MinFilter = RenderingDevice.SamplerFilter.Nearest,
        };
        Rid samplerRid = rd.SamplerCreate(samplerState);

        var heightmapBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.SamplerWithTexture,
            Binding = 0
        };
        heightmapBinding.AddId(samplerRid);
        heightmapBinding.AddId(heightmap.Rid);

        var minMaxErrorMapBinding = new RDUniform()
        {
            UniformType = RenderingDevice.UniformType.Image,
            Binding = 2
        };

        // create minMaxErrorMap
        int mipWidth = (width + 1) / LeafNodeSize;
        int mipHeight = (height + 1) / LeafNodeSize;
        for (int lodLevel = 0; lodLevel < lodCount; lodLevel++)
        {
            var minMaxErrorMapFormat = new GDTextureDesc()
            {
                Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
                Width = (uint)mipWidth,
                Height = (uint)mipHeight,
                Mipmaps = 1,
                UsageBits = RenderingDevice.TextureUsageBits.CanUpdateBit | RenderingDevice.TextureUsageBits.CanCopyFromBit |
                            RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit
            };
            minMaxErrorMapTextures.Add(GDTexture2D.Create(minMaxErrorMapFormat));
            minMaxErrorMapBinding.AddId(minMaxErrorMapTextures.Last().Rid);

            mipWidth = (mipWidth + 1) / 2;
            mipHeight = (mipHeight + 1) / 2;
        }
        // Fill the remaining slots.
        for (int lodLevel = lodCount; lodLevel < MaxLodLevel; lodLevel++)
        {
            minMaxErrorMapBinding.AddId(minMaxErrorMapTextures[0].Rid);
        }
        Rid set = rd.UniformSetCreate([heightmapBinding, parmabufferBinding, minMaxErrorMapBinding], shaderRid, 0);
        Rid pipeline = rd.ComputePipelineCreate(shaderRid);
        var computeList = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(computeList, pipeline);
        rd.ComputeListBindUniformSet(computeList, set, 0);

        int lodWidth = (width + 1) / LeafNodeSize;
        int lodHeight = (height + 1) / LeafNodeSize;
        for (int lod = 0; lod < lodCount; lod++)
        {
            ReadOnlySpan<byte> pushConstant = MemoryMarshal.Cast<uint, byte>([(uint)lod, 1u, 1u, 1u]);
            rd.ComputeListSetPushConstant(computeList, pushConstant, (uint)pushConstant.Length);
            rd.ComputeListDispatch(computeList, (uint)Math.Ceiling(lodWidth / 8.0), (uint)Math.Ceiling(lodHeight / 8.0), 1);
            rd.ComputeListAddBarrier(computeList);
            lodWidth = (lodWidth + 1) / 2;
            lodHeight = (lodHeight + 1) / 2;
        }
        rd.ComputeListEnd();
        rd.FreeRid(shaderRid);
        rd.FreeRid(parmBufferRid);
        rd.FreeRid(samplerRid);
        return minMaxErrorMapTextures;
    }
    public static MinMaxErrorMap[] Build(in GDTexture2D heightmap, float heightScale, uint chunkSize, int nLod = 1)
    {
        List<GDTexture2D> minMaxErrorMapTextures = BuildTextures(heightmap, heightScale, chunkSize, nLod);
        var rd = RenderingServer.GetRenderingDevice();
        var minMaxErrorMaps = new MinMaxErrorMap[nLod];
        for (int i = 0; i < nLod; ++i)
        {
            minMaxErrorMaps[i] = new MinMaxErrorMap()
            {
                Data = rd.TextureGetData(minMaxErrorMapTextures[i].Rid, 0),
                Width = minMaxErrorMapTextures[i].Width,
                Height = minMaxErrorMapTextures[i].Height,
            };
        }

        foreach (var texture in minMaxErrorMapTextures)
        {
            texture.Dispose();
        }

        return minMaxErrorMaps;
    }
}
