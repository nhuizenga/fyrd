namespace Fyrd.Server;

/// <summary>
/// Where the server finds the console UI, the skills and the docs. Run from the repo (<c>/fyrd:up</c> starts the build
/// output with the project folder as its working directory), they are <c>wwwroot</c> under the content root and
/// <c>skills</c> and <c>docs</c> three folders up, at the repo root. Run from the dotnet tool package (or any publish
/// output), all three sit beside the assembly, and the working directory is wherever the tool was started.
/// </summary>
public static class ServerLayout
{
    /// <summary>True when <paramref name="baseDir"/> (the assembly's folder) holds the built console: a package or publish output, never a build output.</summary>
    public static bool IsPackaged(string baseDir) => File.Exists(Path.Combine(baseDir, "wwwroot", "index.html"));

    /// <summary>The content root to start with: the assembly's folder when packaged, else null for the host's default (the working directory).</summary>
    public static string? ContentRoot(string baseDir) => IsPackaged(baseDir) ? baseDir : null;

    /// <summary>The default folder for <paramref name="name"/> (<c>skills</c> or <c>docs</c>) under the given content root.</summary>
    public static string DefaultDir(string contentRoot, string baseDir, string name) =>
        Path.GetFullPath(IsPackaged(baseDir) && SameDir(contentRoot, baseDir)
            ? Path.Combine(baseDir, name)
            : Path.Combine(contentRoot, "..", "..", "..", name));

    private static bool SameDir(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
