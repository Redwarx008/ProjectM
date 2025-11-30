shader_type spatial;
render_mode depth_draw_always;

uniform float u_heightScale;
uniform int u_baseChunkSize;
uniform uvec2 u_heightmapSize;

uniform mat4 u_terrainInverseTransform;
uniform mat3 u_inverseTransposedTransform;

uniform sampler2D u_heightmap : repeat_disable;
uniform sampler2D u_lodmap;

uniform sampler2DArray u_groundDiffuseArray : source_color;
uniform sampler2DArray u_groundNormalArray ;

uniform sampler2D u_debugGridTexture : source_color;

varying vec3 v_debugColor;

int GetBlockLod(ivec2 index)
{
	float level = round(texelFetch(u_lodmap, index, 0).r * 8.0);

	return int(level);
}
void FixSeams(vec2 vertexWorldPos, inout vec3 vertex, inout vec2 uv, int blockSize)
{
	ivec2 blockIndex = ivec2(floor((vertexWorldPos.xy + vec2(0.01, 0.01)))/ vec2(float(u_baseChunkSize), float((u_baseChunkSize))));
	//Because the right boundary belongs to the right block
	if(int(vertex.x) == blockSize)
	{
		blockIndex.x -= 1;
	}
	//Because the bottom boundary belongs to the bottom block
	if(int(vertex.z) == blockSize)
	{
		blockIndex.y -= 1;
	}
	int level = GetBlockLod(blockIndex);
	int leftLevel = GetBlockLod(blockIndex + ivec2(-1, 0));
	int rightLevel = GetBlockLod(blockIndex + ivec2(1, 0));
	int topLevel = GetBlockLod(blockIndex + ivec2(0, -1));
	int bottomLevel = GetBlockLod(blockIndex + ivec2(0, 1));

	//float debug;
	//if(level == 0)
		//color = vec4(1, 0, 0, 1);  //red
	//else if(level == 1)
		//color = vec4(0, 1, 0, 1);  //green
	//else if(level == 2)
		//color = vec4(0, 0, 1, 1);  //blue
	//else if(level == 3)
		//color = vec4(1, 0, 1, 1);  //pink
	//else if(level == 4)
		//color = vec4(1, 1, 0, 1); //yellow
	//else if(level == 5)
		//color = vec4(1, 1, 1, 1);  //white

	int blockStride = 1 << level;
	vec2 uvStride = float(blockStride) / vec2(textureSize(u_heightmap, 0));
	//int blockStrideCount = u_blockSize / blockStride;
	ivec2 vertexIndex = ivec2(floor(vertex.xz + vec2(0.01, 0.01))) / ivec2(blockStride, blockStride);
	int levelDiff = leftLevel - level;

	if(int(vertex.x) == 0 && levelDiff > 0)
	{
		//vertex.y = 20.0;
		int gridStride = 1 << levelDiff;
		int modIndex = vertexIndex.y % gridStride;
		if(modIndex != 0)
		{
			vertex.z += float(gridStride - modIndex) * float(blockStride);
			uv.y +=  uvStride.y * float(gridStride - modIndex) ;
		}
		return;
	}
	//top
	levelDiff = topLevel - level;
	if(int(vertex.z) == 0 && levelDiff > 0)
	{
		//vertex.y = 20.0;
		int gridStride = 1 << levelDiff;
		//vertex.y = float(grid_stride) * 10.0;
		int modIndex = vertexIndex.x % gridStride;
		if(modIndex != 0)
		{
			vertex.x -= float(modIndex) * float(blockStride);
			uv.x -= uvStride.x * float(modIndex) ;
		}
		return;
	}
	//right
	levelDiff = rightLevel - level;
	if(int(vertex.x) == blockSize && levelDiff > 0)
	{
		int gridStride = 1 << levelDiff;
		int modIndex = vertexIndex.y % gridStride;
		if(modIndex != 0)
		{
			vertex.z += float(gridStride - modIndex) * float(blockStride);
			uv.y +=  uvStride.y * float(gridStride - modIndex) ;
		}
		return;
	}
	//bottom
	levelDiff = bottomLevel - level;
	if(int(vertex.z) == blockSize && levelDiff > 0)
	{
		int gridStride = 1 << levelDiff;
		int modIndex = vertexIndex.x % gridStride;
		if(modIndex != 0)
		{
			vertex.x -= float(modIndex) * float(blockStride);
			uv.x -= uvStride.x * float(modIndex);
		}
		return;
	}

}
vec3 CalculateNormal(vec2 uv)
{
	vec2 texelSize = 1.0 / vec2(textureSize(u_heightmap, 0));
	float left = textureLod(u_heightmap, uv + vec2(-texelSize.x, 0), 0).r * u_heightScale;
	float right = textureLod(u_heightmap, uv + vec2(texelSize.x, 0), 0).r * u_heightScale;
	float up = textureLod(u_heightmap, uv + vec2(0, -texelSize.y), 0).r * u_heightScale;
	float down = textureLod(u_heightmap, uv + vec2(0, texelSize.y), 0).r * u_heightScale;
	return normalize(vec3(left - right, 2.0, up - down));
}

void vertex()
{
	int lodLevel = int(INSTANCE_CUSTOM.x);
	ivec2 nodeXY = ivec2(int(INSTANCE_CUSTOM.z), int(INSTANCE_CUSTOM.w));
	float chunkScale = float(1 << lodLevel);
	VERTEX.xz *= chunkScale;

	vec3 worldSpacePos = (MODEL_MATRIX * vec4(VERTEX, 1)).xyz;
	//Remove the extra vertices
	worldSpacePos.xz = min(worldSpacePos.xz, vec2(u_heightmapSize) - vec2(1.0, 1.0));
	//vec2 vertexCoord = (u_terrainInverseTransform * vec4(worldSpacePos, 1)).xz + vec2(0.5, 0.5);
	vec2 vertexCoord = worldSpacePos.xz;

	vec2 uv = vertexCoord / vec2(u_heightmapSize);
	UV = uv;
	FixSeams(vertexCoord, VERTEX, UV, u_baseChunkSize * int(chunkScale));
	worldSpacePos = (MODEL_MATRIX * vec4(VERTEX, 1)).xyz;
	worldSpacePos.xz = min(worldSpacePos.xz, vec2(u_heightmapSize) - vec2(1.0, 1.0));

	float height = textureLod(u_heightmap, worldSpacePos.xz / vec2(u_heightmapSize), 0).r * u_heightScale;
	worldSpacePos.y = height;
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(worldSpacePos, 1);
	//
	//switch(lodLevel)
	//{
	//case 0:
		//v_debugColor = vec3(0, 1, 0);
		//break;
	//case 1:
		//v_debugColor = vec3(0, 0, 1);
		//break;
	//case 2:
		//v_debugColor = vec3(1, 0, 0);
		//break;
	//case 3:
		//v_debugColor = vec3(1, 1, 0);
		//break;
	//case 4:
		//v_debugColor = vec3(0, 1, 1);
		//break;
	//case 5:
		//v_debugColor = vec3(1, 0, 1);
		//break;
	//}
	v_debugColor = vec3(1, 1, 0);
}

void fragment()
{
	//vec3 worldNormal = u_inverseTransposedTransform * CalculateNormal(UV);
	vec3 worldNormal = u_inverseTransposedTransform * CalculateNormal(UV);
	worldNormal = normalize(worldNormal);
	NORMAL = (VIEW_MATRIX * vec4(worldNormal, 0.0)).xyz;
	ALBEDO = texture(u_debugGridTexture, UV * vec2(u_heightmapSize) / 128.0 * 16.0).rgb;
}
