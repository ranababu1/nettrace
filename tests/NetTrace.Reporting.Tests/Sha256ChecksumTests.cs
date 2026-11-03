using System.Text;
using NetTrace.Reporting.Integrity;

namespace NetTrace.Reporting.Tests;

public class Sha256ChecksumTests : IDisposable
{
    // FIPS 180-2 test vector for "abc".
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "NetTraceTests", Guid.NewGuid().ToString("N"));

    public Sha256ChecksumTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ComputeAsync_MatchesKnownVector()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));

        Assert.Equal(AbcSha256, await Sha256Checksum.ComputeAsync(stream));
    }

    [Fact]
    public async Task ComputeFileAsync_HashesExactFileBytes()
    {
        var path = WriteFile("abc");

        Assert.Equal(AbcSha256, await Sha256Checksum.ComputeFileAsync(path));
    }

    [Fact]
    public async Task VerifyFileAsync_AcceptsUppercaseAndWhitespace()
    {
        var path = WriteFile("abc");

        Assert.True(await Sha256Checksum.VerifyFileAsync(path, " " + AbcSha256.ToUpperInvariant() + "\n"));
    }

    [Fact]
    public async Task VerifyFileAsync_DetectsModification()
    {
        var path = WriteFile("abc");
        var original = await Sha256Checksum.ComputeFileAsync(path);
        await File.AppendAllTextAsync(path, " ");

        Assert.False(await Sha256Checksum.VerifyFileAsync(path, original));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }

        GC.SuppressFinalize(this);
    }

    private string WriteFile(string contents)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(contents));
        return path;
    }
}
