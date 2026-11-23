using System.Reflection;

namespace Fyrd.Server;

/// <summary>
/// The server's version, which the project file takes from <c>.claude-plugin/plugin.json</c>, and the line
/// <c>fyrd --version</c> prints. <c>/fyrd:up</c> runs <c>fyrd --version</c> on the tool it found before starting it, and
/// refuses anything that does not answer <see cref="Line"/> for the plugin's own version (scripts/lib.mjs, checkTool).
/// </summary>
public static class ServerVersion
{
    /// <summary>The MAJOR.MINOR.PATCH version of this assembly, without the <c>+commit</c> the SDK appends.</summary>
    public static string Current { get; } = Strip(
        typeof(ServerVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ServerVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    /// <summary>What <c>fyrd --version</c> prints: <c>fyrd &lt;version&gt;</c>.</summary>
    public static string Line => $"fyrd {Current}";

    /// <summary>True when the command line asks only for the version, so the server prints it and exits without starting.</summary>
    public static bool IsVersionRequest(string[] args) => args is ["--version"];

    /// <summary>The informational version without its <c>+metadata</c> suffix.</summary>
    public static string Strip(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+');
        return plus < 0 ? informationalVersion : informationalVersion[..plus];
    }
}
