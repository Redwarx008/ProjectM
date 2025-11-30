#[compute]
#version 450
#extension GL_EXT_scalar_block_layout : enable

precision highp float;
precision highp int;

layout (local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

struct InstancedParam
{
    vec4 rowMajorMatrix[3];
    vec4 customData;
};

layout(set = 0, binding = 0) buffer InstancedParams
{
	InstancedParam data[];
} instancedParams;

layout(set = 0, binding = 1) uniform TerrainParams
{
	uint heightmapSizeX;
	uint heightmapSizeY;
	float heightScale;
	uint leafNodeSize;
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
layout (set = 0, binding = 2, std430)  buffer DrawIndexedIndirectCommandBuffer
{
	DrawIndexedIndirectCommand drawIndirectCommand;
};



struct NodeSelectedInfo
{
	uvec2 position;
	vec2 minMaxHeight;
	uint lodLevel;
	uint padding;
};

layout(set = 0, binding = 3, std430) buffer PendingNodeSelectedList 
{
	int count;
	NodeSelectedInfo data[];
} pendingNodeList;

void main()
{
	uint index = gl_GlobalInvocationID.x;
	int counter = atomicAdd(pendingNodeList.count, -1);
	if (counter <= 0)
	{
		return;
	}
	uvec2 nodeXY = pendingNodeList.data[index].position;

	uint nodeSize = leafNodeSize << pendingNodeList.data[index].lodLevel;
	uvec2 nodeStartXY = nodeXY * nodeSize;
	vec2 minMaxHeight = pendingNodeList.data[index].minMaxHeight;
	// set draw indirect buffer 
	uint instanceIndex = atomicAdd(drawIndirectCommand.instanceCount, 1);
//	drawIndirectCommand.indexCount = leafNodeSize * leafNodeSize * 6;
	uint lodLevel = pendingNodeList.data[index].lodLevel;
	//float morphValue = 2 * maxScreenSpaceError / tolerableError - 1;
	//finalNodeList.data[index] = vec4(vec2(nodeXY), float(lodLevel), morphValue);
	instancedParams.data[instanceIndex].rowMajorMatrix[0] = vec4(1, 0, 0, float(nodeStartXY.x));
	instancedParams.data[instanceIndex].rowMajorMatrix[1] = vec4(0, 1, 0, 0);
	instancedParams.data[instanceIndex].rowMajorMatrix[2] = vec4(0, 0, 1, float(nodeStartXY.y));
	instancedParams.data[instanceIndex].customData = vec4(float(lodLevel), 0, 0, 0);
}



