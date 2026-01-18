
using Godot;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using SVec3 = System.Numerics.Vector3;

public static class MathExtension
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint EncodeMorton(int x, int y)
    {
        return InterleaveBits((uint)x) | (InterleaveBits((uint)y) << 1);
    }

    private static uint InterleaveBits(uint v)
    {
        v = (v | (v << 8)) & 0x00FF00FF;
        v = (v | (v << 4)) & 0x0F0F0F0F;
        v = (v | (v << 2)) & 0x33333333;
        v = (v | (v << 1)) & 0x55555555;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (int x, int y) DecodeMorton(uint m)
    {
        // 偶数位合并为 x，奇数位合并为 y
        return (
            (int)DeinterleaveBits(m),      // 提取 0, 2, 4, 6... 位
            (int)DeinterleaveBits(m >> 1)  // 提取 1, 3, 5, 7... 位
        );
    }

    /// <summary>
    /// 位分离：将原本分散在 0, 2, 4... 位置的位挤压回连续的 0, 1, 2... 位置
    /// </summary>
    private static uint DeinterleaveBits(uint x)
    {
        // 初始掩码：0101 0101 0101 0101 0101 0101 0101 0101 (0x55555555)
        x &= 0x55555555;

        // 逐步通过位移和掩码将位“向左对齐”挤压
        // x = ---- ---- ---- ---- abcd efgh ijkl mnop (原始位分布在 0, 2, 4...)
        x = (x | (x >> 1)) & 0x33333333; // 每 2 位一组，取第 0 位并靠拢
        x = (x | (x >> 2)) & 0x0F0F0F0F; // 每 4 位一组，取前 2 位并靠拢
        x = (x | (x >> 4)) & 0x00FF00FF; // 每 8 位一组...
        x = (x | (x >> 8)) & 0x0000FFFF; // 每 16 位一组...

        return x;
    }

    public static int GetNextPowerOfTwo(int x)
    {
        if (x < 1)
        {
            return 1;  // 如果输入小于1，返回1
        }

        // 如果已经是2的幂，直接返回
        if ((x & (x - 1)) == 0)
        {
            return x;
        }

        // 向上找到最接近的2的幂
        int powerOfTwo = 1;
        while (powerOfTwo < x)
        {
            powerOfTwo <<= 1;
        }
        return powerOfTwo;
    }

    public static Vector4 ToVector4(this Plane plane)
    {
        // godot的frustum plane 法线指向视锥体外, 在这里反转法线向量
        return new Vector4(plane.X, plane.Y, plane.Z, -plane.D);
    }

    public enum IntersectType
    {
        Inside,
        Outside,
        Intersect
    }
    public static IntersectType FrustumCullAABB(Vector3 boundsMin, Vector3 boundsMax, ReadOnlySpan<Plane> frustumPlanes)
    {
        Vector3 center = (boundsMin + boundsMax) * 0.5f;
        Vector3 extents = boundsMax - center;
        //extents = new Vector3(Math.Abs(extents.X), Math.Abs(extents.Y), Math.Abs(extents.Z));
        bool isFullyInside = true;

        foreach (Plane plane in frustumPlanes)
        {
            // 计算AABB在平面法线方向上的投影半径
            float radius = extents.X * Math.Abs(plane.X) +
                          extents.Y * Math.Abs(plane.Y) +
                          extents.Z * Math.Abs(plane.Z);
            // godot的frustum plane 法线指向视锥体外, 在这里反转法线向量
            Vector3 normal = -plane.Normal;
            float distance = normal.Dot(center) + plane.D;

            // 完全在平面负半空间
            if (distance < -radius)
            {
                return IntersectType.Outside;
            }

            // 与平面相交或完全在正半空间
            if (distance <= radius)
            {
                isFullyInside = false;
            }
        }

        return isFullyInside ? IntersectType.Inside : IntersectType.Intersect;
    }

    //public static float MinDistanceFromPointToAabbSquare(
    //in Vector3 boundsMin, in Vector3 boundsMax, in Vector3 point)
    //{
    //    float cx = Math.Clamp(point.X, boundsMin.X, boundsMax.X);
    //    float cy = Math.Clamp(point.Y, boundsMin.Y, boundsMax.Y);
    //    float cz = Math.Clamp(point.Z, boundsMin.Z, boundsMax.Z);

    //    float dx = point.X - cx;
    //    float dy = point.Y - cy;
    //    float dz = point.Z - cz;

    //    return dx * dx + dy * dy + dz * dz;
    //}

    public static IntersectType BoxIntersect(Vector3 centerPos, Vector3 extent, ReadOnlySpan<Vector4> frustumPlanes)
    {
        for (int i = 0; i < 6; ++i)
        {
            Vector4 plane = frustumPlanes[i];
            Vector3 absNormal = new(Math.Abs(plane.X), Math.Abs(plane.Y), Math.Abs(plane.Z));
            if ((centerPos.Dot(new Vector3(plane.X, plane
                .Y, plane.Z)) - absNormal.Dot(extent)) > -plane.W)
            {
                return IntersectType.Outside;
            }
        }
        return IntersectType.Intersect;
    }

    public static float MinDistanceFromPointToAabbSquare(
        in Vector3 boundsMin, in Vector3 boundsMax, in Vector3 point)
    {
        SVec3 min = new SVec3(boundsMin.X, boundsMin.Y, boundsMin.Z);
        SVec3 max = new SVec3(boundsMax.X, boundsMax.Y, boundsMax.Z);
        SVec3 p = new SVec3(point.X, point.Y, point.Z);

        // SIMD clamp
        SVec3 clamped = SVec3.Min(SVec3.Max(p, min), max);
        SVec3 d = p - clamped;
        return SVec3.Dot(d, d);
    }

    public static float MinDistanceFromPointToAabb(
        in Vector3 boundsMin, in Vector3 boundsMax, in Vector3 point)
    {
        return MathF.Sqrt(MinDistanceFromPointToAabbSquare(boundsMin, boundsMax, point));
    }

}