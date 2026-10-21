using System.IO;
using Microsoft.Win32;

namespace ImageClassification.UI.Services;

/// <summary>
/// Auto-detection of CUDA/cuDNN bin directories in the typical install locations:
/// CUDA — the NVIDIA registry (GPU Computing Toolkit, including non-standard disks) and
/// Program Files; cuDNN — the standard NVIDIA layout (zip v9: bin\12.x\x64, v8: bin\x64),
/// drive-root folders and a shallow per-drive scan (up to 3 levels) for a folder
/// containing cudnn*.dll. All lookups are exception-safe and return null when nothing
/// is found, so callers can treat a result as "keep the current value".
/// </summary>
internal static class GpuLibraryDetector
{
    /// <summary>
    /// CUDA bin directory: first the NVIDIA registry (all installed versions, the newest
    /// one whose BinaryPath has a cudart64_*.dll), then Program Files. Fast (no drive
    /// scans) — safe to run synchronously at startup. Null when nothing is found.
    /// </summary>
    public static string? DetectCudaPath()
    {
        // 1) Registry: HKLM\SOFTWARE\NVIDIA Corporation\GPU Computing Toolkit\CUDA\vX.Y (BinaryPath).
        //    Covers installs on non-standard disks (D:\cuda etc.).
        try
        {
            using var toolkit = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\NVIDIA Corporation\GPU Computing Toolkit\CUDA");
            if (toolkit is not null)
            {
                string? best = null;
                double bestVersion = -1;
                foreach (var subName in toolkit.GetSubKeyNames())
                {
                    if (!subName.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!TryParseCudaVersion(subName, out var version))
                        continue;

                    using var versionKey = toolkit.OpenSubKey(subName);
                    var binPath = versionKey?.GetValue("BinaryPath") as string;
                    if (string.IsNullOrWhiteSpace(binPath))
                        continue;
                    if (HasCudart(binPath) && version > bestVersion)
                    {
                        bestVersion = version;
                        best = binPath;
                    }
                }
                if (best is not null)
                    return best;
            }
        }
        catch
        {
            // Registry unavailable — fall through to the typical install directories.
        }

        // 2) Typical install directories (Program Files, incl. the x86 one).
        foreach (var root in new[]
                 {
                     @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA",
                     @"C:\Program Files (x86)\NVIDIA GPU Computing Toolkit\CUDA"
                 })
        {
            var best = BestCudaVersionDirectory(root);
            if (best is not null)
                return best;
        }
        return null;
    }

