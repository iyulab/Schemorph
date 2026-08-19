namespace Schemorph.Provider.Postgres.Tests;

using ProgrammableFile = PgDesiredState.ProgrammableFile;

/// <summary>
/// P3 (ADR-0002 strategy 2): views/functions/procedures/triggers are
/// re-applied via a native <c>CREATE OR REPLACE</c> AST rewrite, never a
/// regex — these tests pin the extraction and rewrite in isolation, with no
/// database (<see cref="PgDesiredState"/>'s loader classification is covered
/// separately by whatever exercises the desired-state directory scan).
/// </summary>
public class PgProgrammablesTests
{
    [Fact]
    public void A_view_becomes_idempotent_and_names_itself()
    {
        var analysis = PgProgrammables.Analyze(new[]
        {
            new ProgrammableFile("v.sql", """CREATE VIEW "ActiveUsers" AS SELECT "Id" FROM "Users" WHERE "Active";"""),
        });

        Assert.Empty(analysis.Messages);
        var obj = Assert.Single(analysis.Objects);
        Assert.Equal("ActiveUsers", obj.ObjectName);
        Assert.Equal("View", obj.ObjectType);
        Assert.Contains("CREATE OR REPLACE VIEW", obj.ApplyScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Users", obj.DependsOnTables ?? []);
    }

    [Fact]
    public void A_view_already_written_as_or_replace_stays_idempotent()
    {
        var analysis = PgProgrammables.Analyze(new[]
        {
            new ProgrammableFile("v.sql", """CREATE OR REPLACE VIEW "V" AS SELECT 1;"""),
        });

        var obj = Assert.Single(analysis.Objects);
        Assert.Contains("CREATE OR REPLACE VIEW", obj.ApplyScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_view_does_not_depend_on_itself()
    {
        var analysis = PgProgrammables.Analyze(new[]
        {
            new ProgrammableFile("v.sql", """CREATE VIEW "V" AS SELECT 1;"""),
        });

        var obj = Assert.Single(analysis.Objects);
        Assert.DoesNotContain("V", obj.DependsOnTables ?? []);
        Assert.DoesNotContain("V", obj.DependsOn);
    }

    [Fact]
    public void A_view_selecting_from_another_declared_view_depends_on_it_not_a_table()
    {
        var analysis = PgProgrammables.Analyze(new[]
        {
            new ProgrammableFile("base.sql", """CREATE VIEW "Base" AS SELECT "Id" FROM "T";"""),
            new ProgrammableFile("derived.sql", """CREATE VIEW "Derived" AS SELECT "Id" FROM "Base";"""),
        });

        Assert.Empty(analysis.Messages);
        var derived = analysis.Objects.Single(o => o.ObjectName == "Derived");
        Assert.Contains("Base", derived.DependsOn);
        Assert.DoesNotContain("Base", derived.DependsOnTables ?? []);
    }

    [Fact]
    public void A_function_is_named_by_its_bare_last_part_and_becomes_idempotent()
    {
        var analysis = PgProgrammables.Analyze(new[]
        {
            new ProgrammableFile("f.sql", """
                CREATE FUNCTION "app"."make_slug"(input text) RETURNS text
                LANGUAGE sql AS $$ SELECT lower(input) $$;
                """),
        });

        var obj = Assert.Single(analysis.Objects);
        Assert.Equal("make_slug", obj.ObjectName);
        Assert.Equal("ScalarFunction", obj.ObjectType);
        Assert.Contains("CREATE OR REPLACE FUNCTION", obj.ApplyScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_procedure_is_classified_distinctly_from_a_function()
    {
        var analysis = PgProgrammables.Analyze(new[]
        {
            new ProgrammableFile("p.sql", """
                CREATE PROCEDURE "do_thing"() LANGUAGE sql AS $$ SELECT 1 $$;
                """),
        });

        var obj = Assert.Single(analysis.Objects);
        Assert.Equal("Procedure", obj.ObjectType);
        Assert.Contains("CREATE OR REPLACE PROCEDURE", obj.ApplyScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_trigger_is_named_by_table_and_trigger_and_depends_on_its_table()
    {
        var analysis = PgProgrammables.Analyze(new[]
        {
            new ProgrammableFile("t.sql", """
                CREATE TRIGGER "touch_updated_at" BEFORE UPDATE ON "Users"
                FOR EACH ROW EXECUTE FUNCTION "set_updated_at"();
                """),
        });

        var obj = Assert.Single(analysis.Objects);
        Assert.Equal("Users.touch_updated_at", obj.ObjectName);
        Assert.Equal("DmlTrigger", obj.ObjectType);
        Assert.Contains("Users", obj.DependsOnTables ?? []);
        Assert.Contains("CREATE OR REPLACE TRIGGER", obj.ApplyScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_files_declaring_the_same_object_name_is_an_error()
    {
        var analysis = PgProgrammables.Analyze(new[]
        {
            new ProgrammableFile("a.sql", """CREATE VIEW "V" AS SELECT 1;"""),
            new ProgrammableFile("b.sql", """CREATE VIEW "V" AS SELECT 2;"""),
        });

        Assert.Empty(analysis.Objects);
        var message = Assert.Single(analysis.Messages);
        Assert.Equal("Error", message.Severity);
        Assert.Equal("SCHEMORPH004", message.Code);
    }
}
