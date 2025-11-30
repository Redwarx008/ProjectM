using Godot;
using Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FileAccess = Godot.FileAccess;
using Logger = Core.Logger;

internal class ShaderHelper
{
    /// <summary>
    /// <para>Create a shader from a glsl file.</para>
    /// <para><b>Note: </b> This requires manually call RenderingServer.FreeRid() </para>
    /// </summary>
    public static Rid CreateComputeShader(in string resFile)
    {
        // RDShaderFile shaderFile = GD.Load<RDShaderFile>(resFile);
        // if (shaderFile == null)
        // {
        //     throw new FileNotFoundException($"CreateShader failed, {resFile} not found.");
        // }
        // RDShaderSpirV shaderBytes = shaderFile.GetSpirV();
        // return RenderingServer.GetRenderingDevice().ShaderCreateFromSpirV(shaderBytes);
        
        var shaderFile = Godot.FileAccess.Open(resFile, FileAccess.ModeFlags.Read);
        if (shaderFile == null)
        {
            throw new FileNotFoundException($"CreateShader failed, {FileAccess.GetOpenError()} .");
        }
        
        // // 为什么不直接用GD.Load<RDShaderFile>呢？ 因为编译的spv不生成调试符号
        var shaderSource = new RDShaderSource()
        {
            Language = RenderingDevice.ShaderLanguage.Glsl,
            SourceCompute = shaderFile.GetAsText().Replace("#[compute]", string.Empty)
        };
        RenderingDevice rd = RenderingServer.GetRenderingDevice();
        RDShaderSpirV spv = rd.ShaderCompileSpirVFromSource(shaderSource);
        if (spv.GetStageCompileError(RenderingDevice.ShaderStage.Compute) != String.Empty)
        {
            Logger.Error($"Compute shader compilation failed : {spv.GetStageCompileError(RenderingDevice.ShaderStage.Compute)}");
        }
        
        return rd.ShaderCreateFromSpirV(spv);
    }
}
