using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

#pragma warning disable CA2255

namespace LateMing
{
    // see https://github.com/godotengine/godot/issues/78513#issuecomment-1967073540
    internal static class AppModule
    {
        [System.Runtime.CompilerServices.ModuleInitializer]
        public static void Initialize()
        {
            var loadContext = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(System.Reflection.Assembly.GetExecutingAssembly());
            if (loadContext != null)
            {
                loadContext.Unloading += alc =>
                {
                    var assembly = typeof(JsonSerializerOptions).Assembly;
                    var updateHandlerType = assembly.GetType("System.Text.Json.JsonSerializerOptionsUpdateHandler");
                    var clearCacheMethod = updateHandlerType?.GetMethod("ClearCache", BindingFlags.Static | BindingFlags.Public);
                    clearCacheMethod?.Invoke(null, new object?[] { null });

                    // Unload any other unloadable references
                };
            }
        }
    }
}
