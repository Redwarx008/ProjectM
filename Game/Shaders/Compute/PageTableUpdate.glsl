#[compute]
#version 450
#extension GL_EXT_scalar_block_layout : enable

layout (local_size_x = 1, local_size_y = 1, local_size_z = 1) in;


layout(set = 0, binding = 0, rg16ui) uniform uimage2D pageTable[8];

layout(push_constant, std430) uniform ClearParams {
	ivec2 clearOrigin;
	ivec2 clearSize;
	uint mip;
	uvec2 clearValue; // [physicalSlot, activeMip]
} params;

void main()
{

	ivec2 coord = ivec2(gl_GlobalInvocationID.xy);

	if (coord.x >= params.clearOrigin.x &&
		coord.x < params.clearOrigin.x + params.clearSize.x &&
		coord.y >= params.clearOrigin.y &&
		coord.y < params.clearOrigin.y + params.clearSize.y)
	{
		// idk how to avoid this, we can't index with not constant in glsl in AMD device.
		switch(params.mip)
		{
			case 0: imageStore(pageTable[0], coord, uvec4(params.clearValue, 0, 0)); break;
			case 1: imageStore(pageTable[1], coord, uvec4(params.clearValue, 0, 0)); break;
			case 2: imageStore(pageTable[2], coord, uvec4(params.clearValue, 0, 0)); break;
			case 3: imageStore(pageTable[3], coord, uvec4(params.clearValue, 0, 0)); break;
			case 4: imageStore(pageTable[4], coord, uvec4(params.clearValue, 0, 0)); break;
			case 5: imageStore(pageTable[5], coord, uvec4(params.clearValue, 0, 0)); break;
			case 6: imageStore(pageTable[6], coord, uvec4(params.clearValue, 0, 0)); break;
			case 7: imageStore(pageTable[7], coord, uvec4(params.clearValue, 0, 0)); break;
		}
    }
}
