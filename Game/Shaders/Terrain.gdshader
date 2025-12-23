shader_type spatial;
render_mode depth_draw_always;

uniform float u_heightScale;
uniform int u_baseChunkSize;

uniform uvec2 u_heightmapSize;

uniform mat4 u_terrainInverseTransform;
uniform mat3 u_inverseTransposedTransform;

//Indirection Texture
//format: rg32f
//r: Physical Layer Index
//g: Active LOD
uniform usampler2D u_VTPageTable[8] : filter_nearest, repeat_disable;
// Array Layer = Physical Slot Index
uniform sampler2DArray u_VTPhysicalHeightmap : repeat_disable;

uniform int u_PagePadding;
uniform int u_HeightmapLodOffset;

uniform sampler2D u_heightmap : repeat_disable;
uniform sampler2D u_lodmap;

uniform sampler2DArray u_groundDiffuseArray : source_color;
uniform sampler2DArray u_groundNormalArray ;

uniform sampler2D u_debugGridTexture : source_color;

varying vec4 v_debugColor;

varying flat int v_RequestHeightMip;

uvec4 SamplePageTable(vec2 globalUv, int requestMip)
{
	// idk how to avoid this, we can't index with not constant in glsl in AMD device.
	switch(requestMip)
	{
		case 0: return textureLod(u_VTPageTable[0], globalUv, 0);
		case 1: return textureLod(u_VTPageTable[1], globalUv, 0);
		case 2: return textureLod(u_VTPageTable[2], globalUv, 0);
		case 3: return textureLod(u_VTPageTable[3], globalUv, 0);
		case 4: return textureLod(u_VTPageTable[4], globalUv, 0);
		case 5: return textureLod(u_VTPageTable[5], globalUv, 0);
		case 6: return textureLod(u_VTPageTable[6], globalUv, 0);
		case 7: return textureLod(u_VTPageTable[7], globalUv, 0);
	}
}

ivec2 PageTableSize(uint mip)
{
	switch(mip)
	{
		case 0u: return textureSize(u_VTPageTable[0], 0);
		case 1u: return textureSize(u_VTPageTable[1], 0);
		case 2u: return textureSize(u_VTPageTable[2],  0);
		case 3u: return textureSize(u_VTPageTable[3],  0);
		case 4u: return textureSize(u_VTPageTable[4],  0);
		case 5u: return textureSize(u_VTPageTable[5],  0);
		case 6u: return textureSize(u_VTPageTable[6],  0);
		case 7u: return textureSize(u_VTPageTable[7],  0);
	}
}

vec3 GetVirtualTextureUV(vec2 globalUv, int requestMip)
{
	uvec4 pageInfo = SamplePageTable(globalUv, requestMip);

	uint physSlot = pageInfo.x;
	uint activeLod = pageInfo.y;
	// 当前 Active LOD 下，整个地形被切分为 gridDimX * gridDimY 个页面
	ivec2 gridDim = PageTableSize(activeLod);
    vec2 clampedUv = clamp(globalUv, 0.0, 1.0 - 1e-7);
    vec2 uvInPage = fract(clampedUv * vec2(gridDim));
	vec2 paddedPageSize = vec2(textureSize(u_VTPhysicalHeightmap, 0).xy);
	vec2 pageSize = paddedPageSize -  2.0 * vec2(float(u_PagePadding));
	// 算出在Physical纹理上的像素坐标 (加上左上角的 Padding)
	vec2 pixelCoord = vec2(float(u_PagePadding)) + uvInPage * pageSize;
	vec2 physUv = pixelCoord / paddedPageSize;
	return vec3(physUv, float(physSlot));
}

float SampleHeightVT(vec2 globalUv, int requestMip)
{
	vec3 virtualTextureUV = GetVirtualTextureUV(globalUv, requestMip);
	return texture(u_VTPhysicalHeightmap, virtualTextureUV).r * u_heightScale;
}

