namespace Fyrd.Core;

/// <summary>The owner's standing preferences, kept in memory/owner-proxy.md. Only an explicit Save rewrites it.</summary>
public sealed class OwnerProxy(FyrdPaths paths)
{
    private const string Template = """
        # Owner preferences

        ## How I decide

        - (Describe how you want decisions made, for example: prefer the simplest option that meets the spec.)

        ## Tests I expect

        - (Describe the tests you expect with every change.)

        ## Things I never want

        - (List the things the team must never do.)
        """;

    /// <summary>True when the text is still the untouched starter template (line endings and outer whitespace ignored).</summary>
    public static bool IsTemplate(string text) =>
        text.ReplaceLineEndings("\n").Trim() == Template.ReplaceLineEndings("\n").Trim();

    private readonly object _gate = new();
    private string FilePath => Path.Combine(paths.MemoryDir, "owner-proxy.md");

    public string Read()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath)) AtomicFile.Write(FilePath, Template.ReplaceLineEndings("\n") + "\n");
            return File.ReadAllText(FilePath);
        }
    }

    public void Save(string text)
    {
        lock (_gate) AtomicFile.Write(FilePath, text);
    }
}
