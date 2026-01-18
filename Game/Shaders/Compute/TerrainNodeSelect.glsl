#[compute]
#version 450
#extension GL_EXT_scalar_block_layout : enable

precision highp float;
precision highp int;

layout (local_size_x = 1, local_size_y = 1, local_size_z = 1) in;

layout(set = 0, binding = 0, rg16) uniform image2D minMaxMaps[10];

struct InstancedParam
{
    vec4 rowMajorMatrix[3];
    vec4 customData;
};

layout(set = 0, binding = 1) buffer InstancedParams
{
	InstancedParam data[];
} instancedParams;

layout(set = 0, binding = 2, std430) uniform TerrainParams
{
	ivec2 heightmapSize;
	vec2 mapSize;
	vec2 mapOffset;
	float height;
	float heightOffset;
	uint patchSize;
};

struct DrawIndexedIndirectCommand
{
	uint indexCount;
	uint instanceCount;
	uint firstIndex;
	uint vertexOffset;
	uint firstInstance;
};

// Same layout as VkDrawIndexedIndirectCommand
layout (set = 0, binding = 3, std430)  buffer DrawIndexedIndirectCommandBuffer
{
	DrawIndexedIndirectCommand drawIndirectCommand;
};


struct Node
{
	uvec2 position;
};

layout(set = 1, binding = 0, std430) buffer ConsumeNodeList
{
	int count;
	Node data[];
} consumeNodeList;

layout(set = 1, binding = 1, std430) buffer AppendNodeList
{
	int count;
	Node data[];
} appendNodeList;

layout (push_constant) uniform PushConstants
{
	vec4 frustumPlanes[6];
	vec4 cameraPos;
	int lodLevel;
	float lodRange;
	float nextLodRange;
};

float minDistanceSqFromPointToAabb(vec3 boundsMin, vec3 boundsMax, vec3 point)
{
    vec3 d = max(max(boundsMin - point, point - boundsMax), vec3(0.0));
    return dot(d, d);
}

bool boxIntersect(vec3 centerPos, vec3 extent)
{
    for (uint i = 0; i < 6; ++i)
    {
        vec4 plane = frustumPlanes[i];
        vec3 absNormal = abs(plane.xyz);
        if ((dot(centerPos, plane.xyz) - dot(absNormal, extent)) > -plane.w)
        {
            return false;
        }
    }
    return true;
}

bool boxIntersectSphere(vec3 boundsMin, vec3 boundsMax, vec3 point, float radius)
{
	return minDistanceSqFromPointToAabb(boundsMin, boundsMax, point) <= radius * radius;
}


void main()
{
	uint index = gl_GlobalInvocationID.x;
	atomicAdd(consumeNodeList.count, -1);
	uvec2 nodeXY = consumeNodeList.data[index].position;
	uint chunkSize = patchSize << lodLevel;
	uvec2 startXY = nodeXY * chunkSize;
	if (startXY.x >= heightmapSize.x || startXY.y >= heightmapSize.y)
	{
		return;
	}
	vec2 minMax = imageLoad(minMaxMaps[lodLevel], ivec2(nodeXY)).xy * height + heightOffset;
	float size = float(chunkSize) * (mapSize / vec2(heightmapSize - uvec2(1)));
	vec2 xy = float(startXY) * size + mapOffset;
	vec3 boundsMin = vec3(xy.x * , minMax.x, xy.y);
	vec3 boundsMax = vec3(xy.x + size, minMax.y, xy.y + size);
	
//	if (!boxIntersect((boundsMin + boundsMax) * 0.5, (boundsMax - boundsMin) * 0.5))
//	{
//		return;
//	}

	if(!boxIntersectSphere(boundsMin, boundsMax, cameraPos.xyz, lodRange))
	{
		return;
	}

	if (lodLevel != 0 )
	{
		if(boxIntersectSphere(boundsMin, boundsMax, cameraPos.xyz, nextLodRange))
		{
			int index = atomicAdd(appendNodeList.count, 4);
			appendNodeList.data[index + 0].position = nodeXY * 2;
			appendNodeList.data[index + 1].position = nodeXY * 2 + uvec2(1, 0);
			appendNodeList.data[index + 2].position = nodeXY * 2 + uvec2(0, 1);
			appendNodeList.data[index + 3].position = nodeXY * 2 + uvec2(1, 1);
		}
	}
	else
	{
		// set draw indirect buffer 
		uint instanceIndex = atomicAdd(drawIndirectCommand.instanceCount, 1);

		instancedParams.data[instanceIndex].rowMajorMatrix[0] = vec4(1, 0, 0, float(xy.x));
		instancedParams.data[instanceIndex].rowMajorMatrix[1] = vec4(0, 1, 0, 0);
		instancedParams.data[instanceIndex].rowMajorMatrix[2] = vec4(0, 0, 1, float(xy.y));
		instancedParams.data[instanceIndex].customData = vec4(float(lodLevel), 0, 0, 0);
	}
}



