using Microsoft.SqlServer.Dac.Model;
using Schemorph.Core.Providers;

namespace Schemorph.Provider.SqlServer.Tests;

/// <summary>
/// DacFx does not document <c>TSqlModel.GetObjects</c>' enumeration order as
/// stable, unlike the Postgres provider's catalog queries (explicit
/// <c>ORDER BY</c>). Built entirely in-memory via <c>TSqlModel.AddObjects</c> —
/// the same construction the compare/apply path already uses in production
/// (<c>ComparisonSession.Open</c>) — so this needs no live database.
/// </summary>
public sealed class SqlServerDesiredStateDeterminismTests
{
    private static TSqlModel BuildModel(params string[] statementsInOrder)
    {
        var model = new TSqlModel(SqlServerVersion.Sql150, new TSqlModelOptions());
        foreach (var statement in statementsInOrder)
        {
            model.AddObjects(statement);
        }
        return model;
    }

    private static readonly string[] Statements =
    {
        "CREATE TABLE dbo.Widgets (Id INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL);",
        "CREATE TABLE dbo.Anvils (Id INT NOT NULL PRIMARY KEY, Weight INT NOT NULL);",
        "CREATE INDEX IX_Widgets_Name ON dbo.Widgets (Name);",
        "CREATE VIEW dbo.WidgetNames AS SELECT Id, Name FROM dbo.Widgets;",
    };

    [Fact]
    public void Rendering_the_same_model_twice_is_byte_identical()
    {
        using var model = BuildModel(Statements);

        var first = SqlServerProvider.RenderDesiredState(model);
        var second = SqlServerProvider.RenderDesiredState(model);

        Assert.Equal(first.Select(f => f.RelativePath), second.Select(f => f.RelativePath));
        Assert.Equal(first.Select(f => f.Content), second.Select(f => f.Content));
    }

    [Fact]
    public void Rendering_is_independent_of_the_order_objects_were_added_in()
    {
        using var forward = BuildModel(Statements);
        using var reversed = BuildModel(Statements.Reverse().ToArray());

        var forwardRendered = SqlServerProvider.RenderDesiredState(forward);
        var reversedRendered = SqlServerProvider.RenderDesiredState(reversed);

        Assert.Equal(
            forwardRendered.Select(f => f.RelativePath),
            reversedRendered.Select(f => f.RelativePath));
        Assert.Equal(
            forwardRendered.Select(f => f.Content),
            reversedRendered.Select(f => f.Content));
    }

    [Fact]
    public void An_index_and_its_owning_table_render_in_alphabetical_object_order()
    {
        // Two tables whose insertion order (Widgets, then Anvils) is the
        // opposite of their alphabetical order — a stale insertion-order
        // dependency would put Widgets first.
        using var model = BuildModel(Statements);

        var rendered = SqlServerProvider.RenderDesiredState(model);
        var tableFiles = rendered.Where(f => f.RelativePath.StartsWith("tables/")).ToList();

        Assert.Equal(
            new[] { "tables/dbo.Anvils.sql", "tables/dbo.Widgets.sql" },
            tableFiles.Select(f => f.RelativePath));
    }
}
