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

struct DrawIndexedIndirectCommand
{
	uint indexCount;
	uint instanceCount;
	uint firstIndex;
	uint vertexOffset;
	uint firstInstance;
};

// Same layout as VkDrawIndexedIndirectCommand
layout (set = 0, binding = 1, std430)  buffer DrawIndexedIndirectCommandBuffer
{
	DrawIndexedIndirectCommand drawIndirectCommand;
};

struct NodeSelectedInfo
{
	uvec2 position;
	vec2 minMaxHeight;
	uint lodLevel;
	uint subdivided;
};

layout(set = 0, binding = 2) uniform TerrainParams
{
	uint leafNodeSize;
};

layout(set = 0, binding = 3, std430) buffer PendingNodeSelectedList 
{
	int count;
	NodeSelectedInfo data[];
} pendingNodeList;


layout (set = 1, binding = 0, std430) buffer NodeDescriptorLocationInfo
{
	uint nodeIndexOffsetPerLod[10];
	uvec2 nodeCountPerLod[10];
};

struct NodeDescriptor
{
	uint subdivided;
};

layout (set = 1, binding = 1, std430) buffer NodeDescriptorBuffer
{
	NodeDescriptor nodeDescriptors[];
};

uint getNodeDescIndex(uvec2 nodeLocation, uint lod)
{
	return nodeIndexOffsetPerLod[lod] + nodeLocation.y * nodeCountPerLod[lod].x + nodeLocation.x;
}

void main()
{
	if (gl_GlobalInvocationID.x >= pendingNodeList.count)
	{
		return;
	}
	uint index = gl_GlobalInvocationID.x;

	uvec2 nodeXY = pendingNodeList.data[index].position;
	uint lodLevel = pendingNodeList.data[index].lodLevel;
	if(pendingNodeList.data[index].subdivided == 0)
	{
		uint nodeSize = leafNodeSize << pendingNodeList.data[index].lodLevel;
		uvec2 nodeStartXY = nodeXY * nodeSize;
		vec2 minMaxHeight = pendingNodeList.data[index].minMaxHeight;

		// set draw indirect buffer 
		uint instanceIndex = atomicAdd(drawIndirectCommand.instanceCount, 1);

		instancedParams.data[instanceIndex].rowMajorMatrix[0] = vec4(1, 0, 0, float(nodeStartXY.x));
		instancedParams.data[instanceIndex].rowMajorMatrix[1] = vec4(0, 1, 0, 0);
		instancedParams.data[instanceIndex].rowMajorMatrix[2] = vec4(0, 0, 1, float(nodeStartXY.y));
		instancedParams.data[instanceIndex].customData = vec4(float(lodLevel), float(nodeXY.x), float(nodeXY.y), 0);		
	}

	// Fill NodeDescriptor buffer
	uint nodeIndex = getNodeDescIndex(nodeXY,  pendingNodeList.data[index].lodLevel);
	nodeDescriptors[nodeIndex].subdivided = pendingNodeList.data[index].subdivided;
}