vec3 CalculateNormalVT(vec2 uv, int requestMip)
{
    // 计算采样步长 (1个像素)
    vec2 texelSize = 1.0 / vec2(u_heightmapSize);

    // 使用 SampleHeightVT 替代 textureLod
    // 请求 Mip 0 以获得最精细的法线细节
    float left  = SampleHeightVT(uv + vec2(-texelSize.x, 0), requestMip);
    float right = SampleHeightVT(uv + vec2(texelSize.x, 0), requestMip);
    float up    = SampleHeightVT(uv + vec2(0, -texelSize.y), requestMip);
    float down  = SampleHeightVT(uv + vec2(0, texelSize.y), requestMip);

    return normalize(vec3(left - right, 2.0, up - down));
}

int GetBlockLod(ivec2 index)
{
	float level = round(texelFetch(u_lodmap, index, 0).r * 8.0);

	return int(level);
}

vec4 DebugColor(vec2 globalUV, int requestMip)
{
	uvec4 pageInfo = SamplePageTable(globalUV, requestMip);

	uint activeLod = pageInfo.y;
		//float debug;
	switch(activeLod)
	{
		case 0u : return vec4(1, 0, 0, 1);  //red
		case 1u : return vec4(0, 1, 0, 1);  //green
		case 2u : return vec4(0, 0, 1, 1);  //blue
		case 3u : return vec4(1, 0, 1, 1);  //pink
		case 4u : return vec4(1, 1, 0, 1);  //yellow
		case 5u : return vec4(1, 1, 1, 1);  //white
	}
}


void vertex()
{
	int lodLevel = int(INSTANCE_CUSTOM.x);
	float morphValue = INSTANCE_CUSTOM.y;
	//ivec2 nodeXY = ivec2(int(INSTANCE_CUSTOM.z), int(INSTANCE_CUSTOM.w));
	float chunkScale = float(1 << lodLevel);
	VERTEX.xz *= chunkScale;

	vec3 worldSpacePos = (MODEL_MATRIX * vec4(VERTEX, 1)).xyz;
	//Remove the extra vertices
	worldSpacePos.xz = min(worldSpacePos.xz, vec2(u_heightmapSize) - vec2(1.0, 1.0));
	//vec2 vertexCoord = (u_terrainInverseTransform * vec4(worldSpacePos, 1)).xz + vec2(0.5, 0.5);
	vec2 vertexCoord = worldSpacePos.xz;

	vec2 uv = vertexCoord / vec2(u_heightmapSize);
	UV = uv;

	worldSpacePos = (MODEL_MATRIX * vec4(VERTEX, 1)).xyz;
	worldSpacePos.xz = min(worldSpacePos.xz, vec2(u_heightmapSize) - vec2(1.0, 1.0));

	//v_debugColor = DebugColor(worldSpacePos.xz / vec2(u_heightmapSize), v_RequestHeightMip);

	//float height = textureLod(u_heightmap, worldSpacePos.xz / vec2(u_heightmapSize), 0).r * u_heightScale;

	v_RequestHeightMip = max(lodLevel - u_HeightmapLodOffset, 0);
	float height = SampleHeightVT(worldSpacePos.xz / vec2(u_heightmapSize), v_RequestHeightMip);
	if(v_RequestHeightMip != 0)
	{
		float heightLowRes = SampleHeightVT(worldSpacePos.xz / vec2(u_heightmapSize), v_RequestHeightMip + 1);
		height = mix(heightLowRes, height, morphValue);
	}

	//worldSpacePos.y = height;
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(worldSpacePos, 1);
}

void fragment()
{
	//vec3 worldNormal = u_inverseTransposedTransform * CalculateNormal(UV);
	vec3 worldNormal = u_inverseTransposedTransform * CalculateNormalVT(UV, v_RequestHeightMip);
	worldNormal = normalize(worldNormal);
	//NORMAL = (VIEW_MATRIX * vec4(worldNormal, 0.0)).xyz;
	vec3 virtualTextureUV = GetVirtualTextureUV(UV, v_RequestHeightMip);
	ALBEDO = texture(u_VTPhysicalHeightmap, virtualTextureUV).rgb;
	//ALBEDO = texture(u_debugGridTexture, UV * vec2(u_heightmapSize) / 128.0 * 16.0).rgb;
	//ALBEDO *= v_debugColor.rgb;
}
