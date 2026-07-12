using System.Security.Cryptography;
using System.Text;

namespace Schemorph.Core;

/// <summary>
/// The one checksum used across strategies (migrations, redefines):
/// SHA-256 over line-ending-normalized content, so the same file checked out
/// on Windows (CRLF) and Linux (LF) yields the same checksum.
/// </summary>
public static class ContentChecksum
{
    public static string Compute(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n"))));
}
