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

layout(set = 0, binding = 0) buffer PlaneInstancedParams
{
	InstancedParam data[];
} planeInstancedParams;

layout(set = 0, binding = 1) buffer SkirtInstancedParams
{
	InstancedParam data[];
} skirtInstancedParams;


struct DrawIndexedIndirectCommand
{
	uint indexCount;
	uint instanceCount;
	uint firstIndex;
	uint vertexOffset;
	uint firstInstance;
};

// Same layout as VkDrawIndexedIndirectCommand
layout (set = 0, binding = 2, std430)  buffer PlaneDrawIndexedIndirectCommandBuffer
{
	DrawIndexedIndirectCommand command;
} planeDrawCommand;

layout (set = 0, binding = 3, std430)  buffer SkirtDrawIndexedIndirectCommandBuffer
{
	DrawIndexedIndirectCommand command;
} skirtDrawCommand;

struct NodeSelectedInfo
{
	uvec2 position;
	uint lodLevel;
	float morphValue;
};

layout(set = 0, binding = 4) uniform TerrainParams
{
	uint leafNodeSize;
};

layout(set = 0, binding = 5, std430) buffer PendingNodeSelectedList 
{
	int count;
	NodeSelectedInfo data[];
} pendingNodeList;



void main()
{
	if (gl_GlobalInvocationID.x >= pendingNodeList.count)
	{
		return;
	}
	uint index = gl_GlobalInvocationID.x;

	uvec2 nodeXY = pendingNodeList.data[index].position;
	uint lodLevel = pendingNodeList.data[index].lodLevel;

	uint nodeSize = leafNodeSize << pendingNodeList.data[index].lodLevel;
	uvec2 nodeStartXY = nodeXY * nodeSize;
	float morphValue = pendingNodeList.data[index].morphValue;
	// set draw indirect buffer 
	uint planeInstanceIndex = atomicAdd(planeDrawCommand.command.instanceCount, 1);

	planeInstancedParams.data[planeInstanceIndex].rowMajorMatrix[0] = vec4(1, 0, 0, float(nodeStartXY.x));
	planeInstancedParams.data[planeInstanceIndex].rowMajorMatrix[1] = vec4(0, 1, 0, 0);
	planeInstancedParams.data[planeInstanceIndex].rowMajorMatrix[2] = vec4(0, 0, 1, float(nodeStartXY.y));
	planeInstancedParams.data[planeInstanceIndex].customData = vec4(float(lodLevel), morphValue, 0, 0);	

	uint skirtInstanceIndex = atomicAdd(skirtDrawCommand.command.instanceCount, 1);

	skirtInstancedParams.data[skirtInstanceIndex].rowMajorMatrix[0] = vec4(1, 0, 0, float(nodeStartXY.x));
	skirtInstancedParams.data[skirtInstanceIndex].rowMajorMatrix[1] = vec4(0, 1, 0, 0);
	skirtInstancedParams.data[skirtInstanceIndex].rowMajorMatrix[2] = vec4(0, 0, 1, float(nodeStartXY.y));
	skirtInstancedParams.data[skirtInstanceIndex].customData = vec4(float(lodLevel), morphValue, 0, 0);	
}
