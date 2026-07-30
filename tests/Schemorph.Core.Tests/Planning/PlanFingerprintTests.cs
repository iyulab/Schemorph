using Schemorph.Core.Planning;
using Schemorph.Core.Providers;

namespace Schemorph.Core.Tests.Planning;

/// <summary>
/// The apply gate rests on the fingerprint: <c>apply --expect-plan H</c> runs the
/// reviewed plan or refuses. These pin the property that makes that promise true —
/// the hash binds <em>what executes</em>, not just an object-level summary of it.
/// The driving defect, reported from a production apply: two plans that alter the
/// same objects with the same operation and risk but different DDL shared a hash.
/// </summary>
public class PlanFingerprintTests
{
    private static Plan PlanWith(string? updateScript, params PlanAction[] actions) =>
        new(Plan.CurrentFormatVersion, actions, Array.Empty<PlanMessage>(), ApplyAtomicity.Partial, updateScript);

    // The exact defect: identical action tuples, different executed script. A
    // reviewer signs the ck-only plan's hash; the gate must NOT then pass a plan
    // that also adds columns and a UNIQUE.
    [Fact]
    public void Same_action_shape_but_different_executed_script_yields_different_hash()
    {
        var shape = new PlanAction("sample_app.Members", "Table", PlanOperation.Alter, RiskLevel.Warning);

        var ckOnly = PlanWith(
            "ALTER TABLE sample_app.\"Members\" DROP CONSTRAINT ck_member_kind, " +
            "ADD CONSTRAINT ck_member_kind CHECK ((\"Kind\")::text = ANY (ARRAY['admin','builder']));",
            shape);
        var withColumns = PlanWith(
            "ALTER TABLE sample_app.\"Members\" ADD COLUMN \"Slug\" varchar(64), " +
            "ADD CONSTRAINT uq_member_slug UNIQUE (\"Slug\"), " +
            "DROP CONSTRAINT ck_member_kind, ADD CONSTRAINT ck_member_kind CHECK (true);",
            shape);

        Assert.NotEqual(PlanFingerprint.Compute(ckOnly), PlanFingerprint.Compute(withColumns));
    }

    // A provider that leaves per-change sql null makes the executed script the ONLY
    // discriminator — the fix must not lean on PlanAction.Sql being populated.
    [Fact]
    public void Different_scripts_are_distinguished_even_when_per_change_sql_is_null()
    {
        var shape = new PlanAction("s.T", "Table", PlanOperation.Alter, RiskLevel.Warning, Sql: null);

        var addA = PlanWith("ALTER TABLE s.\"T\" ADD COLUMN a int;", shape);
        var addB = PlanWith("ALTER TABLE s.\"T\" ADD COLUMN b int;", shape);

        Assert.NotEqual(PlanFingerprint.Compute(addA), PlanFingerprint.Compute(addB));
    }

    // A redefine's exact body is its identity: two CREATE OR REPLACE of the same
    // object with different definitions are different plans.
    [Fact]
    public void Redefine_actions_with_different_bodies_yield_different_hash()
    {
        static PlanAction Redefine(string sql) =>
            new("s.V", "View", PlanOperation.Redefine, RiskLevel.Safe, Sql: sql);

        var one = PlanWith(updateScript: null, Redefine("CREATE OR REPLACE VIEW s.V AS SELECT 1;"));
        var two = PlanWith(updateScript: null, Redefine("CREATE OR REPLACE VIEW s.V AS SELECT 2;"));

        Assert.NotEqual(PlanFingerprint.Compute(one), PlanFingerprint.Compute(two));
    }

    // Stability: the gate recomputes the hash at apply time from the same
    // comparison the diff showed, so identical inputs must hash identically.
    [Fact]
    public void Identical_plans_hash_identically()
    {
        var one = PlanWith("ALTER TABLE s.T ADD COLUMN a int;",
            new PlanAction("s.T", "Table", PlanOperation.Alter, RiskLevel.Warning));
        var two = PlanWith("ALTER TABLE s.T ADD COLUMN a int;",
            new PlanAction("s.T", "Table", PlanOperation.Alter, RiskLevel.Warning));

        Assert.Equal(PlanFingerprint.Compute(one), PlanFingerprint.Compute(two));
    }

