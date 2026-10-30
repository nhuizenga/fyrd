namespace Fyrd.Core;

internal static class AtomicFile
{
    /// <summary>Writes to a temp file then moves it over <paramref name="path"/> so readers never see a partial file.</summary>
    public static void Write(string path, string contents)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}
