using SqlArchive.Core.Format;
using SqlSchemaDiff.Models;

namespace SqlArchive.Core.Tests;

/// <summary>
/// Which columns the archive carries. Three kinds are left out, and all three because
/// SQL Server refuses to be told what they are - so an archive that carried them could
/// never be restored, and could never pass its own verify afterwards.
/// </summary>
public sealed class FormatColumnTests
{
    private static TableModel Table(params ColumnModel[] columns) => new()
    {
        Schema = "dbo",
        Name = "T",
        Columns = [.. columns]
    };

    private static ColumnModel Column(string name, string type) => new() { Name = name, TypeName = type };

    [Fact]
    public void OrdinaryColumnsAreCarriedInOrder()
    {
        var columns = ArchiveColumns.For(Table(
            Column("Id", "int"),
            Column("Name", "nvarchar"),
            Column("Total", "decimal")));

        Assert.Equal(["Id", "Name", "Total"], columns.Select(c => c.Name).ToArray());
    }

    /// <summary>
    /// An identity column is carried. It can be written back, with IDENTITY_INSERT, and
    /// its values are data - a foreign key somewhere else points at them.
    /// </summary>
    [Fact]
    public void AnIdentityColumnIsCarried()
    {
        var column = new ColumnModel { Name = "Id", TypeName = "int", IsIdentity = true };

        Assert.True(ArchiveColumns.IsArchived(column));
    }

    [Fact]
    public void AComputedColumnIsNot()
    {
        var column = new ColumnModel { Name = "Total", TypeName = "decimal", IsComputed = true, IsPersisted = true };

        Assert.False(ArchiveColumns.IsArchived(column));
    }

    /// <summary>
    /// "Cannot insert an explicit value into a timestamp column." Its value belongs to
    /// one database's counter, and a restored row necessarily gets a different one, so
    /// including it would make verify-after-restore impossible on every table that has
    /// one.
    /// </summary>
    [Theory]
    [InlineData("timestamp")]
    [InlineData("rowversion")]
    [InlineData("ROWVERSION")]
    public void ARowVersionIsNot(string typeName) =>
        Assert.False(ArchiveColumns.IsArchived(Column("V", typeName)));

    /// <summary>
    /// Verified against SQL Server 2025: a GENERATED ALWAYS period column is refused even
    /// with SYSTEM_VERSIONING set to OFF, which is the state a restore loads rows in.
    /// </summary>
    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    public void APeriodColumnIsNot(byte generatedAlwaysType)
    {
        var column = new ColumnModel { Name = "SysStart", TypeName = "datetime2", GeneratedAlwaysType = generatedAlwaysType };

        Assert.False(ArchiveColumns.IsArchived(column));
    }

    [Fact]
    public void TheOmittedOnesAreListedWithTheirReason()
    {
        var table = Table(
            Column("Id", "int"),
            new ColumnModel { Name = "Total", TypeName = "decimal", IsComputed = true },
            Column("V", "timestamp"),
            new ColumnModel { Name = "SysStart", TypeName = "datetime2", GeneratedAlwaysType = 1 });

        Assert.Equal(
            [("Total", "computed"), ("V", "rowversion"), ("SysStart", "GENERATED ALWAYS")],
            ArchiveColumns.Omitted(table).ToArray());

        Assert.Equal(["Id"], ArchiveColumns.For(table).Select(c => c.Name).ToArray());
    }

    /// <summary>
    /// A column declared as an alias type is encoded as the system type underneath it,
    /// because that is what comes down the wire - and it is what a live reader reports
    /// for the same column, which is what makes the two paths agree.
    /// </summary>
    [Fact]
    public void AnAliasTypeIsResolvedToWhatItIsBuiltFrom()
    {
        var table = Table(new ColumnModel
        {
            Name = "Amount",
            TypeSchema = "dbo",
            TypeName = "Dinero",
            IsUserDefinedType = true
        });

        var aliases = new[]
        {
            new AliasTypeModel { Schema = "dbo", Name = "Dinero", BaseTypeName = "decimal", Precision = 19, Scale = 4 }
        };

        var columns = ArchiveColumns.For(table, aliases);

        Assert.Equal("decimal", columns[0].TypeName);
        Assert.Equal(SqlValueKind.Decimal, columns[0].Kind);
        Assert.Equal(19, columns[0].Precision);
        Assert.Equal(4, columns[0].Scale);
    }

    [Fact]
    public void AnAliasTypeWithNoDeclarationIsRefusedRatherThanGuessedAt()
    {
        var table = Table(new ColumnModel
        {
            Name = "Amount",
            TypeSchema = "dbo",
            TypeName = "Dinero",
            IsUserDefinedType = true
        });

        Assert.Throws<ArchiveEncodingException>(() => ArchiveColumns.For(table));
    }

    [Theory]
    [InlineData("bit", SqlValueKind.Boolean)]
    [InlineData("int", SqlValueKind.Integer)]
    [InlineData("numeric", SqlValueKind.Decimal)]
    [InlineData("smallmoney", SqlValueKind.Money)]
    [InlineData("float", SqlValueKind.Double)]
    [InlineData("real", SqlValueKind.Single)]
    [InlineData("date", SqlValueKind.Date)]
    [InlineData("time", SqlValueKind.Time)]
    [InlineData("smalldatetime", SqlValueKind.DateTime)]
    [InlineData("datetimeoffset", SqlValueKind.DateTimeOffset)]
    [InlineData("ntext", SqlValueKind.String)]
    [InlineData("uniqueidentifier", SqlValueKind.Guid)]
    [InlineData("image", SqlValueKind.Binary)]
    [InlineData("geometry", SqlValueKind.UserDefinedBinary)]
    public void EveryTypeInTheTableMapsToItsKind(string typeName, SqlValueKind expected) =>
        Assert.Equal(expected, new ArchiveColumn("C", typeName).Kind);

    [Fact]
    public void TypeNamesAreCaseAndBracketInsensitive()
    {
        Assert.Equal("nvarchar", new ArchiveColumn("C", "NVarChar").TypeName);
        Assert.Equal("nvarchar", new ArchiveColumn("C", "[nvarchar]").TypeName);
    }

    /// <summary>
    /// The mapping back to a parameter, which is the encoding table read one step
    /// further. The three CLR types go back as varbinary, which is what lets a restore
    /// run without Microsoft.SqlServer.Types.
    /// </summary>
    [Theory]
    [InlineData("hierarchyid")]
    [InlineData("geography")]
    [InlineData("geometry")]
    public void AClrTypeGoesBackAsVarBinary(string typeName) =>
        Assert.Equal(System.Data.SqlDbType.VarBinary, RowDecoder.ParameterType(new ArchiveColumn("C", typeName)));

    [Theory]
    [InlineData("datetime", System.Data.SqlDbType.DateTime)]
    [InlineData("datetime2", System.Data.SqlDbType.DateTime2)]
    [InlineData("smalldatetime", System.Data.SqlDbType.SmallDateTime)]
    [InlineData("money", System.Data.SqlDbType.Money)]
    [InlineData("smallmoney", System.Data.SqlDbType.SmallMoney)]
    [InlineData("nvarchar", System.Data.SqlDbType.NVarChar)]
    [InlineData("xml", System.Data.SqlDbType.Xml)]
    public void ATypeThatSharesAClrTypeStillGoesBackAsItself(string typeName, System.Data.SqlDbType expected) =>
        Assert.Equal(expected, RowDecoder.ParameterType(new ArchiveColumn("C", typeName)));
}
