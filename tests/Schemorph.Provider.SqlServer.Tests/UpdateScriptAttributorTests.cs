using Schemorph.Core.Providers;
using Schemorph.Provider.SqlServer;

namespace Schemorph.Provider.SqlServer.Tests;

/// <summary>
/// Attribution over the DacFx update-script shape (observed from the pinned
/// DacFx version: PRINT announcements per object, error/transaction
/// scaffolding between segments). The contract under test: attribute only
/// what the announcements prove, degrade to nothing on anything else.
/// </summary>
public sealed class UpdateScriptAttributorTests
{
    // A faithful miniature of a real generated script: SQLCMD preamble,
    // announced segments (drop / alter+index / rebuild / create), scaffolding
    // between them, progress chatter at the end.
    private const string Script = """
        /* Deployment script */
        GO
        SET ANSI_NULLS ON;
        GO
        :setvar DatabaseName "Db"
        GO
        USE [$(DatabaseName)];
        GO
        BEGIN TRANSACTION
        GO
        PRINT N'Dropping Table [dbo].[Gone]...';
        GO
        DROP TABLE [dbo].[Gone];
        GO
        IF @@ERROR <> 0 AND @@TRANCOUNT > 0 BEGIN ROLLBACK; END
        GO
        PRINT N'Altering Table [dbo].[InPlace]...';
        GO
        ALTER TABLE [dbo].[InPlace] ADD [Email] NVARCHAR (200) NULL;
        GO
        PRINT N'Creating Index [dbo].[InPlace].[IX_Name]...';
        GO
        CREATE NONCLUSTERED INDEX [IX_Name] ON [dbo].[InPlace]([Name] ASC);
        GO
        PRINT N'Starting rebuilding table [dbo].[Rebuilt]...';
        GO
        BEGIN TRANSACTION;
        CREATE TABLE [dbo].[tmp_ms_xx_Rebuilt] ([Id] INT NOT NULL);
        DROP TABLE [dbo].[Rebuilt];
        EXECUTE sp_rename N'[dbo].[tmp_ms_xx_Rebuilt]', N'Rebuilt';
        COMMIT TRANSACTION;
        GO
        PRINT N'Refreshing View [dbo].[Dependent]...';
        GO
        EXECUTE sp_refreshsqlmodule N'[dbo].[Dependent]';
        GO
        IF EXISTS (SELECT * FROM #tmpErrors) ROLLBACK TRANSACTION
        GO
        IF @@TRANCOUNT>0 BEGIN PRINT N'ok' COMMIT TRANSACTION END
        GO
        PRINT N'Update complete.';
        GO
        """;

    private static readonly IReadOnlyList<RawChange> Changes = new[]
    {
        new RawChange("Delete", "Table", "dbo.Gone"),
        new RawChange("Change", "Table", "dbo.InPlace"),
        new RawChange("Change", "Table", "dbo.Rebuilt"),
    };

    private static IReadOnlyDictionary<string, ChangeScript> Attributed() =>
        UpdateScriptAttributor.Attribute(Script, Changes).ToDictionary(s => s.ObjectName);

    [Fact]
    public void Each_announced_segment_lands_on_its_change()
    {
        var byName = Attributed();

        Assert.Equal("DROP TABLE [dbo].[Gone];", byName["dbo.Gone"].Sql);
        Assert.False(byName["dbo.Gone"].Rebuild);
    }

