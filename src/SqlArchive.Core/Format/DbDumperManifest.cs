using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlArchive.Core.Format;

/// <summary>
/// dbdumper's <c>manifest.json</c>, version 1, as its <c>internal/model</c> package
/// declares it.
/// <para>
/// Read, never written. SqlArchive does not produce this format: without a per-table row
/// hash there is nothing to verify beyond a row count, and improving on the row count is
/// the reason this tool exists. Reading it is worth the code, though - an archive
/// somebody already made with dbdumper can then be inspected, diffed against a live
/// database, and restored as a migration.
/// </para>
/// <para>
/// The property names below are dbdumper's, not ours; the reader maps them onto
/// SqlSchemaDiff's snapshot in <see cref="DbDumperManifestReader"/>. Unknown properties
/// are ignored, which is what makes this survive dbdumper adding to its own format.
/// </para>
/// </summary>
public sealed class DbDumperManifest
{
    public int FormatVersion { get; set; }

    /// <summary>A string here, where ours is an object. One of the ways the two are told apart.</summary>
    public string? Tool { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DbDumperSource? Source { get; set; }

    public DbDumperDatabase? Database { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; set; }
}

public sealed class DbDumperSource
{
    public string? Server { get; set; }

    public string? Database { get; set; }

    public string? ServerVersion { get; set; }

    public string? Edition { get; set; }

    public string? Collation { get; set; }

    public bool SchemaOnly { get; set; }
}

public sealed class DbDumperDatabase
{
    public string? Collation { get; set; }

    public List<DbDumperSchema> Schemas { get; set; } = [];

    public List<DbDumperUserType> UserTypes { get; set; } = [];

    public List<DbDumperSequence> Sequences { get; set; } = [];

    public List<DbDumperTable> Tables { get; set; } = [];

    public List<DbDumperModule> Modules { get; set; } = [];
}

public sealed class DbDumperSchema
{
    public string Name { get; set; } = string.Empty;

    public string? Owner { get; set; }
}

public sealed class DbDumperUserType
{
    public string Schema { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsTableType { get; set; }

    public string? BaseType { get; set; }

    public int MaxLength { get; set; }

    public int Precision { get; set; }

    public int Scale { get; set; }

    public bool IsNullable { get; set; }

    public List<DbDumperColumn> Columns { get; set; } = [];
}

public sealed class DbDumperSequence
{
    public string Schema { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string DataType { get; set; } = string.Empty;

    public int Precision { get; set; }

    public int Scale { get; set; }

    public string? StartValue { get; set; }

    public string? Increment { get; set; }

    public string? MinValue { get; set; }

    public string? MaxValue { get; set; }

    public bool IsCycling { get; set; }

    public bool IsCached { get; set; }

    public int? CacheSize { get; set; }

    public string? CurrentValue { get; set; }
}

public sealed class DbDumperTable
{
    public string Schema { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<DbDumperColumn> Columns { get; set; } = [];

    public DbDumperIndex? PrimaryKey { get; set; }

    public List<DbDumperIndex> UniqueConstraints { get; set; } = [];

    public List<DbDumperIndex> Indexes { get; set; } = [];

    public List<DbDumperForeignKey> ForeignKeys { get; set; } = [];

    public List<DbDumperCheckConstraint> CheckConstraints { get; set; } = [];

    public bool IsMemoryOptimized { get; set; }

    public List<string> DataColumns { get; set; } = [];

    /// <summary>Set when the table went into one file; <see cref="DataFiles"/> when it was split.</summary>
    public string? DataFile { get; set; }

    public List<string> DataFiles { get; set; } = [];

    public long RowCount { get; set; }

    public bool DataSkipped { get; set; }

    public string? RowFilter { get; set; }
}

public sealed class DbDumperColumn
{
    public string Name { get; set; } = string.Empty;

    public int Ordinal { get; set; }

    public string? TypeSchema { get; set; }

    public string TypeName { get; set; } = string.Empty;

    /// <summary>The system type behind an alias type. dbdumper says explicitly that this is what drives encoding.</summary>
    public string? BaseTypeName { get; set; }

    public int MaxLength { get; set; }

    public int Precision { get; set; }

    public int Scale { get; set; }

    public bool IsNullable { get; set; }

    public string? Collation { get; set; }

    public bool IsIdentity { get; set; }

    public string? IdentitySeed { get; set; }

    public string? IdentityIncrement { get; set; }

    public bool IsComputed { get; set; }

    public string? ComputedDefinition { get; set; }

    public bool IsPersisted { get; set; }

    public string? DefaultName { get; set; }

    public string? DefaultDefinition { get; set; }

    [JsonPropertyName("isRowGuidCol")]
    public bool IsRowGuidCol { get; set; }

    public bool IsSparse { get; set; }
}

public sealed class DbDumperIndex
{
    public string Name { get; set; } = string.Empty;

    /// <summary>CLUSTERED, NONCLUSTERED, and so on. dbdumper writes it under the name <c>type</c>.</summary>
    [JsonPropertyName("type")]
    public string? TypeDescription { get; set; }

    public bool IsPrimaryKey { get; set; }

    public bool IsUniqueConstraint { get; set; }

    public bool IsUnique { get; set; }

    public List<DbDumperIndexColumn> Columns { get; set; } = [];

    public string? FilterDefinition { get; set; }

    public int FillFactor { get; set; }

    public bool IsPadded { get; set; }

    public bool IgnoreDupKey { get; set; }

    public bool IsDisabled { get; set; }
}

public sealed class DbDumperIndexColumn
{
    public string Name { get; set; } = string.Empty;

    public int KeyOrdinal { get; set; }

    public bool IsDescending { get; set; }

    public bool IsIncluded { get; set; }
}

public sealed class DbDumperForeignKey
{
    public string Name { get; set; } = string.Empty;

    public List<string> Columns { get; set; } = [];

    public string ReferencedSchema { get; set; } = string.Empty;

    public string ReferencedTable { get; set; } = string.Empty;

    public List<string> ReferencedColumns { get; set; } = [];

    public string? OnDelete { get; set; }

    public string? OnUpdate { get; set; }

    public bool IsDisabled { get; set; }

    public bool IsNotTrusted { get; set; }
}

public sealed class DbDumperCheckConstraint
{
    public string Name { get; set; } = string.Empty;

    public string Definition { get; set; } = string.Empty;

    public bool IsDisabled { get; set; }

    public bool IsNotTrusted { get; set; }
}

public sealed class DbDumperModule
{
    public string Schema { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>view, function, procedure or trigger.</summary>
    public string Kind { get; set; } = string.Empty;

    public string Definition { get; set; } = string.Empty;

    public string? ParentSchema { get; set; }

    public string? ParentName { get; set; }

    public bool AnsiNulls { get; set; } = true;

    public bool QuotedIdentifier { get; set; } = true;

    public bool IsDisabled { get; set; }

    public bool IsSchemaBound { get; set; }

    public bool IsInsteadOfTrigger { get; set; }
}
