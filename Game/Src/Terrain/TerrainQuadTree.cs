using Godot;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using IntersectType = MathExtension.IntersectType;

public class TerrainQuadTree
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct NodeSelectedInfo
    {
        public uint X;
        public uint Y;
        public float MinHeight;
        public float MaxHeight;
        public uint LodLevel;
        public uint Subdivided;
    }

    public ref struct SelectDesc
    {
        public NodeSelectedInfo[] nodeSelectedInfos;
        public int nodeSelectedCount;
        public ReadOnlySpan<Plane> planes;
        public Vector3 viewerPos;
        public float tolerableError;
    }

    private float _kFactor;

    private int _topNodeCountX;

    private int _topNodeCountY;

    private int _topNodeSize;

    private MinMaxErrorMap[] _minMaxErrorMaps;

    public TerrainQuadTree(int leafNodeSize, float kFactor, MinMaxErrorMap[] minMaxErrorMaps)
    {
        _kFactor = kFactor;
        _topNodeSize = leafNodeSize << (minMaxErrorMaps.Length - 1);
        _topNodeCountX = (int)minMaxErrorMaps[minMaxErrorMaps.Length - 1].Width;
        _topNodeCountY = (int)minMaxErrorMaps[minMaxErrorMaps.Length - 1].Height;
        _minMaxErrorMaps = minMaxErrorMaps; 
    }

    public void Select(ref SelectDesc selectDesc)
    {
        for (int y = 0; y < _topNodeCountY; y++)
        {
            for (int x = 0; x < _topNodeCountX; x++)
            {
                int lodLevelCount = _minMaxErrorMaps.Length;
                NodeSelect(x * _topNodeSize, y * _topNodeSize, _topNodeSize, lodLevelCount - 1, false, ref selectDesc);
            }
        }
    }

    private void NodeSelect(int x, int y, int size, int lodLevel, 
        bool parentCompletelyInFrustum, ref SelectDesc selectDesc)
    {
        int nodeX = x / size;
        int nodeY = y / size;
        _minMaxErrorMaps![lodLevel].GetMinMaxError(nodeX, nodeY, out float minZ, out float maxZ, out float geometricError);
        var boundsMin = new Vector3(x, minZ, y);
        var boundsMax = new Vector3(x + size, maxZ, y + size);
        IntersectType cullResult = parentCompletelyInFrustum ? IntersectType.Inside :
            MathExtension.FrustumCullAABB(boundsMin, boundsMax, selectDesc.planes);

        if (cullResult == IntersectType.Outside)
        {
            return;
        }

        float distance = MathExtension.MinDistanceFromPointToAabb(boundsMin, boundsMax, selectDesc.viewerPos);
        float maxScreenSpaceError = geometricError / distance * _kFactor;

        // 如果到达最底层或者最大屏幕空间误差可以容忍
        if (lodLevel == 0 || maxScreenSpaceError <= selectDesc.tolerableError)
        {
            selectDesc.nodeSelectedInfos[selectDesc.nodeSelectedCount++] = new NodeSelectedInfo
            {
                X = (uint)nodeX,
                Y = (uint)nodeY,
                MinHeight = minZ,
                MaxHeight = maxZ,
                LodLevel = (uint)lodLevel,
                Subdivided = 0,
            };

            return;
        }
        else
        {
            selectDesc.nodeSelectedInfos[selectDesc.nodeSelectedCount++] = new NodeSelectedInfo
            {
                X = (uint)nodeX,
                Y = (uint)nodeY,
                LodLevel = (uint)lodLevel,
                Subdivided = 1,
            };

            bool weAreCompletelyInFrustum = cullResult == IntersectType.Inside;

            _minMaxErrorMaps[lodLevel - 1].GetSubNodesExist(nodeX, nodeY,
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