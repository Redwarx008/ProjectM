shader_type spatial;
render_mode depth_draw_always;

uniform float u_heightScale;

uniform uvec2 u_heightmapSize;

uniform mat4 u_terrainInverseTransform;
uniform mat3 u_inverseTransposedTransform;

//Indirection Texture
//format: rg32f
//r: Physical Layer Index
//g: Active LOD
uniform usampler2D u_VTPageTable : filter_nearest, repeat_disable;
// Array Layer = Physical Slot Index
uniform sampler2DArray u_VTPhysicalHeightmap : repeat_disable;

uniform ivec2 u_VTPageTableSize[10];

uniform int u_PagePadding;
uniform int u_HeightmapLodOffset;

uniform sampler2DArray u_groundDiffuseArray : source_color;
uniform sampler2DArray u_groundNormalArray ;

uniform sampler2D u_debugGridTexture : source_color;

varying vec4 v_debugColor;

varying flat int v_RequestHeightMip;

ivec2 PageTableSize(int mip)
{

	return textureSize(u_VTPageTable, mip);
}

ivec2 RealPageTableSize(int mip)
{
	//switch(mip)
	//{
		//case 0u: return ivec2(u_VTPageTableSize[0]);
		//case 1u: return ivec2(u_VTPageTableSize[1]);
		//case 2u: return ivec2(u_VTPageTableSize[2]);
		//case 3u: return ivec2(u_VTPageTableSize[3]);
		//case 4u: return ivec2(u_VTPageTableSize[4]);
		//case 5u: return ivec2(u_VTPageTableSize[5]);
		//case 6u: return ivec2(u_VTPageTableSize[6]);
		//case 7u: return ivec2(u_VTPageTableSize[7]);
	//}
	return ivec2(u_VTPageTableSize[mip]);
}

vec3 GetVirtualTextureUV(vec2 globalUv, int requestMip)
{
    // 获取当前LOD下虚拟纹理的实际页面数（不包含填充）
    ivec2 virtualPageCount = RealPageTableSize(requestMip);
    // 获取PageTable纹理的尺寸（包含填充）
    ivec2 pageTableSize = PageTableSize(requestMip);
	// 将globalUv映射到实际页面数范围内，避免采样到填充区域
    vec2 effectiveUv = globalUv * vec2(virtualPageCount) / vec2(pageTableSize);

	uvec2 pageInfo = textureLod(u_VTPageTable, effectiveUv, float(requestMip)).xy;

	uint physSlot = pageInfo.x;
	uint activeLod = pageInfo.y;

	// 当前 Active LOD 下，整个地形被切分为 gridDimX * gridDimY 个页面
	ivec2 gridDim = PageTableSize(int(activeLod));
    effectiveUv = clamp(effectiveUv, 0.0, 1.0 - 1e-7);
    vec2 uvInPage = fract(vec2(gridDim) * effectiveUv);

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
	return textureLod(u_VTPhysicalHeightmap, virtualTextureUV, 0).r * u_heightScale;
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

vec4 DebugColor(vec2 globalUV, int requestMip)
{
	uvec4 pageInfo = textureLod(u_VTPageTable, globalUV , float(requestMip));

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

float GetOriginHeight(vec2 worldSpacePosXZ, ivec2 lodDection, int mip)
{
	float h1 = SampleHeightVT(vec2(ivec2(worldSpacePosXZ) - lodDection) / vec2(u_heightmapSize), mip);
	float h2 = SampleHeightVT(vec2(ivec2(worldSpacePosXZ) + lodDection) / vec2(u_heightmapSize), mip);
	return (h1 + h2) * 0.5;
}


void vertex()
{
	ivec2 lodDirection = ivec2(UV);
	int lodLevel = int(INSTANCE_CUSTOM.x);
	float morphVal = clamp(INSTANCE_CUSTOM.y, 0.0, 1.0);
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

	//v_debugColor = DebugColor(worldSpacePos.xz / vec2(u_heightmapSize), v_RequestHeightMip);
	v_RequestHeightMip = max(lodLevel - u_HeightmapLodOffset, 0);
	int lowResMip = clamp(lodLevel - u_HeightmapLodOffset + 1, 0, 7);
	float height = SampleHeightVT(worldSpacePos.xz / vec2(u_heightmapSize), v_RequestHeightMip);
	float lowResHeight = GetOriginHeight(worldSpacePos.xz, lodDirection * (1 << lodLevel), lowResMip);
	height = mix(lowResHeight, height, morphVal);

	worldSpacePos.y = height;
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(worldSpacePos, 1);
}

void fragment()
{
	//vec3 worldNormal = u_inverseTransposedTransform * CalculateNormal(UV);
	vec3 worldNormal = u_inverseTransposedTransform * CalculateNormalVT(UV, 0);
	worldNormal = normalize(worldNormal);
	NORMAL = (VIEW_MATRIX * vec4(worldNormal, 0.0)).xyz;
	vec3 virtualTextureUV = GetVirtualTextureUV(UV, 0);
	//ALBEDO = texture(u_VTPhysicalHeightmap, virtualTextureUV).rgb;
	ALBEDO = texture(u_debugGridTexture, UV * vec2(u_heightmapSize) / 128.0 * 16.0).rgb;
	//ALBEDO *= v_debugColor.rgb;
}
