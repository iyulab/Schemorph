using Microsoft.SqlServer.Dac.Model;

namespace Schemorph.Provider.SqlServer.Tests;

/// <summary>
/// What a kept column still needs: the alias type, the sequence a default draws from, the function a
/// computed column calls - when the desired state no longer declares them. Built from a model made of
/// scripts, so it needs no database.
/// </summary>
public class ColumnKeeperSupportTests : IDisposable
{
    private readonly TSqlModel _model = new(SqlServerVersion.Sql150, new TSqlModelOptions());

    private static readonly IReadOnlyDictionary<string, SqlServerProvider.DroppedColumns> TagDropped =
        new Dictionary<string, SqlServerProvider.DroppedColumns>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.Items"] = new("dbo", "Items", ["Tag", "Loud", "Seq"]),
        };

    public ColumnKeeperSupportTests()
    {
        _model.AddObjects("CREATE TYPE dbo.TagType FROM NVARCHAR(10) NOT NULL;");
        _model.AddObjects("CREATE SEQUENCE dbo.TagSeq AS INT START WITH 100;");
        _model.AddObjects("CREATE FUNCTION dbo.Shout(@text NVARCHAR(50)) RETURNS NVARCHAR(50) AS BEGIN RETURN UPPER(@text); END");
        _model.AddObjects("""
            CREATE TABLE dbo.Items (
                Id INT NOT NULL PRIMARY KEY,
                Name NVARCHAR(50) NOT NULL,
                Tag dbo.TagType,
                Loud AS dbo.Shout(Name),
                Seq INT NOT NULL CONSTRAINT DF_Items_Seq DEFAULT (NEXT VALUE FOR dbo.TagSeq)
            );
            """);
    }

    private List<TSqlObject> Hanging() =>
        _model.GetObjects(DacQueryScopes.UserDefined, DefaultConstraint.TypeClass).ToList();

    private static HashSet<string> Deleted(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void The_type_the_sequence_and_the_function_the_kept_columns_read_are_all_found()
    {
        var (found, why) = ColumnKeeper.SupportingObjects(
            _model, Deleted("dbo.TagType", "dbo.TagSeq", "dbo.Shout"), TagDropped, Hanging());

        Assert.Null(why);
        Assert.NotNull(found);
        Assert.Equal(
            ["DROP FUNCTION [dbo].[Shout];", "DROP SEQUENCE [dbo].[TagSeq];", "DROP TYPE [dbo].[TagType];"],
            found.Select(s => s.Drop).Order(StringComparer.Ordinal).ToArray());
        Assert.All(found, s => Assert.Equal("dbo.Items", s.Table));
        Assert.All(found, s => Assert.StartsWith("CREATE ", s.Script, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_object_the_desired_state_still_declares_is_left_alone()
    {
        var (found, _) = ColumnKeeper.SupportingObjects(_model, Deleted("dbo.TagType"), TagDropped, Hanging());

        var only = Assert.Single(found!);
        Assert.Equal("DROP TYPE [dbo].[TagType];", only.Drop);
    }

    [Fact]
    public void Nothing_is_found_when_nothing_the_comparison_removes_is_read()
    {
        var (found, why) = ColumnKeeper.SupportingObjects(_model, Deleted(), TagDropped, Hanging());

        Assert.Null(why);
        Assert.Empty(found!);
    }

    [Fact]
    public void A_column_that_is_not_being_kept_pulls_nothing_in()
    {
        var onlyName = new Dictionary<string, SqlServerProvider.DroppedColumns>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.Items"] = new("dbo", "Items", ["Name"]),
        };

        var (found, _) = ColumnKeeper.SupportingObjects(
            _model, Deleted("dbo.TagType", "dbo.TagSeq", "dbo.Shout"), onlyName, []);

        Assert.Empty(found!);
    }

    public void Dispose() => _model.Dispose();
}