    [Fact]
    public void Segments_for_the_same_object_concatenate_in_order()
    {
        var inPlace = Attributed()["dbo.InPlace"];

        Assert.Contains("ALTER TABLE [dbo].[InPlace]", inPlace.Sql);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_Name]", inPlace.Sql);
        Assert.True(inPlace.Sql.IndexOf("ALTER TABLE", StringComparison.Ordinal)
                    < inPlace.Sql.IndexOf("CREATE NONCLUSTERED", StringComparison.Ordinal));
        Assert.False(inPlace.Rebuild);
    }

    [Fact]
    public void Rebuild_announcements_flag_the_change()
    {
        var rebuilt = Attributed()["dbo.Rebuilt"];

        Assert.True(rebuilt.Rebuild);
        Assert.Contains("sp_rename", rebuilt.Sql);
    }

    [Fact]
    public void Scaffolding_preamble_and_chatter_are_never_payload()
    {
        var all = string.Join("\n---\n", Attributed().Values.Select(s => s.Sql));

        Assert.DoesNotContain(":setvar", all);
        Assert.DoesNotContain("#tmpErrors", all);
        Assert.DoesNotContain("@@ERROR", all);
        Assert.DoesNotContain("Update complete", all);
    }

    [Fact]
    public void Side_work_outside_the_reported_changes_is_dropped()
    {
        // DacFx refreshes dependent views the comparison never reported as
        // changes — attributing them would invent changes.
        Assert.DoesNotContain("dbo.Dependent", Attributed().Keys);
    }

    [Theory]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT NOT NULL;", true)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT DEFAULT 0 NOT NULL;", false)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT CONSTRAINT [DF_C] DEFAULT 0 NOT NULL;", false)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT NULL;", false)]
    [InlineData("ALTER TABLE [dbo].[T] ADD [C] INT IDENTITY (1, 1) NOT NULL;", false)]
    public void Not_null_addition_without_default_is_flagged(string alter, bool expected)
    {
        var script = $"PRINT N'Altering Table [dbo].[T]...';\nGO\n{alter}\nGO\n";

        var result = UpdateScriptAttributor.Attribute(script,
            new[] { new RawChange("Change", "Table", "dbo.T") });

        Assert.Equal(expected, Assert.Single(result).AddsNotNullWithoutDefault);
    }

    // The same NOT NULL-without-default hazard reaches the row-copy of a table
    // rebuild: the new column lives in the rebuilt table but is absent from the
    // INSERT that copies existing rows, so a row-holding table fails at apply —
    // exactly SCHEMORPH101, reached via the rebuild path, not ALTER ADD.
    [Fact]
    public void Rebuild_row_copy_omitting_a_not_null_without_default_column_is_flagged()
    {
        var script = """
            PRINT N'Starting rebuilding table [dbo].[T]...';
            GO
            BEGIN TRANSACTION;
            CREATE TABLE [dbo].[tmp_ms_xx_T] ([Id] INT NOT NULL, [Slug] NVARCHAR (30) NOT NULL);
            IF EXISTS (SELECT TOP 1 1 FROM [dbo].[T])
                INSERT INTO [dbo].[tmp_ms_xx_T] ([Id]) SELECT [Id] FROM [dbo].[T];
            DROP TABLE [dbo].[T];
            EXECUTE sp_rename N'[dbo].[tmp_ms_xx_T]', N'T';
            COMMIT TRANSACTION;
            GO
            """;

        var result = UpdateScriptAttributor.Attribute(script,
            new[] { new RawChange("Change", "Table", "dbo.T") });

        Assert.True(Assert.Single(result).AddsNotNullWithoutDefault);
    }

    [Fact]
    public void Rebuild_row_copy_carrying_the_column_is_not_flagged()
    {
        var script = """
            PRINT N'Starting rebuilding table [dbo].[T]...';
            GO
            BEGIN TRANSACTION;
            CREATE TABLE [dbo].[tmp_ms_xx_T] ([Id] INT NOT NULL, [Code] NVARCHAR (10) NOT NULL);
            IF EXISTS (SELECT TOP 1 1 FROM [dbo].[T])
                INSERT INTO [dbo].[tmp_ms_xx_T] ([Id], [Code]) SELECT [Id], [Code] FROM [dbo].[T];
            DROP TABLE [dbo].[T];
            EXECUTE sp_rename N'[dbo].[tmp_ms_xx_T]', N'T';
            COMMIT TRANSACTION;
            GO
            """;

        var result = UpdateScriptAttributor.Attribute(script,
            new[] { new RawChange("Change", "Table", "dbo.T") });

        Assert.False(Assert.Single(result).AddsNotNullWithoutDefault);
    }

    // A freshly created table (no row-copy) has no existing rows to fail — a
    // NOT NULL column without a default is safe. The rebuild rule must not
    // misfire on a CREATE TABLE that has no INSERT copying into it.
    [Fact]
    public void New_table_with_a_not_null_without_default_column_is_not_flagged()
    {
        var script = """
            PRINT N'Creating Table [dbo].[T]...';
            GO
            CREATE TABLE [dbo].[T] ([Id] INT NOT NULL, [Slug] NVARCHAR (30) NOT NULL);
            GO
            """;

        var result = UpdateScriptAttributor.Attribute(script,
            new[] { new RawChange("Add", "Table", "dbo.T") });

        Assert.False(Assert.Single(result).AddsNotNullWithoutDefault);
    }

    // DacFx announces a table's constraint work under the CONSTRAINT's name, not
    // the table's — so a change reported as "Change Table dbo.T" had no attributable
    // slice at all whenever the edit was expressed as constraint churn (check,
    // default, foreign key). That is the common case for a column default or a CHECK,
    // and it left `sql` null exactly where a reader most needs it. The statements
    // name the owner, and that is stronger evidence than the marker.
    [Fact]
    public void Constraint_segments_attribute_to_the_table_that_owns_them()
    {
        var script = """
            PRINT N'Dropping Check Constraint [dbo].[CK_T_Status]...';
            GO
            ALTER TABLE [dbo].[T] DROP CONSTRAINT [CK_T_Status];
            GO
            PRINT N'Creating Check Constraint [dbo].[CK_T_Status]...';
            GO
            ALTER TABLE [dbo].[T] WITH NOCHECK
                ADD CONSTRAINT [CK_T_Status] CHECK (Status IN ('A', 'B'));
            GO
            """;

        var result = UpdateScriptAttributor.Attribute(script,
            new[] { new RawChange("Change", "Table", "dbo.T") });

        var slice = Assert.Single(result);
        Assert.Equal("dbo.T", slice.ObjectName);
        Assert.Contains("DROP CONSTRAINT [CK_T_Status]", slice.Sql);
        Assert.Contains("ADD CONSTRAINT [CK_T_Status]", slice.Sql);
    }

    [Fact]
    public void A_constraint_on_a_table_that_is_not_a_reported_change_is_not_attributed()
    {
        var result = UpdateScriptAttributor.Attribute("""
            PRINT N'Dropping Check Constraint [dbo].[CK_Other_Status]...';
            GO
            ALTER TABLE [dbo].[Other] DROP CONSTRAINT [CK_Other_Status];
            GO
            """,
            new[] { new RawChange("Change", "Table", "dbo.T") });

        Assert.Empty(result);
    }

    // Ownership is only proven when the statements agree. A segment touching two
    // tables names no single owner, so it attaches to neither.
    [Fact]
    public void A_segment_spanning_two_tables_is_not_attributed()
    {
        var result = UpdateScriptAttributor.Attribute("""
            PRINT N'Dropping Foreign Key [dbo].[FK_A_B]...';
            GO
            ALTER TABLE [dbo].[A] DROP CONSTRAINT [FK_A_B];
            ALTER TABLE [dbo].[B] DROP CONSTRAINT [FK_B_A];
            GO
            """,
            new[] { new RawChange("Change", "Table", "dbo.A"), new RawChange("Change", "Table", "dbo.B") });

        Assert.Empty(result);
    }

    [Fact]
    public void Unrecognized_script_shape_degrades_to_no_attribution()
    {
        var result = UpdateScriptAttributor.Attribute(
            "SET NOCOUNT ON;\nGO\nALTER TABLE [dbo].[X] ADD [C] INT;\nGO\n",
            new[] { new RawChange("Change", "Table", "dbo.X") });

        Assert.Empty(result);   // no announcements → nothing attributed, nothing wrong
    }

    // Verbatim shape captured from a real DacFx update script (cycle-61): the
    // scaffolding batches between segments must not swallow the following segment.
    [Fact]
    public void Real_dacfx_constraint_churn_attributes_both_halves()
    {
        var script = """
            PRINT N'Dropping Check Constraint [dbo].[CK_Flags_Status]...';


            GO
            ALTER TABLE [dbo].[Flags] DROP CONSTRAINT [CK_Flags_Status];


            GO
            IF @@ERROR <> 0
               AND @@TRANCOUNT > 0
                BEGIN
                    ROLLBACK;
                END

            IF OBJECT_ID(N'tempdb..#tmpErrors') IS NULL
                BEGIN
                END

            GO
            PRINT N'Dropping Table [dbo].[__SchemorphHistory]...';


            GO
            DROP TABLE [dbo].[__SchemorphHistory];


            GO
            IF @@ERROR <> 0
               AND @@TRANCOUNT > 0
                BEGIN
                    ROLLBACK;
                END

            GO
            PRINT N'Creating Check Constraint [dbo].[CK_Flags_Status]...';


            GO
            ALTER TABLE [dbo].[Flags] WITH NOCHECK
                ADD CONSTRAINT [CK_Flags_Status] CHECK (Status IN ('A', 'B', 'C'));


            GO
            """;

        var result = UpdateScriptAttributor.Attribute(script,
            new[] { new RawChange("Change", "Table", "dbo.Flags"), new RawChange("Delete", "Table", "dbo.__SchemorphHistory") });

        var slice = result.Single(s => s.ObjectName == "dbo.Flags");
        Assert.Contains("DROP CONSTRAINT", slice.Sql);
        Assert.Contains("ADD CONSTRAINT", slice.Sql);
    }

    // A PRINT that is not an announcement ends the announced work; it must close
    // the open segment. Real scripts continue after it with the generator's own
    // post-commit batches (a USE, a constraint re-validation), and letting those
    // flow into the last announced object cost that object its whole slice —
    // ownership is proven from the statements, and one foreign statement
    // disproves it (cycle-61).
    [Fact]
    public void Chatter_closes_the_open_segment()
    {
        var result = UpdateScriptAttributor.Attribute("""
            PRINT N'Dropping Check Constraint [dbo].[CK_T_Status]...';
            GO
            ALTER TABLE [dbo].[T] DROP CONSTRAINT [CK_T_Status];
            GO
            PRINT N'Checking existing data against newly created constraints';
            GO
            USE [$(DatabaseName)];
            GO
            ALTER TABLE [dbo].[T] WITH CHECK CHECK CONSTRAINT [CK_T_Status];
            GO
            """,
            new[] { new RawChange("Change", "Table", "dbo.T") });

        var slice = Assert.Single(result);
        Assert.Equal("ALTER TABLE [dbo].[T] DROP CONSTRAINT [CK_T_Status];", slice.Sql);
    }
}
