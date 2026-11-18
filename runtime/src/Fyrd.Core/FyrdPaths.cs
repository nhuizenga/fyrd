namespace Fyrd.Core;

public sealed class FyrdPaths
{
    public FyrdPaths(string root)
    {
        Root = Path.GetFullPath(root);
        ConfigFile = Path.Combine(Root, "config.json");
        HubDir = Path.Combine(Root, "hub");
        SecretsDir = Path.Combine(Root, "secrets");
        HubPersonasDir = Path.Combine(HubDir, "personas");
        HubMemoryDir = Path.Combine(HubDir, "memory");
        foreach (var dir in new[] { Root, HubDir, HubPersonasDir, HubMemoryDir, SecretsDir })
            Directory.CreateDirectory(dir);
    }

    public string Root { get; }
    public string ConfigFile { get; }
    public string HubDir { get; }
    public string SecretsDir { get; }
    public string HubPersonasDir { get; }
    public string HubMemoryDir { get; }

    public static FyrdPaths Resolve(string? configured)
    {
        var root = !string.IsNullOrWhiteSpace(configured) ? configured
            : Environment.GetEnvironmentVariable("FYRD_HOME") is { Length: > 0 } env ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fyrd", "data");
        return new FyrdPaths(root);
    }
}
