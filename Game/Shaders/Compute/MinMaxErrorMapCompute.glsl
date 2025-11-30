#[compute]
#version 450
#extension GL_EXT_scalar_block_layout : enable

layout(binding = 0) uniform sampler2D heightmap;

layout(binding = 1, std430) uniform TerrainParams
{
	float heightScale;
	uint baseChunkSize;
};

layout(binding = 2, rgba32f) uniform image2D minMaxErrorMapMips[8];

layout(push_constant) uniform PushConstant
{
	uint currentLod;
};

layout (local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

float getHeight(uint x, uint y)
{
	ivec2 heightmapSize = textureSize(heightmap, 0);

	x = clamp(x, 0, heightmapSize.x);
	y = clamp(y, 0, heightmapSize.y);
	vec2 uv = vec2(x, y) / heightmapSize;
	float height = texture(heightmap, uv).r * heightScale;
	return height;
}

void getAreaMinMaxHeight(uint startX, uint startY, uint sizeX, uint sizeY,
        out float minHeight, out float maxHeight)
{
	float minH = 999.0;
	float maxH = -1.0;

	for (uint y = startY; y <= startY + sizeY; ++y)
	{
		for (uint x = startX; x <= startX + sizeX; ++x)
		{
			float height = getHeight(x, y);
			minH = min(minH, height);
			maxH = max(maxH, height);
		}
	}

	minHeight = minH;
	maxHeight = maxH;
}

float getGeometricError(uint startX, uint startY, uint sizeX, uint sizeY)
{
	float maxError = 0.0;
    int stride = 1 << currentLod;
    int halfStride = stride / 2;

	for (uint y = startY; y <= startY + sizeY; y += stride)
    {
        for (uint x = startX + halfStride; x <= startX + sizeX - halfStride; x += stride)
        {
            float height = getHeight(x, y);
            float left = getHeight(x - halfStride, y);
            float right = getHeight(x + halfStride, y);
            float simplifiedHeight = (left + right) / 2;
            float error = abs(simplifiedHeight - height);
            maxError = max(maxError, error);
        }
    }

    for (uint y = startY + halfStride; y <= startY + sizeY - halfStride; y += stride)
    {
        for (uint x = startX; x <= startX + sizeX; x += stride)
        {
            float height = getHeight(x, y);
            float up = getHeight(x, y + halfStride);
            float down = getHeight(x, y - halfStride);
            float simplifiedHeight = (up + down) / 2;
            float error = abs(simplifiedHeight - height);
            maxError = max(maxError, error);
        }
    }

    for (uint y = startY + halfStride; y <= startY + sizeY - halfStride; y += stride)
    {
        for (uint x = startX + halfStride; x <= startX + sizeX - halfStride; x += stride)
        {
            float height = getHeight(x, y);
            float upLeft = getHeight(x - halfStride, y + halfStride);
            float downRight = getHeight(x + halfStride, y - halfStride);
            float simplifiedHeight = (upLeft + downRight) / 2;
            float error = abs(simplifiedHeight - height);
            maxError = max(maxError, error);
        }
    }

    return maxError;
}


void createBaseMinMaxErrorMap(uint chunkX, uint chunkY)
{
    uint startX = chunkX * baseChunkSize;
    uint startY= chunkY * baseChunkSize;

    uint chunkVertices = baseChunkSize + 1;

    float minHeight;
    float maxHeight;
    getAreaMinMaxHeight(startX, startY, chunkVertices, chunkVertices, minHeight, maxHeight);
    float error = getGeometricError(startX, startY, chunkVertices, chunkVertices);
    imageStore(minMaxErrorMapMips[currentLod], ivec2(chunkX, chunkY), vec4(minHeight, maxHeight, error, 1.0));
}

void createFromLowerMinMaxErrorMap(uint chunkX, uint chunkY)
{
    uint lowerChunkX = chunkX << 1;
    uint lowerChunkY = chunkY << 1;

    float minHeight = 999.0;
    float maxHeight = -1.0;
    for (uint x = lowerChunkX; x < lowerChunkX + 2; ++x)
    {
        for (uint y = lowerChunkY; y < lowerChunkY + 2; ++y)
        {
            vec2 minMax = imageLoad(minMaxErrorMapMips[currentLod-1], ivec2(x, y)).xy;
            minHeight = min(minHeight, minMax.x);
            maxHeight = max(maxHeight, minMax.y);
        }
    }

    uint startX = chunkX * (baseChunkSize << currentLod);
    uint startY = chunkY * (baseChunkSize << currentLod);
    uint chunkVertices = baseChunkSize << currentLod + 1;
    float error = getGeometricError(startX, startY, chunkVertices, chunkVertices);
    imageStore(minMaxErrorMapMips[currentLod], ivec2(chunkX, chunkY), vec4(minHeight, maxHeight, error, 1.0));
}


void main()
{
	ivec2 minMaxErrorMapSize = imageSize(minMaxErrorMapMips[currentLod]);
    if (gl_GlobalInvocationID.x >= minMaxErrorMapSize.x || gl_GlobalInvocationID.y >= minMaxErrorMapSize.y)
    {
        return;
    }

    if (currentLod == 0)
    {
        createBaseMinMaxErrorMap(gl_GlobalInvocationID.x, gl_GlobalInvocationID.y);
    }
    else
    {
        createFromLowerMinMaxErrorMap(gl_GlobalInvocationID.x, gl_GlobalInvocationID.y);
    }
}




