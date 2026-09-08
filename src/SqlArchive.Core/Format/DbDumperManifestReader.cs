using System.Text.Json;
using System.Text.Json.Serialization;
using SqlSchemaDiff.Models;

namespace SqlArchive.Core.Format;

/// <summary>
/// Turns a dbdumper archive's manifest into one side of a schema comparison.
/// <para>
/// The point is not to support dbdumper. It is that <c>verify</c> and
/// <c>restore-as-migration</c> take a <see cref="DatabaseSnapshot"/> on one side and a
/// live database on the other, and once a dbdumper manifest can be turned into the first
/// of those, both commands work on an archive somebody else made without a line of
/// special-casing anywhere else in the codebase.
/// </para>
/// <para>
/// What comes back is honest about being partial. dbdumper's manifest carries no row
/// hash - <see cref="ArchiveTableEntry.RowHash"/> is left null - so verifying against one
/// can compare the schema and the row counts and nothing more. Saying so in the model is
/// better than filling the field with something computed from the data files, which would
/// make an unverifiable archive look verified.
/// </para>
/// </summary>
public static class DbDumperManifestReader
{
    /// <summary>The dbdumper manifest version this understands.</summary>
    public const int SupportedVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() }
    };

    public static DbDumperManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        DbDumperManifest? manifest;

        try
        {
            manifest = JsonSerializer.Deserialize<DbDumperManifest>(json, Options);
        }
        catch(JsonException ex)
        {
            throw new ArchiveFormatException($"The dbdumper manifest could not be parsed: {ex.Message}", ex);
        }

        if(manifest is null)
            throw new ArchiveFormatException("The dbdumper manifest JSON did not deserialize to an object.");

        if(manifest.Database is null)
            throw new ArchiveFormatException(
                "The manifest has no 'database' object, so it is not one of dbdumper's.");

        if(manifest.FormatVersion > SupportedVersion)
            throw new ArchiveFormatException(
                $"The dbdumper manifest declares format version {manifest.FormatVersion} and this build reads " +
                $"version {SupportedVersion}.");

        return manifest;
    }

    public static async Task<DbDumperManifest> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return Parse(ArchiveFormat.Utf8.GetString(buffer.ToArray()));
    }

    /// <summary>
    /// Reads a dbdumper archive and presents it the way the rest of SqlArchive expects:
    /// a snapshot to diff and a table entry per table.
    /// </summary>
    public static ArchiveManifest ToArchiveManifest(DbDumperManifest source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var database = source.Database
            ?? throw new ArchiveFormatException("The manifest has no 'database' object.");

        return new ArchiveManifest
        {
            FormatVersion = ArchiveFormat.CurrentVersion,
            Tool = new ArchiveTool { Name = "dbdumper", Version = source.Tool ?? string.Empty },
            CreatedAt = source.CreatedAt,
            Source = new ArchiveSource
            {
                Server = source.Source?.Server ?? string.Empty,
                Database = source.Source?.Database ?? string.Empty,
                Edition = source.Source?.Edition,
                ProductVersion = source.Source?.ServerVersion,
                Collation = source.Source?.Collation ?? database.Collation
            },

            // dbdumper reads table by table too, and its manifest does not record a mode,
            // so the honest value is the one that claims least.
            Consistency = ArchiveConsistency.PerTable,
            Schema = ToSnapshot(source),
            Tables = database.Tables.Select(ToTableEntry).ToList()
        };
    }

    /// <summary>The schema, as the object SqlSchemaDiff's differ compares.</summary>
    public static DatabaseSnapshot ToSnapshot(DbDumperManifest source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var database = source.Database
            ?? throw new ArchiveFormatException("The manifest has no 'database' object.");

        var objects = new List<DbSchemaObject>();

        foreach(var table in database.Tables)
        {
            objects.Add(new DbSchemaObject
            {
                Type = DbObjectType.Table,
                Schema = table.Schema,
                Name = table.Name,
                Table = ToTableModel(table)
            });
        }

        foreach(var sequence in database.Sequences)
        {
            objects.Add(new DbSchemaObject
            {
                Type = DbObjectType.Sequence,
                Schema = sequence.Schema,
                Name = sequence.Name,
                Sequence = ToSequenceModel(sequence)
            });
        }

        foreach(var module in database.Modules)
        {
            var type = ModuleType(module.Kind);

            objects.Add(new DbSchemaObject
            {
                Type = type,
                Schema = module.Schema,
                Name = module.Name,
                Definition = module.Definition,
                UsesAnsiNulls = module.AnsiNulls,
                UsesQuotedIdentifier = module.QuotedIdentifier,
                Trigger = type == DbObjectType.Trigger
                    ? new TriggerModel
                    {
                        ParentSchema = module.ParentSchema ?? string.Empty,
                        ParentName = module.ParentName ?? string.Empty,
                        IsDisabled = module.IsDisabled,
                        IsInsteadOf = module.IsInsteadOfTrigger
                    }
                    : null
            });
        }

        foreach(var type in database.UserTypes.Where(t => t.IsTableType))
        {
            objects.Add(new DbSchemaObject
            {
                Type = DbObjectType.TableType,
                Schema = type.Schema,
                Name = type.Name,
                TableType = new TableTypeModel
                {
                    Schema = type.Schema,
                    Name = type.Name,
                    Columns = type.Columns.OrderBy(c => c.Ordinal).Select(ToColumnModel).ToList()
                }
            });
        }

        return new DatabaseSnapshot
        {
            DatabaseName = source.Source?.Database ?? string.Empty,
            GeneratedAtUtc = source.CreatedAt,
            GeneratedBy = source.Tool,

            // dbo is implicit everywhere and is not something a script has to create.
            Schemas = database.Schemas
                .Select(s => s.Name)
                .Where(n => !string.Equals(n, "dbo", StringComparison.OrdinalIgnoreCase))
                .ToList(),

            SchemaOwners = database.Schemas
                .Where(s => !string.IsNullOrEmpty(s.Owner))
                .ToDictionary(s => s.Name, s => s.Owner!, StringComparer.Ordinal) is { Count: > 0 } owners
                ? owners
                : null,

            Types = database.UserTypes
                .Where(t => !t.IsTableType)
                .Select(t => new AliasTypeModel
                {
                    Schema = t.Schema,
                    Name = t.Name,
                    BaseTypeName = t.BaseType ?? string.Empty,
                    MaxLength = Clamp(t.MaxLength),
                    Precision = (byte)t.Precision,
                    Scale = (byte)t.Scale,
                    IsNullable = t.IsNullable
                })
                .ToList(),

            Objects = objects
        };
    }

    /// <summary>
    /// One table's manifest entry. <see cref="ArchiveTableEntry.RowHash"/> stays null -
    /// dbdumper has nothing to put there, and a verify against this archive says so
    /// rather than pretending.
    /// </summary>
    public static ArchiveTableEntry ToTableEntry(DbDumperTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var files = table.DataFiles.Count > 0
            ? new List<string>(table.DataFiles)
            : table.DataFile is { Length: > 0 } single ? [single] : [];

        return new ArchiveTableEntry
        {
            Schema = table.Schema,
            Name = table.Name,
            RowCount = table.RowCount,
            RowHash = null,
            DataFiles = files,
            RowFilter = string.IsNullOrEmpty(table.RowFilter) ? null : table.RowFilter,
            DataSkipped = table.DataSkipped,
            OmittedColumns = table.Columns
                .Where(c => c.IsComputed)
                .ToDictionary(c => c.Name, _ => "computed", StringComparer.Ordinal)
        };
    }

    private static TableModel ToTableModel(DbDumperTable table)
    {
        var model = new TableModel
        {
            Schema = table.Schema,
            Name = table.Name,
            IsMemoryOptimized = table.IsMemoryOptimized,
            Columns = table.Columns.OrderBy(c => c.Ordinal).Select(ToColumnModel).ToList(),
            ForeignKeys = table.ForeignKeys.Select(ToForeignKeyModel).ToList(),
            CheckConstraints = table.CheckConstraints
                .Select(c => new CheckConstraintModel
                {
                    Name = c.Name,
                    Definition = c.Definition,
                    IsDisabled = c.IsDisabled,
                    IsNotTrusted = c.IsNotTrusted
                })
                .ToList(),
            Indexes = table.Indexes.Select(ToIndexModel).ToList()
        };

        // SqlSchemaDiff keeps the index behind a PRIMARY KEY or UNIQUE constraint in a
        // list of its own, because the two are scripted differently even though
        // sys.indexes holds both. dbdumper keeps them apart the same way.
        if(table.PrimaryKey is not null)
            model.KeyConstraints.Add(ToKeyConstraint(table.PrimaryKey, "PK"));

        foreach(var unique in table.UniqueConstraints)
            model.KeyConstraints.Add(ToKeyConstraint(unique, "UQ"));

        return model;
    }

    private static ColumnModel ToColumnModel(DbDumperColumn column) => new()
    {
        Name = column.Name,
        TypeSchema = column.TypeSchema ?? string.Empty,
        TypeName = column.TypeName,

        // dbdumper marks an alias type by giving the column a type schema; SqlSchemaDiff
        // carries a flag. BaseTypeName is what dbdumper says drives its encoding, and it
        // is set only when the two differ.
        IsUserDefinedType = !string.IsNullOrEmpty(column.TypeSchema) &&
                            !string.Equals(column.TypeSchema, "sys", StringComparison.OrdinalIgnoreCase),
        MaxLength = Clamp(column.MaxLength),
        Precision = (byte)column.Precision,
        Scale = (byte)column.Scale,
        IsNullable = column.IsNullable,
        IsIdentity = column.IsIdentity,
        IdentitySeed = column.IdentitySeed,
        IdentityIncrement = column.IdentityIncrement,
        IsComputed = column.IsComputed,
        ComputedDefinition = column.ComputedDefinition,
        IsPersisted = column.IsPersisted,
        CollationName = column.Collation,
        IsRowGuid = column.IsRowGuidCol,
        IsSparse = column.IsSparse,
        DefaultName = column.DefaultName,
        DefaultDefinition = column.DefaultDefinition,
        DefaultIsSystemNamed = IsSystemNamedDefault(column.DefaultName)
    };

    private static KeyConstraintModel ToKeyConstraint(DbDumperIndex index, string typeCode) => new()
    {
        TypeCode = typeCode,
        Name = index.Name,
        IndexTypeDesc = index.TypeDescription ?? string.Empty,
        FillFactor = (byte)index.FillFactor,
        IsPadded = index.IsPadded,
        IgnoreDupKey = index.IgnoreDupKey,
        Columns = index.Columns.Select(ToIndexColumn).ToList()
    };

    private static IndexModel ToIndexModel(DbDumperIndex index) => new()
    {
        Name = index.Name,
        IsUnique = index.IsUnique,
        TypeDesc = index.TypeDescription ?? string.Empty,
        FilterDefinition = string.IsNullOrEmpty(index.FilterDefinition) ? null : index.FilterDefinition,
        IsDisabled = index.IsDisabled,
        FillFactor = (byte)index.FillFactor,
        IsPadded = index.IsPadded,
        IgnoreDupKey = index.IgnoreDupKey,
        Columns = index.Columns.Select(ToIndexColumn).ToList()
    };

    private static IndexColumnModel ToIndexColumn(DbDumperIndexColumn column) => new()
    {
        Name = column.Name,
        KeyOrdinal = (byte)column.KeyOrdinal,
        IsDescending = column.IsDescending,
        IsIncluded = column.IsIncluded
    };

    private static ForeignKeyModel ToForeignKeyModel(DbDumperForeignKey key) => new()
    {
        Name = key.Name,
        ReferencedSchema = key.ReferencedSchema,
        ReferencedTable = key.ReferencedTable,
        DeleteActionDesc = key.OnDelete ?? "NO_ACTION",
        UpdateActionDesc = key.OnUpdate ?? "NO_ACTION",
        IsDisabled = key.IsDisabled,
        IsNotTrusted = key.IsNotTrusted,
        Columns = key.Columns
            .Select((c, i) => new ForeignKeyColumnModel
            {
                ParentColumn = c,
                ReferencedColumn = i < key.ReferencedColumns.Count ? key.ReferencedColumns[i] : string.Empty
            })
            .ToList()
    };

    private static SequenceModel ToSequenceModel(DbDumperSequence sequence) => new()
    {
        Schema = sequence.Schema,
        Name = sequence.Name,
        TypeName = sequence.DataType,
        Precision = (byte)sequence.Precision,
        Scale = (byte)sequence.Scale,
        StartValue = sequence.StartValue ?? string.Empty,
        Increment = sequence.Increment ?? string.Empty,
        MinValue = sequence.MinValue,
        MaxValue = sequence.MaxValue,
        IsCycling = sequence.IsCycling,
        IsCached = sequence.IsCached,
        CacheSize = sequence.CacheSize,
        CurrentValue = sequence.CurrentValue
    };

    private static DbObjectType ModuleType(string kind) => kind.ToLowerInvariant() switch
    {
        "view" => DbObjectType.View,
        "function" => DbObjectType.Function,
        "procedure" or "storedprocedure" or "proc" => DbObjectType.StoredProcedure,
        "trigger" => DbObjectType.Trigger,
        _ => throw new ArchiveFormatException(
            $"The manifest describes a module of kind '{kind}', which is not one of view, function, procedure " +
            "or trigger.")
    };

    /// <summary>
    /// <c>sys.columns.max_length</c> is a <c>smallint</c> with -1 for MAX, and that is
    /// what SqlSchemaDiff stores. dbdumper widens it to an int, so a value that does not
    /// fit can only be a MAX column described some other way.
    /// </summary>
    private static short Clamp(int maxLength) =>
        maxLength is >= short.MinValue and <= short.MaxValue ? (short)maxLength : (short)-1;

    /// <summary>
    /// dbdumper does not record whether SQL Server named a default constraint, and the
    /// difference matters: a system-named one is matched by shape rather than by name,
    /// so treating <c>DF__Orders__Total__5629CD9C</c> as deliberate would make every
    /// diff report a rename. The shape of the generated name is the only evidence there
    /// is.
    /// </summary>
    private static bool IsSystemNamedDefault(string? name) =>
        name is { Length: > 3 } &&
        name.StartsWith("DF__", StringComparison.Ordinal) &&
        name.AsSpan(3).LastIndexOf("__", StringComparison.Ordinal) > 0 &&
        name.AsSpan(name.Length - 8).ToString().All(c => Uri.IsHexDigit(c));
}