    // Messages, atomicity and explanation describe a plan; they are not what it
    // executes, and must stay out of its identity (a hash reviewed under one set
    // of diagnostics still gates the apply).
    [Fact]
    public void Messages_atomicity_and_explanation_do_not_change_the_hash()
    {
        var action = new PlanAction("s.T", "Table", PlanOperation.Alter, RiskLevel.Warning, Sql: "x", Explanation: "one");
        const string script = "ALTER TABLE s.T ADD COLUMN a int;";

        var bare = new Plan(Plan.CurrentFormatVersion, new[] { action },
            Array.Empty<PlanMessage>(), ApplyAtomicity.Partial, script);
        var decorated = new Plan(Plan.CurrentFormatVersion,
            new[] { action with { Explanation = "a completely different explanation" } },
            new[] { new PlanMessage("Warning", "SCHEMORPH101", "heads up") },
            ApplyAtomicity.Transactional, script);

        Assert.Equal(PlanFingerprint.Compute(bare), PlanFingerprint.Compute(decorated));
    }

    // The hash is built by concatenating two inputs, so where one ends and the
    // next begins has to be decidable from the string itself. Both inputs are
    // SQL text: a slice ending in "X" beside a script "Y" concatenates to
    // exactly what a slice "XY" beside no script does. Two materially different
    // plans, one hash — the gate would pass a plan nobody reviewed.
    [Fact]
    public void The_boundary_between_the_plan_shape_and_the_executed_script_cannot_be_forged()
    {
        var sliceThenScript = PlanWith("Y",
            new PlanAction("s.T", "Table", PlanOperation.Alter, RiskLevel.Warning, Sql: "X"));
        var sliceCarryingBoth = PlanWith(updateScript: null,
            new PlanAction("s.T", "Table", PlanOperation.Alter, RiskLevel.Warning, Sql: "XY"));

        Assert.NotEqual(
            PlanFingerprint.Compute(sliceThenScript),
            PlanFingerprint.Compute(sliceCarryingBoth));
    }

    // Same argument one level down: an action's members are concatenated too,
    // and a quoted identifier or a fragment of SQL can contain whatever
    // character separates them. Where a name ends and a type begins must come
    // from the encoding, not from the content.
    [Fact]
    public void Field_boundaries_inside_an_action_cannot_be_forged_by_content()
    {
        const string script = "ALTER TABLE s.T ADD COLUMN a int;";

        var one = PlanWith(script,
            new PlanAction("s.[a|b]", "Table", PlanOperation.Alter, RiskLevel.Warning));
        var other = PlanWith(script,
            new PlanAction("s.[a", "b]|Table", PlanOperation.Alter, RiskLevel.Warning));

        Assert.NotEqual(PlanFingerprint.Compute(one), PlanFingerprint.Compute(other));
    }

    // The per-change slice is bound on its own account (format 1.5): the same
    // publish script with a different attribution is a different document for
    // the reviewer who reads the slices, so a signed hash must not survive it.
    [Fact]
    public void A_changed_per_change_slice_alone_changes_the_hash()
    {
        const string script = "ALTER TABLE s.T ADD COLUMN a int;";
        var attributed = PlanWith(script,
            new PlanAction("s.T", "Table", PlanOperation.Alter, RiskLevel.Warning,
                Sql: "ALTER TABLE s.T ADD COLUMN a int;"));
        var unattributed = PlanWith(script,
            new PlanAction("s.T", "Table", PlanOperation.Alter, RiskLevel.Warning, Sql: null));

        Assert.NotEqual(PlanFingerprint.Compute(attributed), PlanFingerprint.Compute(unattributed));
    }

    // "in plan order" (docs/plan-format.md): execution order is part of what a
    // reviewer signed, so the same changes in another order are another plan.
    [Fact]
    public void Plan_order_is_part_of_the_identity()
    {
        var table = new PlanAction("s.T", "Table", PlanOperation.Alter, RiskLevel.Warning);
        var view = new PlanAction("s.V", "View", PlanOperation.Redefine, RiskLevel.Safe,
            Sql: "CREATE OR ALTER VIEW s.V AS SELECT 1;");

        Assert.NotEqual(
            PlanFingerprint.Compute(PlanWith("ALTER TABLE s.T ADD COLUMN a int;", table, view)),
            PlanFingerprint.Compute(PlanWith("ALTER TABLE s.T ADD COLUMN a int;", view, table)));
    }

    // End-to-end: the discriminator arrives from the provider's
    // CompareResult.UpdateScript, threaded through PlanBuilder onto the plan.
    [Fact]
    public void PlanBuilder_carries_the_update_script_into_the_fingerprint()
    {
        var changes = new[] { new RawChange("Change", "Table", "s.T") };

        var addA = PlanBuilder.Build(
            new CompareResult(changes, Array.Empty<RawMessage>(), "ALTER TABLE s.T ADD COLUMN a int;"),
            allowDestructive: false);
        var addB = PlanBuilder.Build(
            new CompareResult(changes, Array.Empty<RawMessage>(), "ALTER TABLE s.T ADD COLUMN b int;"),
            allowDestructive: false);

        Assert.NotEqual(PlanFingerprint.Compute(addA), PlanFingerprint.Compute(addB));
    }
}
