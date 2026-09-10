using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlArchive.Core.Verify;

/// <summary>
/// The verdict as JSON, for a build to read.
/// <para>
/// The same object the readable report is rendered from, so the two can never disagree
/// about what was found. Enums are written as their names rather than as numbers: a
/// pipeline that greps for <c>"contentDiffers"</c> keeps working when a value is added
/// in the middle of the enum, and one that greps for <c>7</c> does not.
/// </para>
/// </summary>
public static class VerifyJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(VerifyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, Options);
    }

    public static async Task WriteAsync(VerifyReport report, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if(!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(path, Serialize(report), Format.ArchiveFormat.Utf8, cancellationToken)
            .ConfigureAwait(false);
    }
}
