using System.Security.Cryptography;
using System.Text;

namespace Fyrd.Core.Config;

/// <summary>Encrypts a secret for the current user. Windows uses DPAPI; elsewhere there is no protector.</summary>
public interface ISecretProtector
{
    bool Supported { get; }
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>DPAPI (current user scope). Unsupported off Windows, where saving a secret fails with a clear message.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("fyrd-secrets-v1");

    public bool Supported => OperatingSystem.IsWindows();

    public byte[] Protect(byte[] plain) => OperatingSystem.IsWindows()
        ? ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)
        : throw new SecretStoreException("Saving secrets needs Windows (DPAPI); set the environment variable instead.");

    public byte[] Unprotect(byte[] protectedData) => OperatingSystem.IsWindows()
        ? ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser)
        : throw new SecretStoreException("Saving secrets needs Windows (DPAPI); set the environment variable instead.");
}

public sealed class SecretStoreException(string message) : Exception(message);

/// <summary>
/// Optional secrets the owner saves in Settings, one DPAPI-protected file each under the Fyrd home. Values are never
/// logged or returned by the API; only whether one is saved.
/// </summary>
public sealed class SecretStore(FyrdPaths paths, ISecretProtector protector)
{
    public const string GitHubToken = "github-token", AnthropicApiKey = "anthropic-api-key";
    public static readonly IReadOnlyList<string> Names = [GitHubToken, AnthropicApiKey];

    private readonly object _gate = new();

    public bool Supported => protector.Supported;

    private string FileFor(string name) =>
        Names.Contains(name) ? Path.Combine(paths.SecretsDir, name + ".bin") : throw new ArgumentException("Unknown secret.", nameof(name));

    public bool Has(string name) => File.Exists(FileFor(name));

    /// <summary>The saved value, or null when none is saved or it can no longer be decrypted (another user or machine).</summary>
    public string? Get(string name)
    {
        lock (_gate)
        {
            var file = FileFor(name);
            if (!File.Exists(file)) return null;
            try { return Encoding.UTF8.GetString(protector.Unprotect(File.ReadAllBytes(file))); }
            catch (Exception e) when (e is CryptographicException or IOException or SecretStoreException) { return null; }
        }
    }

    public void Set(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new SecretStoreException("Enter a value to save.");
        lock (_gate)
        {
            var file = FileFor(name);
            var data = protector.Protect(Encoding.UTF8.GetBytes(value.Trim()));
            Directory.CreateDirectory(paths.SecretsDir);
            File.WriteAllBytes(file, data);
        }
    }

    public void Delete(string name)
    {
        lock (_gate) { File.Delete(FileFor(name)); }
    }
}
