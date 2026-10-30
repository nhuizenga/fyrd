namespace Fyrd.Core.Tests;

public sealed class TempHome : IDisposable
{
    public TempHome() =>
        Paths = new FyrdPaths(Path.Combine(Path.GetTempPath(), "fyrd-test-" + Guid.NewGuid()));

    public FyrdPaths Paths { get; }

    public void Dispose()
    {
        if (Directory.Exists(Paths.Root)) Directory.Delete(Paths.Root, recursive: true);
    }
}