    /// <summary>
    /// cuDNN bin directory from the fast locations only: the standard NVIDIA layout
    /// (C:\Program Files\NVIDIA\CUDNN\...) and drive-root cudnn folders. Safe to run
    /// synchronously at startup — no per-drive scanning.
    /// </summary>
    public static string? DetectCudnnPathFast()
    {
        foreach (var root in new[]
                 {
                     @"C:\Program Files\NVIDIA\CUDNN",
                     @"C:\Program Files (x86)\NVIDIA\CUDNN"
                 })
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var versionDir in SafeGetDirectories(root))
            {
                var found = FindCudnnBinInTree(versionDir, maxDepth: 3);
                if (found is not null)
                    return found;
            }
        }

        // Drive-root folders: ...\cudnn\bin (a popular way of unpacking the zip).
        foreach (var drive in SafeFixedDrives())
        {
            foreach (var topLevel in SafeGetDirectories(drive.RootDirectory.FullName))
            {
                if (!topLevel.EndsWith("cudnn", StringComparison.OrdinalIgnoreCase))
                    continue;
                var found = FindCudnnBinInTree(topLevel, maxDepth: 2);
                if (found is not null)
                    return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Full cuDNN detection: the fast locations plus a shallow per-drive scan (up to
    /// 3 levels, system folders skipped). The scan can take a while on large drives —
    /// run it in the background (the result applies from the next launch).
    /// </summary>
    public static string? DetectCudnnPathFull()
    {
        var fast = DetectCudnnPathFast();
        if (fast is not null)
            return fast;

        foreach (var drive in SafeFixedDrives())
        {
            var found = FindCudnnDirectoryOnDrive(drive);
            if (found is not null)
                return found;
        }
        return null;
    }

    /// <summary>True when the directory holds a cudart64_*.dll (the CUDA bin marker).</summary>
    public static bool HasCudart(string binPath)
    {
        try
        {
            return Directory.Exists(binPath)
                   && Directory.EnumerateFiles(binPath, "cudart64_*.dll").Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The newest installed CUDA version (v12.4 &gt; v11.8) that has a cudart, under <paramref name="root"/>.</summary>
    internal static string? BestCudaVersionDirectory(string root)
    {
        if (!Directory.Exists(root))
            return null;

        string? best = null;
        double bestVersion = -1;
        foreach (var versionDir in SafeGetDirectories(root))
        {
            var versionName = Path.GetFileName(versionDir);
            if (!versionName.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!TryParseCudaVersion(versionName, out var version))
                continue;

            var bin = Path.Combine(versionDir, "bin");
            if (HasCudart(bin) && version > bestVersion)
            {
                bestVersion = version;
                best = bin;
            }
        }
        return best;
    }

    /// <summary>"v12.4" → 12.4 (for version comparison). The name must start with v/V.</summary>
    internal static bool TryParseCudaVersion(string name, out double version)
    {
        version = 0;
        if (name.Length < 2 || (name[0] != 'v' && name[0] != 'V') || !char.IsDigit(name[1]))
            return false;

        var digits = new System.Text.StringBuilder();
        for (var i = 1; i < name.Length && (char.IsDigit(name[i]) || name[i] == '.'); i++)
            digits.Append(name[i]);
        return double.TryParse(digits.ToString().Replace('.', ','), out version);
    }

    /// <summary>
    /// Finds the first folder (no deeper than <paramref name="maxDepth"/>) containing
    /// cudnn*.dll (cudnn64_8.dll / cudnn_ops64_9.dll etc.) — that folder is the cuDNN bin.
    /// </summary>
    internal static string? FindCudnnBinInTree(string root, int maxDepth)
    {
        var currentLevel = new List<string> { root };
        for (var depth = 0; depth <= maxDepth && currentLevel.Count > 0; depth++)
        {
            var nextLevel = new List<string>();
            foreach (var dir in currentLevel)
            {
                if (ContainsCudnn(dir))
                    return dir;
                if (depth < maxDepth)
                    nextLevel.AddRange(SafeGetDirectories(dir));
            }
            currentLevel = nextLevel;
        }
        return null;
    }

    internal static bool ContainsCudnn(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "cudnn*.dll").Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Shallow drive walk (up to 3 folder levels) looking for a folder with cudnn*.dll.
    /// Skips system folders (Windows, ProgramData, $Recycle.Bin, ...).
    /// </summary>
    internal static string? FindCudnnDirectoryOnDrive(DriveInfo drive)
    {
        var root = drive.RootDirectory.FullName;
        var currentLevel = new List<string>();
        foreach (var dir in SafeGetDirectories(root))
        {
            if (IsSystemFolder(Path.GetFileName(dir.TrimEnd('\\', '/'))))
                continue;
            currentLevel.Add(dir);
        }

        for (var depth = 1; depth <= 3 && currentLevel.Count > 0; depth++)
        {
            var nextLevel = new List<string>();
            foreach (var dir in currentLevel)
            {
                if (ContainsCudnn(dir))
                    return dir;
                if (depth < 3)
                {
                    foreach (var sub in SafeGetDirectories(dir))
                    {
                        if (IsSystemFolder(Path.GetFileName(sub.TrimEnd('\\', '/'))))
                            continue;
                        nextLevel.Add(sub);
                    }
                }
            }
            currentLevel = nextLevel;
        }
        return null;
    }

    internal static bool IsSystemFolder(string name)
    {
        return name.StartsWith("$", StringComparison.Ordinal)
               || string.Equals(name, "Windows", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "ProgramData", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "Recovery", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "System Volume Information", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "PerfLogs", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<DriveInfo> SafeFixedDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .ToList();
        }
        catch
        {
            return Enumerable.Empty<DriveInfo>();
        }
    }

    private static IEnumerable<string> SafeGetDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path).ToList();
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }
}