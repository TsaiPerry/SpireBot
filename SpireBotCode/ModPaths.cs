using System;
using System.IO;
using Godot;

namespace SpireBot.SpireBotCode;

/// <summary>
/// Resolves the config screen's file-path settings into actual files.
///
/// The settings screen is a plain text field, so a path can arrive as a directory (the mistake
/// costs a silent no-op otherwise: <c>Contract.Load</c> refuses a directory, the bot never
/// starts, and the only trace is a log line). This resolver accepts the three forms a person
/// actually types — a full file path, the folder holding the file, or nothing at all — and
/// reports which file it settled on. It never invents content: an unresolvable path is returned
/// unchanged so the caller's own "file not found" error still names what the user typed.
///
/// Convention: shipped artifacts live in <c>mods\SpireBot\model\</c>, next to the mod DLL.
/// </summary>
internal static class ModPaths
{
    private const string ModelSubdir = "model";

    /// <summary>Directory containing SpireBot.dll, or null if it can't be determined.</summary>
    internal static string? ModDir { get; } =
        Path.GetDirectoryName(typeof(ModPaths).Assembly.Location);

    /// <summary>
    /// Resolves <paramref name="configured"/> to an existing file.
    /// Empty → look for <paramref name="defaultFileName"/> under the mod dir (and its
    /// <c>model\</c> subdir). A directory → look for the default name inside it (and its
    /// <c>model\</c> subdir). Anything else → returned as typed.
    /// Returns null only when <paramref name="configured"/> is empty and no default file exists.
    /// </summary>
    internal static string? ResolveDataFile(string? configured, string defaultFileName)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string typed = configured.Trim();
            if (File.Exists(typed))
                return typed;

            if (Directory.Exists(typed))
            {
                string? found = FirstExisting(typed, defaultFileName);
                if (found != null)
                {
                    GD.Print($"[SpireBot] ModPaths: '{typed}' is a directory — using '{found}'.");
                    return found;
                }
            }

            // Not a file and not a directory holding the default: hand it back so the caller's
            // error message quotes exactly what was configured.
            return typed;
        }

        if (ModDir == null)
            return null;

        string? fallback = FirstExisting(ModDir, defaultFileName);
        if (fallback != null)
            GD.Print($"[SpireBot] ModPaths: no path configured — using '{fallback}'.");
        return fallback;
    }

    private static string? FirstExisting(string dir, string fileName)
    {
        string inModelSubdir = Path.Combine(dir, ModelSubdir, fileName);
        if (File.Exists(inModelSubdir)) return inModelSubdir;

        string direct = Path.Combine(dir, fileName);
        return File.Exists(direct) ? direct : null;
    }
}
