
using Godot;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using SVec3 = System.Numerics.Vector3;

public static class MathExtension
{
    public static Vector4 ToVector4(this Plane plane)
    {
        // godot的frustum plane 法线指向视锥体外, 在这里反转法线向量
        return new Vector4(-plane.X, -plane.Y, -plane.Z, plane.D);
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