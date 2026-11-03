using System.Security.Cryptography;

namespace NetTrace.Reporting.Integrity;

/// <summary>
/// SHA-256 checksums for exported files. A matching checksum shows the file has not changed
/// since export; it does not independently prove the measurements themselves are authentic.
/// </summary>
public static class Sha256Checksum
{
    public const string AlgorithmName = "SHA-256";

    public static async Task<string> ComputeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static async Task<string> ComputeFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        return await ComputeAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> VerifyFileAsync(
        string filePath, string expectedSha256Hex, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256Hex);
        var actual = await ComputeFileAsync(filePath, cancellationToken).ConfigureAwait(false);
        return string.Equals(actual, expectedSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
