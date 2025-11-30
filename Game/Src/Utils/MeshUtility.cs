
using Godot;

public static class MeshUtility
{
        /// <summary>
    /// The position of godot's PlaneMesh is at the midpoint of the geometry, 
    /// and the position of this PlaneMesh is at (0,0,0) in its model space.
    /// </summary>
    /// <param name="dimensionX"></param>
    /// <param name="dimensionY"></param>
    /// <param name="sizeX"></param>
    /// <param name="sizeY"></param>
    /// <returns></returns>
    public static Mesh CreatePlaneMesh(int dimensionX, int dimensionY, float sizeX, float sizeY)
    {
        float strideX = sizeX / dimensionX;
        float strideY = sizeY / dimensionY;

        int verticesCountX = dimensionX + 1;
        int verticesCountY = dimensionY + 1;

        Vector3[] vertices = new Vector3[verticesCountX * verticesCountY];
        int[] indices = new int[(verticesCountX - 1) * (verticesCountY - 1) * 6];
        Vector2[] uvs = new Vector2[verticesCountX * verticesCountY];
        Vector3[] normals = new Vector3[verticesCountX * verticesCountY];

        int vertexCount = 0;
        int indexCount = 0;
        for (int y = 0; y < verticesCountY; y++)
        {
            for (int x = 0; x < verticesCountX; x++)
            {
                vertices[vertexCount] = new Vector3(x * strideX, 0, y * strideY);
                uvs[vertexCount] = new Vector2((float)x / dimensionX, (float)y / dimensionY);
                normals[vertexCount] = new Vector3(0, 1, 0);
                ++vertexCount;
            }
        }
        for (int y = 0; y < verticesCountY - 1; ++y)
        {
            for (int x = 0; x < verticesCountX - 1; ++x)
            {
                int baseIndex = y * verticesCountX + x;
                indices[indexCount++] = baseIndex + 1;
                indices[indexCount++] = baseIndex + 1 + verticesCountX;
                indices[indexCount++] = baseIndex + verticesCountX;
                indices[indexCount++] = baseIndex + 1;
                indices[indexCount++] = baseIndex + verticesCountX;
                indices[indexCount++] = baseIndex;
            }
        }
        Godot.Collections.Array meshData = new();
        meshData.Resize((int)Mesh.ArrayType.Max);
        meshData[(int)Mesh.ArrayType.Vertex] = vertices;
        meshData[(int)Mesh.ArrayType.Index] = indices;
        meshData[(int)Mesh.ArrayType.TexUV] = uvs;
        meshData[(int)Mesh.ArrayType.Normal] = normals;
        ArrayMesh mesh = new();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, meshData);
        return mesh;
    }
}