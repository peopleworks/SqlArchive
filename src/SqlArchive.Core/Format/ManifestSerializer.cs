using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlArchive.Core.Format;

/// <summary>
/// Reads and writes <see cref="ArchiveManifest"/> as JSON, with one shared set of
/// options so that no consumer has to rebuild them and get one of them wrong.
/// </summary>
public static class ManifestSerializer
{
    /// <summary>
    /// Indented, camel-cased property names, dictionary keys left exactly as they are -
    /// they are entry names and column names, and camel-casing them would rename the
    /// files the manifest points at. Unknown properties are kept rather than dropped, so
    /// an archive from a later version can be read, described and written back out
    /// without losing what this build did not understand.
    /// <para>
    /// Nulls are written rather than omitted. <c>"rowFilter": null</c> tells a reader
    /// that the field exists and this table was not filtered; leaving it out leaves them
    /// to work out whether the export had no filter or the writer had no such concept.
    /// </para>
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(ArchiveManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, Options);
    }

    public static ArchiveManifest Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        // Whose format this is has to be settled before anything is bound, not after. A
        // dbdumper manifest carries "tool" as a string where ours is an object, so
        // deserializing it first fails with a type error about a property, and the reader
        // ends up saying the JSON is malformed when in fact it is a perfectly good file of
        // a different kind.
        Discriminate(json);

        try
        {
            return Validate(JsonSerializer.Deserialize<ArchiveManifest>(json, Options));
        }
        catch(JsonException ex)
        {
            throw new ArchiveFormatException($"The manifest could not be parsed: {ex.Message}", ex);
        }
    }

    public static async Task WriteAsync(ArchiveManifest manifest, Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(stream);

        await JsonSerializer.SerializeAsync(stream, manifest, Options, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ArchiveManifest> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        // Read whole rather than streamed on purpose: a manifest is kilobytes even for a
        // database of thousands of objects, and having the text in hand is what lets a
        // failed parse say which format the file actually is instead of only that it is
        // not this one.
        return Deserialize(ArchiveFormat.Utf8.GetString(buffer.ToArray()));
    }

    /// <summary>
    /// The checks that decide whether what came back is one of our manifests, one of
    /// dbdumper's, or something else entirely.
    /// </summary>
    /// <remarks>
    /// A version newer than this build understands is deliberately <b>not</b> an error.
    /// The whole point of an archive is to be opened by whoever is there when the source
    /// is not, and "this file is version 3 and I read version 1, here is what I can still
    /// tell you about it" is a far better answer than a parse failure. Refusing happens
    /// where it matters - at the point of restoring data - and is
    /// <see cref="ArchiveManifest.RequiresNewerReader"/>'s job to signal.
    /// </remarks>
    private static ArchiveManifest Validate(ArchiveManifest? manifest)
    {
        if(manifest is null)
            throw new ArchiveFormatException("The manifest JSON did not deserialize to an object.");

        if(manifest.FormatVersion < 1)
            throw new ArchiveFormatException(
                $"The manifest declares format version {manifest.FormatVersion}, and versions start at 1.");

        return manifest;
    }

    /// <summary>
    /// Decides, from the raw JSON, whether this is one of ours before anything is bound
    /// to a model that assumes it is.
    /// </summary>
    private static void Discriminate(string json)
    {
        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch(JsonException ex)
        {
            throw new ArchiveFormatException($"The manifest could not be parsed: {ex.Message}", ex);
        }

        if(root.ValueKind != JsonValueKind.Object)
            throw new ArchiveFormatException($"The manifest is a JSON {root.ValueKind}, not an object.");

        if(root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String)
        {
            var name = format.GetString();

            if(!string.Equals(name, ArchiveFormat.FormatName, StringComparison.OrdinalIgnoreCase))
                throw new ArchiveFormatException(
                    $"The manifest declares format '{name}', which is not '{ArchiveFormat.FormatName}'.");

            return;
        }

        if(root.TryGetProperty("database", out _) &&
           !root.TryGetProperty("schema", out _) &&
           !root.TryGetProperty("tables", out _))
        {
            throw new ArchiveFormatException(
                "This manifest is dbdumper's, not SqlArchive's - it carries a 'database' object and no 'schema' or " +
                $"'tables'. Read it with {nameof(DbDumperManifestReader)}, which turns it into one side of a " +
                "snapshot so it can be diffed and restored. SqlArchive does not write dbdumper's format: without a " +
                "row hash there is nothing to verify beyond a row count.");
        }
    }
}
