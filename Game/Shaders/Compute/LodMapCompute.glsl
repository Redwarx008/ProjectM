#[compute]
#version 450
#extension GL_EXT_scalar_block_layout : enable

layout (local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout (set = 0, binding = 0, r8) uniform image2D lodMap;

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

layout (push_constant) uniform PushConstants
{
    int lodCount;
};


uint getNodeDescIndex(uvec2 nodeLocation, uint lod)
{
    return nodeIndexOffsetPerLod[lod] + nodeLocation.y * nodeCountPerLod[lod].x + nodeLocation.x;
}


void main()
{
    uvec2 nodeLocInLod0 = uvec2(gl_GlobalInvocationID.x, gl_GlobalInvocationID.y);
    if (nodeLocInLod0.x >= nodeCountPerLod[0].x || nodeLocInLod0.y >= nodeCountPerLod[0].y)
    {
        return;
    }

    for (int lod = lodCount - 1; lod >= 0; --lod)
    {
        uint nodeSize = 1 << lod;
        uvec2 nodeLoc = nodeLocInLod0 / nodeSize;
        uint nodeIndex = getNodeDescIndex(nodeLoc, lod);
        NodeDescriptor desc = nodeDescriptors[nodeIndex];
        if (desc.subdivided == 0)
        {
            imageStore(lodMap, ivec2(nodeLocInLod0), vec4(float(lod) / 8, 0, 0, 0));
            return;
        }
    }
    imageStore(lodMap, ivec2(nodeLocInLod0), vec4(0, 0, 0, 0));
}