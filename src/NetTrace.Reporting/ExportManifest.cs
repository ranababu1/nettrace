using System.Text.Json;
using NetTrace.Reporting.Integrity;

namespace NetTrace.Reporting;

public sealed record ManifestFile(string Name, long SizeBytes, string Sha256);

public sealed record ExportManifest(
    string SchemaVersion,
    string Algorithm,
    DateTimeOffset GeneratedAtUtc,
    string IntegrityNote,
    IReadOnlyList<ManifestFile> Files);

public sealed record FileVerification(string Name, bool Matches, string? Problem);

public static class ExportManifestFile
{
    public const string FileName = "manifest.json";
    public const string SchemaVersion = "1.0";

    public const string IntegrityNote =
        "The SHA-256 hashes detect changes made to these files after export. They do not prove that the "
        + "underlying measurements are independently verified or tamper-proof.";

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    /// <summary>Re-hashes every file listed in the manifest in <paramref name="directory"/>.</summary>
    public static async Task<IReadOnlyList<FileVerification>> VerifyAsync(string directory, CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(directory, FileName);
        if (!File.Exists(manifestPath))
        {
            return [new FileVerification(FileName, false, "The manifest is missing.")];
        }

        ExportManifest? manifest;
        await using (var stream = File.OpenRead(manifestPath))
        {
            manifest = await JsonSerializer.DeserializeAsync<ExportManifest>(stream, Options, cancellationToken).ConfigureAwait(false);
        }

        if (manifest is null)
        {
            return [new FileVerification(FileName, false, "The manifest could not be read.")];
        }

        var results = new List<FileVerification>();
        foreach (var file in manifest.Files)
        {
            // Names come from a file on disk, so never follow a path out of the export folder.
            if (file.Name != Path.GetFileName(file.Name))
            {
                results.Add(new FileVerification(file.Name, false, "The manifest lists an invalid file name."));
                continue;
            }

            var path = Path.Combine(directory, file.Name);
            if (!File.Exists(path))
            {
                results.Add(new FileVerification(file.Name, false, "The file is missing."));
                continue;
            }

            var matches = new FileInfo(path).Length == file.SizeBytes
                && await Sha256Checksum.VerifyFileAsync(path, file.Sha256, cancellationToken).ConfigureAwait(false);
            results.Add(new FileVerification(file.Name, matches, matches ? null : "The file has changed since export."));
        }

        return results;
    }
}
