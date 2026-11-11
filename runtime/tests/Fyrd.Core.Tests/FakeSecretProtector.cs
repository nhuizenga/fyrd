using Fyrd.Core.Config;

namespace Fyrd.Core.Tests;

/// <summary>Reversible stand-in for DPAPI: reverses the bytes, so a stored file never holds the plain text.</summary>
public sealed class FakeSecretProtector : ISecretProtector
{
    public bool Supported => true;
    public byte[] Protect(byte[] plain) => plain.Reverse().ToArray();
    public byte[] Unprotect(byte[] protectedData) => protectedData.Reverse().ToArray();
}
