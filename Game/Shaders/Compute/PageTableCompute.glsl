#[compute]
#version 450
#extension GL_EXT_scalar_block_layout : enable

precision highp float;
precision highp int;

layout (local_size_x = 64, local_size_y = 1, local_size_z = 1) in;


layout(set = 0, binding = 0, rg16ui) uniform uimage2D pageTable[8];

struct PageTableUpdateEntry
{
	int x;
	int y;
	int mip;
	int physicalLayer;
	int activeMip;
};

layout (set = 0, binding = 1, std430)  buffer PageTableUpdateList
{
	int count;
	PageTableUpdateEntry data[];
} pendingPageTableUpdates;

void main()
{
	int counterDecrement = atomicAdd(pendingPageTableUpdates.count, -1);
	if (counterDecrement <= 0)
	{
		return;
	}
	uint index = uint(counterDecrement - 1);
	PageTableUpdateEntry entry = pendingPageTableUpdates.data[index];
	// idk how to avoid this, we can't index with not constant in glsl in AMD device.
	switch(entry.mip)
	{
		case 0: imageStore(pageTable[0], ivec2(entry.x, entry.y), uvec4(entry.physicalLayer, entry.activeMip, 0, 0)); break;
		case 1: imageStore(pageTable[1], ivec2(entry.x, entry.y), uvec4(entry.physicalLayer, entry.activeMip, 0, 0)); break;
		case 2: imageStore(pageTable[2], ivec2(entry.x, entry.y), uvec4(entry.physicalLayer, entry.activeMip, 0, 0)); break;
		case 3: imageStore(pageTable[3], ivec2(entry.x, entry.y), uvec4(entry.physicalLayer, entry.activeMip, 0, 0)); break;
		case 4: imageStore(pageTable[4], ivec2(entry.x, entry.y), uvec4(entry.physicalLayer, entry.activeMip, 0, 0)); break;
		case 5: imageStore(pageTable[5], ivec2(entry.x, entry.y), uvec4(entry.physicalLayer, entry.activeMip, 0, 0)); break;
		case 6: imageStore(pageTable[6], ivec2(entry.x, entry.y), uvec4(entry.physicalLayer, entry.activeMip, 0, 0)); break;
		case 7: imageStore(pageTable[7], ivec2(entry.x, entry.y), uvec4(entry.physicalLayer, entry.activeMip, 0, 0)); break;
	}
}
