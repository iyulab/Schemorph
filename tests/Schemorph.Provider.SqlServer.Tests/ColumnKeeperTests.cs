namespace Schemorph.Provider.SqlServer.Tests;

/// <summary>
/// The text half of keeping a dropped column: the live column's definition added to the desired table's
/// <c>CREATE TABLE</c>. Needs no database - what the engine then makes of the model is the integration
/// tests' question.
/// </summary>
public class ColumnKeeperTests
{
    private const string Live = """
        CREATE TABLE [dbo].[Products] (
            [Id]    INT            NOT NULL,
            [Name]  NVARCHAR (50)  NOT NULL,
            [Notes] NVARCHAR (100) COLLATE Latin1_General_100_CI_AS NULL,
            [Tag]   NVARCHAR (10)  NOT NULL
        );
        """;

    [Fact]
    public void The_kept_columns_are_added_to_the_table_the_batch_creates()
    {
        var definitions = ColumnKeeper.DefinitionsIn(Live, ["Notes", "Tag"])!;

        var text = ColumnKeeper.WithColumns(
            "CREATE TABLE dbo.Products (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(50) NOT NULL);",
            new(StringComparer.OrdinalIgnoreCase) { ["dbo.Products"] = definitions },
            restored: new(StringComparer.OrdinalIgnoreCase));

        Assert.NotNull(text);
        Assert.Contains("[Notes]", text);
        Assert.Contains("Latin1_General_100_CI_AS", text);   // the live definition travels whole
        Assert.Contains("[Tag]", text);
        Assert.Contains("PRIMARY KEY", text);                // what the desired state wrote stays
    }

    [Fact]
    public void A_batch_that_creates_another_table_is_left_alone_and_the_table_is_not_marked_found()
    {
        var restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var text = ColumnKeeper.WithColumns(
            "CREATE TABLE dbo.Orders (Id INT NOT NULL);",
            new(StringComparer.OrdinalIgnoreCase) { ["dbo.Products"] = ColumnKeeper.DefinitionsIn(Live, ["Notes"])! },
            restored);

        Assert.Null(text);
        Assert.Empty(restored);
    }

    [Fact]
    public void A_table_written_without_a_schema_is_dbo_and_is_found()
    {
        var restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var text = ColumnKeeper.WithColumns(
            "CREATE TABLE Products (Id INT NOT NULL);",
            new(StringComparer.OrdinalIgnoreCase) { ["dbo.Products"] = ColumnKeeper.DefinitionsIn(Live, ["Notes"])! },
            restored);

        Assert.Contains("[Notes]", text);
        Assert.Contains("dbo.Products", restored);
    }

    [Fact]
    public void A_column_the_live_table_does_not_have_cannot_be_kept()
    {
        Assert.Null(ColumnKeeper.DefinitionsIn(Live, ["Notes", "Nope"]));
    }

    [Fact]
    public void A_batch_that_does_not_parse_is_left_alone()
    {
        Assert.Null(ColumnKeeper.WithColumns(
            "CREATE TABLE dbo.Products (",
            new(StringComparer.OrdinalIgnoreCase) { ["dbo.Products"] = ColumnKeeper.DefinitionsIn(Live, ["Notes"])! },
            restored: new(StringComparer.OrdinalIgnoreCase)));
    }
}
