using Schemorph.Core.Providers;
using Schemorph.Provider.SqlServer;

namespace Schemorph.Provider.SqlServer.Tests;

/// <summary>
/// The dialect half of the migration safety lint: AST-proven signals only,
/// nothing on scripts it cannot parse.
/// </summary>
public sealed class MigrationScriptLinterTests
{
    [Fact]
    public void Truncate_is_flagged()
        => Assert.Equal(new[] { MigrationLintSignal.Truncate },
            MigrationScriptLinter.Lint("TRUNCATE TABLE dbo.Log;"));

    [Fact]
    public void Unfiltered_update_and_delete_are_flagged()
        => Assert.Equal(new[] { MigrationLintSignal.UnfilteredUpdate, MigrationLintSignal.UnfilteredDelete },
            MigrationScriptLinter.Lint("UPDATE dbo.T SET A = 1;\nGO\nDELETE FROM dbo.U;"));

    [Fact]
    public void Filtered_update_and_delete_are_silent()
        => Assert.Empty(MigrationScriptLinter.Lint(
            "UPDATE dbo.T SET A = 1 WHERE Id = 3;\nDELETE FROM dbo.U WHERE Stale = 1;"));

    [Fact]
    public void Permission_statements_are_flagged_once()
        => Assert.Equal(new[] { MigrationLintSignal.PermissionChange },
            MigrationScriptLinter.Lint("GRANT SELECT ON dbo.T TO app;\nDENY DELETE ON dbo.T TO app;"));

    [Fact]
    public void Plain_ddl_and_guarded_dml_are_silent()
        => Assert.Empty(MigrationScriptLinter.Lint(
            "CREATE TABLE dbo.NewT (Id INT NOT NULL);\nGO\nINSERT INTO dbo.NewT (Id) VALUES (1);"));

    [Fact]
    public void Unparseable_script_proves_nothing()
        => Assert.Empty(MigrationScriptLinter.Lint("THIS IS NOT T-SQL ;;; %%%"));
}
