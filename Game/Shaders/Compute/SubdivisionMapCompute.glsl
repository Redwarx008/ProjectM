#[compute]
#version 450
#extension GL_EXT_scalar_block_layout : enable


layout (local_size_x = 1, local_size_y = 1, local_size_z = 1) in;

layout (set = 0, binding = 0, std430) uniform NodeDescriptorLocationInfo
{
    uint nodeIndexOffsetPerLod[8];
    uvec2 nodeCountPerLod[8];
};

struct NodeDescriptor
{
    uint subdivided;
};

layout (set = 0, binding = 1, std430) buffer NodeDescriptorBuffer
{
    NodeDescriptor nodeDescriptors[];
};

struct NodeSubdivisionInfo
{
    uvec2 position;
    uint subdivided;
    uint lodLevel;
};

layout (set = 0, binding = 2) buffer NodeSubdivisionInfoBuffer
{
    int count;
    NodeSubdivisionInfo data[];
}nodeSubdivisionInfos;



uint getNodeDescIndex(uvec2 nodeLocation, uint lod)
{
    return nodeIndexOffsetPerLod[lod] + nodeLocation.y * nodeCountPerLod[lod].x + nodeLocation.x;
}

void main()
{
    uint index = gl_GlobalInvocationID.x;
	atomicAdd(nodeSubdivisionInfos.count, -1);
	if (nodeSubdivisionInfos.count < 0)
	{
		return;
	}
    uvec2 nodeXY = nodeSubdivisionInfos.data[index].position;
    uint nodeIndex = getNodeDescIndex(nodeXY,  nodeSubdivisionInfos.data[index].lodLevel);
	nodeDescriptors[nodeIndex].subdivided = nodeSubdivisionInfos.data[index].subdivided;
}