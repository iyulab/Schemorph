using Schemorph.Core.Providers;

namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// P4's safety lint (strategy 3, ADR-0002): provable, AST-judged risky
/// constructs in a migration script — mirrors <see cref="MigrationScriptLinter"/>'s
/// SQL Server coverage (Truncate/UnfilteredUpdate/UnfilteredDelete/PermissionChange),
/// on PostgreSQL's own grammar.
/// </summary>
public class PgMigrationLinterTests
{
    [Fact]
    public void Truncate_is_flagged()
    {
        var signals = PgMigrationLinter.Lint("TRUNCATE TABLE \"Orders\";");
        Assert.Equal(new[] { MigrationLintSignal.Truncate }, signals);
    }

    [Fact]
    public void An_update_without_where_is_flagged()
    {
        var signals = PgMigrationLinter.Lint("UPDATE \"Orders\" SET \"Status\" = 'closed';");
        Assert.Equal(new[] { MigrationLintSignal.UnfilteredUpdate }, signals);
    }

    [Fact]
    public void An_update_with_where_is_not_flagged()
    {
        var signals = PgMigrationLinter.Lint("UPDATE \"Orders\" SET \"Status\" = 'closed' WHERE \"Id\" = 1;");
        Assert.Empty(signals);
    }

    [Fact]
    public void A_delete_without_where_is_flagged()
    {
        var signals = PgMigrationLinter.Lint("DELETE FROM \"Orders\";");
        Assert.Equal(new[] { MigrationLintSignal.UnfilteredDelete }, signals);
    }

    [Fact]
    public void A_delete_with_where_is_not_flagged()
    {
        var signals = PgMigrationLinter.Lint("DELETE FROM \"Orders\" WHERE \"Id\" = 1;");
        Assert.Empty(signals);
    }

    [Theory]
    [InlineData("GRANT SELECT ON \"Orders\" TO app_reader;")]
    [InlineData("REVOKE SELECT ON \"Orders\" FROM app_reader;")]
    public void A_grant_or_revoke_is_flagged_as_a_permission_change(string sql)
    {
        var signals = PgMigrationLinter.Lint(sql);
        Assert.Equal(new[] { MigrationLintSignal.PermissionChange }, signals);
    }

    [Fact]
    public void Multiple_signals_in_one_script_are_all_reported_in_enum_order()
    {
        var signals = PgMigrationLinter.Lint("""
            GRANT SELECT ON "Orders" TO app_reader;
            DELETE FROM "Orders";
            TRUNCATE TABLE "Audit";
            """);

        Assert.Equal(
            new[] { MigrationLintSignal.Truncate, MigrationLintSignal.UnfilteredDelete, MigrationLintSignal.PermissionChange },
            signals);
    }

    [Fact]
    public void An_unparseable_script_yields_no_signals()
    {
        var signals = PgMigrationLinter.Lint("this is not sql at all {{{");
        Assert.Empty(signals);
    }

    [Fact]
    public void A_plain_insert_is_not_flagged()
    {
        var signals = PgMigrationLinter.Lint("INSERT INTO \"Orders\" (\"Id\") VALUES (1);");
        Assert.Empty(signals);
    }
}
