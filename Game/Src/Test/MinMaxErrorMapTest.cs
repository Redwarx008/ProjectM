using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Logger = Core.Logger;
using Core;

//public partial class MinMaxErrorMapTest : Node2D
//{
//    Sprite2D[]? _sprite2Ds;

//    private List<GDTexture2D>? _minMaxErrorMaps;
//    private GDTexture2D? _heightmap;
//    private MapDefinition _map = MapDefinition.LoadFromJsonFile(VirtualFileSystem.Instance.ResolvePath("Map/descriptor.json"));
//    private void LoadHeightmap()
//    {
//        string? heightmapFile = VirtualFileSystem.Instance.ResolvePath("Map/heightmap.png");
//        if (!File.Exists(heightmapFile))
//        {
//            Logger.Error($"Can't find heightmap file at {heightmapFile}.");
//            return;
//        }

//        using var stream = File.OpenRead(heightmapFile);

//        int width;
//        int height;
//        var stbiContext = new StbImageSharp.StbImage.stbi__context(stream);
//        ReadOnlySpan<byte> data;
//        unsafe
//        {
//            int channels;
//            ushort* buffer = StbImageSharp.StbImage.stbi__load_and_postprocess_16bit(stbiContext, &width, &height, &channels, 1);
//            ReadOnlySpan<ushort> bufferSpan = new Span<ushort>(buffer, width * height * channels);
//            data = MemoryMarshal.AsBytes(bufferSpan);
//        }
        
//        // create heightmap
//        var heightmapFormat = new GDTexture2DDesc()
//        {
//            Format = RenderingDevice.DataFormat.R16Unorm,
//            Width = (uint)width,
//            Height = (uint)height,
//            Mipmaps = 1,
//            UsageBits = RenderingDevice.TextureUsageBits.CanUpdateBit | 
//                        RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanCopyFromBit
//        };
//        _heightmap = GDTexture2D.Create(heightmapFormat, data);
//    }
//    public override void _Ready()
//    {
//        base._Ready();
//        RenderingServer.CallOnRenderThread(Callable.From(() =>
//        {
//            LoadHeightmap();
//            if (_heightmap != null)
//            {
//                _minMaxErrorMaps = MinMaxErrorMapBuilder.BuildTextures(_heightmap, _map.TerrainHeightScale, 32);
//            }
//        }));

//        RenderingServer.ForceSync();
//        if (_minMaxErrorMaps == null) return;
//        _sprite2Ds = new Sprite2D[_minMaxErrorMaps.Count];
//        float x = 0;
//        for (int i = 0; i < _sprite2Ds.Length; i++)
//        {
//            _sprite2Ds[i] = new Sprite2D()
//            {
//                Texture = _minMaxErrorMaps[i],
//                Position = new Vector2(x, 0),
//                // Scale = new Vector2(2, 2)
//            };
//            AddChild(_sprite2Ds[i]);
//            x += 100;
//        }
//    }

//    public override void _ExitTree()
//    {
//        base._ExitTree();
//        RenderingServer.CallOnRenderThread(Callable.From(() =>
//        {
//            if (_minMaxErrorMaps != null)
//            {
//                foreach (var minMaxErrorMap in _minMaxErrorMaps)
//                {
//                    minMaxErrorMap.Dispose();
//                }
//            }

//            _heightmap?.Dispose();
//        }));
//    }
//}
