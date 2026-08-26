using System;
using System.Collections.Generic;
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
/// mod's own folder.
///
/// The ONNX Runtime's native library is then preloaded by ABSOLUTE path and bound to the managed
/// wrapper's P/Invokes through a <see cref="NativeLibrary.SetDllImportResolver"/> hook, so
/// nothing depends on the OS's own library search order finding a file inside a mod folder.
/// </summary>
internal static class AssemblyResolution
{
    /// <summary>Assembly declaring the ONNX Runtime P/Invokes whose native binding we override.</summary>
    private const string OnnxManagedAssembly = "Microsoft.ML.OnnxRuntime";

    private static string? _modDir;
    private static bool _installed;

    /// <summary>Preloaded native modules, keyed by the bare library name a <c>DllImport</c> asks
    /// for (no "lib" prefix, no extension). Populated by <see cref="PreloadNativeLibraries"/>.</summary>
    private static readonly Dictionary<string, IntPtr> NativeHandles =
        new(StringComparer.OrdinalIgnoreCase);

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

        PreloadNativeLibraries();
        InstallDllImportResolver();

        GD.Print($"[SpireBot] AssemblyResolution installed for {_modDir}");
    }

    /// <summary>
    /// The native ONNX Runtime files this platform needs, as (bare library name, path relative to
    /// the mod dir). Empty on a platform we do not ship binaries for — the caller logs that and
    /// OnnxPolicy.Load then fails the attach with a visible reason, which is the intended
    /// behaviour: better a loud refusal than a bot driving on a policy that never loaded.
    ///
    /// Windows keeps the FLAT layout `dotnet build` produces (RuntimeIdentifier=win-x64 resolves
    /// runtimes/win-x64/native/* straight into the output root). The macOS assets sit under
    /// runtimes/&lt;rid&gt;/native/ instead, because osx-arm64 and osx-x64 both name their library
    /// libonnxruntime.dylib and so cannot coexist in one flat folder. See the csproj's
    /// OnnxMacNativeFiles item for the copy that puts them there.
    /// </summary>
    private static IEnumerable<(string Name, string RelativePath)> NativeLibrariesForThisPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return ("onnxruntime", "onnxruntime.dll");
            yield return ("onnxruntime_providers_shared", "onnxruntime_providers_shared.dll");
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            // The Mac packages ship libonnxruntime.dylib only — no providers_shared counterpart.
            string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "osx-arm64"
                : "osx-x64";
            yield return ("onnxruntime", Path.Combine("runtimes", rid, "native", "libonnxruntime.dylib"));
        }
    }

    private static void PreloadNativeLibraries()
    {
        bool any = false;

        foreach ((string name, string relative) in NativeLibrariesForThisPlatform())
        {
            any = true;
            string path = Path.Combine(_modDir!, relative);
            if (!File.Exists(path))
            {
                GD.PrintErr($"[SpireBot] AssemblyResolution: native library '{relative}' is missing from " +
                            $"the mod folder; ONNX inference will not be available.");
                continue;
            }

            try
            {
                NativeHandles[name] = NativeLibrary.Load(path);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[SpireBot] AssemblyResolution: failed to preload {relative}: {ex.Message}");
            }
        }

        if (!any)
        {
            GD.PrintErr($"[SpireBot] AssemblyResolution: no ONNX Runtime native binaries are shipped for " +
                        $"{RuntimeInformation.RuntimeIdentifier}; the bot will refuse to attach on this platform.");
        }
    }

    /// <summary>
    /// Points the managed wrapper's <c>DllImport("onnxruntime")</c> at the module we already
    /// loaded by absolute path. Without this the runtime falls back to the OS search order, which
    /// on macOS does not include the mod folder and would fail even with the dylib sitting
    /// right next to the assembly.
    /// </summary>
    private static void InstallDllImportResolver()
    {
        if (NativeHandles.Count == 0) return;

        Assembly onnx;
        try
        {
            // Resolves through the handlers installed above, i.e. out of the mod folder.
            onnx = Assembly.Load(OnnxManagedAssembly);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[SpireBot] AssemblyResolution: could not load {OnnxManagedAssembly} to bind its " +
                        $"native library: {ex.Message}");
            return;
        }

        try
        {
            NativeLibrary.SetDllImportResolver(onnx, ResolveNativeLibrary);
        }
        catch (InvalidOperationException)
        {
            // A resolver was already set for that assembly (only possible if Install ran twice
            // against the same loaded wrapper); the existing one is ours, so nothing to do.
        }
    }

    /// <summary>Returns a preloaded module for a name we shipped, else <see cref="IntPtr.Zero"/>
    /// so the runtime falls back to its normal probing.</summary>
    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        => NativeHandles.TryGetValue(NormalizeLibraryName(libraryName), out IntPtr handle)
            ? handle
            : IntPtr.Zero;

    /// <summary>Reduces the many spellings a DllImport may use ("onnxruntime",
    /// "libonnxruntime.dylib", "onnxruntime.dll") to the bare name used as a key.</summary>
    private static string NormalizeLibraryName(string libraryName)
    {
        string name = libraryName;
        foreach (string suffix in new[] { ".dll", ".dylib", ".so" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        if (name.StartsWith("lib", StringComparison.Ordinal))
            name = name[3..];

        return name;
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
