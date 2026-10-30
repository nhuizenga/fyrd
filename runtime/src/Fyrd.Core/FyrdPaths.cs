namespace Fyrd.Core;

public sealed class FyrdPaths
{
    public FyrdPaths(string root)
    {
        Root = Path.GetFullPath(root);
        ConfigFile = Path.Combine(Root, "config.json");
        SpecsDir = Path.Combine(Root, "specs");
        InboxDir = Path.Combine(Root, "inbox");
        MemoryDir = Path.Combine(Root, "memory");
        foreach (var dir in new[] { Root, SpecsDir, InboxDir, MemoryDir })
            Directory.CreateDirectory(dir);
    }

    public string Root { get; }
    public string ConfigFile { get; }
    public string SpecsDir { get; }
    public string InboxDir { get; }
    public string MemoryDir { get; }

    public string SpecDir(string specId) => Path.Combine(SpecsDir, specId);

    public static FyrdPaths Resolve(string? configured)
    {
        var root = !string.IsNullOrWhiteSpace(configured) ? configured
            : Environment.GetEnvironmentVariable("FYRD_HOME") is { Length: > 0 } env ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fyrd", "data");
        return new FyrdPaths(root);
    }
}
