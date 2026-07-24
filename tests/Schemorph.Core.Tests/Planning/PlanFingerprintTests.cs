using Schemorph.Core.Planning;
using Schemorph.Core.Providers;

namespace Schemorph.Core.Tests.Planning;

/// <summary>
/// The apply gate rests on the fingerprint: <c>apply --expect-plan H</c> runs the
/// reviewed plan or refuses. These pin the property that makes that promise true —
/// the hash binds <em>what executes</em>, not just an object-level summary of it.
/// The driving defect (VibeBase PG P1 acceptance #2): two plans that alter the same
/// objects with the same operation and risk but different DDL used to share a hash.
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
        var shape = new PlanAction("vibebase_control.Members", "Table", PlanOperation.Alter, RiskLevel.Warning);

        var ckOnly = PlanWith(
            "ALTER TABLE vibebase_control.\"Members\" DROP CONSTRAINT ck_member_kind, " +
            "ADD CONSTRAINT ck_member_kind CHECK ((\"Kind\")::text = ANY (ARRAY['admin','builder']));",
            shape);
        var withColumns = PlanWith(
            "ALTER TABLE vibebase_control.\"Members\" ADD COLUMN \"Slug\" varchar(64), " +
            "ADD CONSTRAINT uq_member_slug UNIQUE (\"Slug\"), " +
            "DROP CONSTRAINT ck_member_kind, ADD CONSTRAINT ck_member_kind CHECK (true);",
            shape);

        Assert.NotEqual(PlanFingerprint.Compute(ckOnly), PlanFingerprint.Compute(withColumns));
    }

    // Postgres P1 leaves per-change sql null, so the executed script is the ONLY
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
