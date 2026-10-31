using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Fyrd.Core.Specs;

public static partial class SpecIds
{
    private const int MaxSlugLength = 40;

    public static string NewId(string title, DateTimeOffset now) =>
        $"{now.UtcDateTime:yyyyMMdd}-{Slugify(title)}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}";

    public static bool IsValid(string id) => id is not null && IdPattern().IsMatch(id);

    private static string Slugify(string title)
    {
        var sb = new StringBuilder();
        foreach (var c in (title ?? "").ToLowerInvariant())
        {
            var ok = c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
            if (ok) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > MaxSlugLength) slug = slug[..MaxSlugLength].TrimEnd('-');
        return slug.Length == 0 ? "spec" : slug;
    }

    [GeneratedRegex(@"^[0-9]{8}-[a-z0-9-]{1,48}\z")]
    private static partial Regex IdPattern();
}
