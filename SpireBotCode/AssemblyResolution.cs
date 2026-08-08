using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Godot;

namespace SpireBot.SpireBotCode;

/// <summary>
/// The game's mod loader loads only SpireBot.dll itself — it never probes mods\SpireBot\ for
/// dependency assemblies. That was harmless until OnnxPolicy gave SpireBot a real (non-trimmed)
/// reference to Microsoft.ML.OnnxRuntime: BaseLib's late post-mod init reflects over every mod
/// type's fields, which forces the dependency load and killed game startup with
/// FileNotFoundException before any SpireBot code ran. Hooking assembly resolution here (called
/// first thing in Initialize, i.e. before BaseLib's reflection pass) redirects those loads to the
/// mod's own folder. The native onnxruntime.dll is preloaded by absolute path so the managed
/// wrapper's P/Invoke ("onnxruntime") later binds to the already-loaded module.
/// </summary>
internal static class AssemblyResolution
{
    private static string? _modDir;
    private static bool _installed;

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;

        _modDir = Path.GetDirectoryName(typeof(AssemblyResolution).Assembly.Location);
        if (string.IsNullOrEmpty(_modDir))
        {
            GD.PrintErr("[SpireBot] AssemblyResolution: could not determine mod directory; dependency loads may fail.");
            return;
        }

        // Cover both load shapes: mods loaded into the default ALC surface failures on
        // AppDomain.AssemblyResolve; a custom ALC surfaces them on its Resolving event.
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) => ResolveFromModDir(new AssemblyName(args.Name));
        var alc = AssemblyLoadContext.GetLoadContext(typeof(AssemblyResolution).Assembly);
        if (alc != null && alc != AssemblyLoadContext.Default)
            alc.Resolving += (_, name) => ResolveFromModDir(name);

        foreach (var native in new[] { "onnxruntime.dll", "onnxruntime_providers_shared.dll" })
        {
            string path = Path.Combine(_modDir, native);
            if (!File.Exists(path)) continue;
            try
            {
                NativeLibrary.Load(path);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[SpireBot] AssemblyResolution: failed to preload {native}: {ex.Message}");
            }
        }

        GD.Print($"[SpireBot] AssemblyResolution installed for {_modDir}");
    }

    private static Assembly? ResolveFromModDir(AssemblyName name)
    {
        if (_modDir == null || name.Name == null) return null;
        string candidate = Path.Combine(_modDir, name.Name + ".dll");
        if (!File.Exists(candidate)) return null;
        try
        {
            return Assembly.LoadFrom(candidate);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] AssemblyResolution: failed to load {name.Name} from mod dir: {ex.Message}");
            return null;
        }
    }
}
