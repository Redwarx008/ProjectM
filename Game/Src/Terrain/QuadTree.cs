using Godot;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using IntersectType = MathExtension.IntersectType;

public class QuadTree
{
    public struct Node
    {
        public uint x;
        public uint y;
        public uint mip;
    }

    public ref struct SelectDesc
    {
        public Span<Node> selections;
        public int selectionCount;
        public ReadOnlySpan<Vector4> planes;
        public Vector3 viewerPos;
        public ReadOnlySpan<float> lodRanges;
        public Vector3 mapSize;
        public Vector3 mapOffset;
    }

    public int TopNodeCountX { get; private set; }
    public int TopNodeCountY { get; private set; }
    public int TopNodeSize { get; private set; }
    public int LodOffset { get; private set; }
    public int MapDimX { get; private set; }
    public int MapDimY { get; private set; }

    private MinMaxMap[] _minMaxMaps;

    public QuadTree(MinMaxMap[] minMaxMaps, int leafNodeSize, int mapDimX, int mapDimY, int lodOffset = 0)
    {
        LodOffset = lodOffset;
        TopNodeSize = leafNodeSize << (minMaxMaps.Length - 1);
        TopNodeCountX = minMaxMaps[minMaxMaps.Length - 1].Width;
        TopNodeCountY = minMaxMaps[minMaxMaps.Length - 1].Height;
        MapDimX = mapDimX;
        MapDimY = mapDimY;
        _minMaxMaps = minMaxMaps;
    }

    public void Select(ref SelectDesc selectDesc)
    {
        for (int y = 0; y < TopNodeCountY; y++)
        {
            for (int x = 0; x < TopNodeCountX; x++)
            {
                int lodLevelCount = _minMaxMaps.Length;
                NodeSelect(x * TopNodeSize, y * TopNodeSize, TopNodeSize, lodLevelCount - 1, false, ref selectDesc);
            }
        }
    }

    private void NodeSelect(int x, int y, int size, int lodLevel,
        bool parentCompletelyInFrustum, ref SelectDesc selectDesc)
    {
        int nodeX = x / size;
        int nodeY = y / size;
        _minMaxMaps![lodLevel].GetMinMax(nodeX, nodeY, out ushort minZ, out ushort maxZ);
        var mapScale = new Vector3()
        {
            X = selectDesc.mapSize.X / (MapDimX - 1),
            Y = selectDesc.mapSize.Y,           // heightScale
            Z = selectDesc.mapSize.Z / (MapDimY - 1)
        };
        var boundsMin = new Vector3()
        {
            X = selectDesc.mapOffset.X + x * mapScale.X,
            Y = selectDesc.mapOffset.Y + minZ / ushort.MaxValue * mapScale.Y,
            Z = selectDesc.mapOffset.Z + y * mapScale.Z
        };
        var boundsMax = new Vector3()
        {
            X = selectDesc.mapOffset.X + (x + size) * mapScale.X,
            Y = selectDesc.mapOffset.Y + maxZ / ushort.MaxValue * mapScale.Y,
            Z = selectDesc.mapOffset.Z + (y + size) * mapScale.Z
        };
        IntersectType cullResult = parentCompletelyInFrustum ? IntersectType.Inside :
            MathExtension.FrustumCullAABB(boundsMin, boundsMax, selectDesc.planes);

        //if (cullResult == IntersectType.Outside)
        //{
        //    return;
        //}

        float distanceSq = MathExtension.MinDistanceFromPointToAabbSquare(boundsMin, boundsMax, selectDesc.viewerPos);

        float nextLodRange = selectDesc.lodRanges[Math.Max(LodOffset + lodLevel - 1, 0)];

        if (lodLevel == 0 || distanceSq > nextLodRange * nextLodRange)
        {
            if(selectDesc.selectionCount < selectDesc.selections.Length)
            {
                selectDesc.selections[selectDesc.selectionCount++] = new Node
                {
                    x = (uint)nodeX,
                    y = (uint)nodeY,
                    mip = (uint)lodLevel,
                };
            }

            return;
        }
        else
        {
            bool weAreCompletelyInFrustum = cullResult == IntersectType.Inside;

            _minMaxMaps[lodLevel - 1].GetSubNodesExist(nodeX, nodeY,
                out bool subTLExist, out bool subTRExist, out bool subBLExist, out bool subBRExist);
            int halfSize = size / 2;
            if (subTLExist)
            {
                NodeSelect(x, y, halfSize, lodLevel - 1, weAreCompletelyInFrustum, ref selectDesc);
            }
            if (subTRExist)
            {
                NodeSelect(x + halfSize, y, halfSize, lodLevel - 1, weAreCompletelyInFrustum, ref selectDesc);
            }
            if (subBLExist)
            {
                NodeSelect(x, y + halfSize, halfSize, lodLevel - 1, weAreCompletelyInFrustum, ref selectDesc);
            }
            if (subBRExist)
            {
                NodeSelect(x + halfSize, y + halfSize, halfSize, lodLevel - 1, weAreCompletelyInFrustum, ref selectDesc);
            }
        }
    }
}