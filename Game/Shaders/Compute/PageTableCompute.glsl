#[compute]
#version 450
#extension GL_EXT_scalar_block_layout : enable

precision highp float;
precision highp int;

layout (local_size_x = 64, local_size_y = 1, local_size_z = 1) in;


layout(set = 0, binding = 0, rg16f) uniform image2D pageTable[8];

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
	int index = counterDecrement - 1;
	PageTableUpdateEntry entry = pendingPageTableUpdates.data[index];
	imageStore(pageTable[entry.mip], ivec2(entry.x, entry.y), vec4(entry.physicalLayer, entry.activeMip, 0, 0));
}
