using System;
using Godot;

public class TerrainMesh : IDisposable
{
    private Rid _multiMeshRid;
    private Rid _instanceRid;
    private Mesh? _mesh;
    private bool _visible = true;
    private ShaderMaterial? _material;

    public ShaderMaterial? Material
    {
        get => _material;
        set
        {
            if (_material != value)
            {
                _material = value;
                Rid materialRid = Constants.NullRid;
                if (_material != null)
                {
                    materialRid = _material.GetRid();
                }
                RenderingServer.InstanceGeometrySetMaterialOverride(_instanceRid, materialRid);
            }
        }
    }
    

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible != value)
            {
                _visible = value;
                RenderingServer.InstanceSetVisible(_instanceRid, value);
            }
        }
    }
    public Rid GetDrawIndirectBuffer()
    {
        return RenderingServer.MultimeshGetCommandBufferRdRid(_multiMeshRid);
    }

    public Rid GetInstanceBuffer()
    {
        return RenderingServer.MultimeshGetBufferRdRid(_multiMeshRid);
    }

    private TerrainMesh()
    {
    }

    public static TerrainMesh Create(int chunkCount, int dimension, float size, World3D world)
    {
        var mesh = new TerrainMesh();
        mesh._multiMeshRid = RenderingServer.MultimeshCreate();
        mesh._instanceRid = RenderingServer.InstanceCreate2(mesh._multiMeshRid, world.Scenario);
        RenderingServer.MultimeshAllocateData(mesh._multiMeshRid, chunkCount,
            RenderingServer.MultimeshTransformFormat.Transform3D, false, true, true);
        mesh._mesh = CreatePlaneMesh(dimension, size);
        RenderingServer.MultimeshSetMesh(mesh._multiMeshRid, mesh._mesh.GetRid());
        RenderingServer.InstanceSetIgnoreCulling(mesh._instanceRid, true);
        RenderingServer.InstanceSetCustomAabb(mesh._instanceRid, new Aabb(Vector3.Zero, new Vector3(chunkCount * size, 1000, chunkCount * size)));
        return mesh;
    }

    private static ArrayMesh CreatePlaneMesh(int dimension, float size)
    {
        float stride = size / dimension;

        int verticesCount = dimension + 1;

        Vector3[] vertices = new Vector3[verticesCount * verticesCount];
        int[] indices = new int[dimension * dimension * 6];
        Vector3[] normals = new Vector3[verticesCount * verticesCount];
        Vector2[] directions = new Vector2[verticesCount * verticesCount];

        int vertexCount = 0;
        for (int y = 0; y < verticesCount; y++)
        {
            for (int x = 0; x < verticesCount; x++)
            {
                vertices[vertexCount] = new Vector3(x * stride, 0, y * stride);
                directions[vertexCount] = new Vector2(x % 2, y % 2);
                normals[vertexCount] = new Vector3(0, 1, 0);
                vertexCount++;
            }
        }

        int indexCount = 0;
        for (int y = 0; y < verticesCount - 1; ++y)
        {
            for (int x = 0; x < verticesCount - 1; ++x)
            {
                int baseIndex = y * verticesCount + x;
                indices[indexCount++] = baseIndex + 1;
                indices[indexCount++] = baseIndex + 1 + verticesCount;
                indices[indexCount++] = baseIndex + verticesCount;
                indices[indexCount++] = baseIndex + 1;
                indices[indexCount++] = baseIndex + verticesCount;
                indices[indexCount++] = baseIndex;
            }
        }

        Godot.Collections.Array meshData = new();
        meshData.Resize((int)Godot.Mesh.ArrayType.Max);
        meshData[(int)Godot.Mesh.ArrayType.Vertex] = vertices;
        meshData[(int)Godot.Mesh.ArrayType.Index] = indices;
        meshData[(int)Godot.Mesh.ArrayType.Normal] = normals;
        meshData[(int)Godot.Mesh.ArrayType.TexUV] = directions;

        Godot.ArrayMesh mesh = new();
        mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, meshData);
        return mesh;
    }

    private static ArrayMesh CreateSkirtMesh(int dimension, float size, float skirtHeight = 0)
    {
        float stride = size / dimension;

        int verticesCount = dimension + 1;

        // Number of vertices for each side.
        int verticesPerEdge = dimension + 1;

        // Total vertices are calculated for the perimeter of the rectangle.
        int perimeterVerticesCount = (verticesPerEdge * 2) * 2 - 4;
        Vector3[] vertices = new Vector3[perimeterVerticesCount * 2];
        Vector2[] directions = new Vector2[perimeterVerticesCount * 2];
        int[] indices = new int[perimeterVerticesCount * 6];

        int vertexCount = 0;
        int indexCount = 0;

        // Left edge (along Y-axis)
        for (int y = 0; y < verticesPerEdge; ++y)
        {
            vertices[vertexCount] = new Vector3(0, 0, y * stride);
            directions[vertexCount] = new Vector2(0, y % 2);
            vertexCount++;
            vertices[vertexCount] = new Vector3(0, skirtHeight, y * stride);
            directions[vertexCount] = new Vector2(0, y % 2);
            vertexCount++;
        }

        // Bottom edge (along X-axis)
        for (int x = 1; x < verticesPerEdge; ++x)
        {
            vertices[vertexCount] = new Vector3(x * stride, 0, size);
            directions[vertexCount] = new Vector2(x % 2, 0);
            vertexCount++;
            vertices[vertexCount] = new Vector3(x * stride, skirtHeight, size);
            directions[vertexCount] = new Vector2(x % 2, 0);
            vertexCount++;
        }

        // Right edge (along Y-axis, reversed)
        for (int y = verticesPerEdge - 2; y >= 0; --y)
        {
            vertices[vertexCount] = new Vector3(size, 0, y * stride);
            directions[vertexCount] = new Vector2(0, y % 2);
            vertexCount++;
            vertices[vertexCount] = new Vector3(size, skirtHeight, y * stride);
            directions[vertexCount] = new Vector2(0, y % 2);
            vertexCount++;
        }

        // Top edge (along X-axis, reversed)
        for (int x = verticesPerEdge - 2; x >= 1; --x)
        {
            vertices[vertexCount] = new Vector3(x * stride, 0, 0);
            directions[vertexCount] = new Vector2(x % 2, 0);
            vertexCount++;
            vertices[vertexCount] = new Vector3(x * stride, skirtHeight, 0);
            directions[vertexCount] = new Vector2(x % 2, 0);
            vertexCount++;
        }

        // Generate indices for the skirt
        for (int i = 0; i < perimeterVerticesCount - 1; ++i)
        {
            int index00 = i * 2;
            int index10 = index00 + 1;
            int index01 = (i + 1) * 2;
            int index11 = index01 + 1;

            // First triangle
            indices[indexCount++] = index00;
            indices[indexCount++] = index10;
            indices[indexCount++] = index11;

            // Second triangle
            indices[indexCount++] = index00;
            indices[indexCount++] = index11;
            indices[indexCount++] = index01;
        }

        // Connect the last two triangles to the beginning
        int lastVertexIndex = (perimeterVerticesCount - 1) * 2;
        indices[indexCount++] = lastVertexIndex;
        indices[indexCount++] = lastVertexIndex + 1;
        indices[indexCount++] = 1;
        indices[indexCount++] = lastVertexIndex;
        indices[indexCount++] = 1;
        indices[indexCount] = 0;

        Godot.Collections.Array meshData = new Godot.Collections.Array();
        meshData.Resize((int)Godot.Mesh.ArrayType.Max);
        meshData[(int)Godot.Mesh.ArrayType.Vertex] = vertices;
        meshData[(int)Godot.Mesh.ArrayType.Index] = indices;
        meshData[(int)Godot.Mesh.ArrayType.TexUV] = directions;

        ArrayMesh mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, meshData);

        return mesh;
    }

    public void Dispose()
    {
        RenderingServer.FreeRid(_multiMeshRid);
        RenderingServer.FreeRid(_instanceRid);
    }
}