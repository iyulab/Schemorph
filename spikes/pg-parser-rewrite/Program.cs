// Spike: can a libpg_query binding be the parser behind the shadow harness's
// schema rewriting? (P1, ADR-0007 "not adopted now" re-examined with evidence.)
//
// Measures, on the killer shapes from cycle-76 and the engine spike:
//   1. Parse fidelity on quoted PascalCase DDL (multi-statement).
//   2. Whether identifier AST nodes carry usable byte offsets (offset-guided
//      surgical rewrite — keeps user text verbatim except the retargeted names).
//   3. Deparse round-trip: does synthesis preserve quoting? (The exact failure
//      that eliminated psqldef.)
//
// Throwaway code; the deliverable is the measurement.

using PgSqlParser;

const string Ddl = """
    CREATE TABLE "SrcTest"."Workspaces" (
        "Id" uuid NOT NULL DEFAULT gen_random_uuid(),
        "Tier" text NOT NULL,
        CONSTRAINT "PK_Workspaces" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_Tier" CHECK ("Tier" IN ('free', 'pro'))
    );
    CREATE INDEX "IX_Lower" ON "SrcTest"."Workspaces" USING btree (lower("Tier"));
    ALTER TABLE "SrcTest"."Workspaces" ADD COLUMN "Note" text;
    """;

Console.WriteLine("=== 1. Parse ===");
var parsed = Parser.Parse(Ddl);
if (parsed.Error is not null)
{
    Console.WriteLine($"PARSE FAILED: {parsed.Error}");
    return 1;
}
Console.WriteLine($"Statements: {parsed.Value?.Stmts?.Count}");

Console.WriteLine();
Console.WriteLine("=== 2. AST shape (first statement, truncated) ===");
var json = parsed.Value?.ToString() ?? "";
Console.WriteLine(json.Length > 3000 ? json[..3000] + " …[truncated]" : json);

Console.WriteLine();
Console.WriteLine("=== 3. Deparse round-trip ===");
var deparsed = Parser.Deparse(parsed.Value!);
if (deparsed.Error is not null)
{
    Console.WriteLine($"DEPARSE FAILED: {deparsed.Error}");
    return 1;
}
Console.WriteLine(deparsed.Value);
Console.WriteLine();
Console.WriteLine($"quoting preserved (\"Tier\"):   {deparsed.Value!.Contains("\"Tier\"")}");
Console.WriteLine($"quoting preserved (\"SrcTest\"): {deparsed.Value!.Contains("\"SrcTest\"")}");
Console.WriteLine($"lower() index survives:          {deparsed.Value!.Contains("lower", StringComparison.OrdinalIgnoreCase)}");

Console.WriteLine();
Console.WriteLine("=== 4. Schema-bearing nodes beyond the table itself ===");
// FK REFERENCES target, qualified enum/composite type, qualified function in a
// default — every place a schema name can hide. The rewriter must reach all of
// them, so the AST must expose all of them as structured fields.
const string Cross = """
    CREATE TABLE "SrcTest"."Members" (
        "Id" uuid NOT NULL,
        "Role" "SrcTest"."MemberRole" NOT NULL,
        "WorkspaceId" uuid NOT NULL REFERENCES "SrcTest"."Workspaces" ("Id") ON DELETE CASCADE,
        "Slug" text DEFAULT "SrcTest".make_slug()
    );
    """;
var cross = Parser.Parse(Cross);
if (cross.Error is not null)
{
    Console.WriteLine($"PARSE FAILED: {cross.Error}");
    return 1;
}
var crossJson = cross.Value!.ToString()!;
Console.WriteLine($"FK pktable schemaname present:   {crossJson.Contains("\"pktable\": { \"schemaname\": \"SrcTest\"")}");
Console.WriteLine($"type name carries schema:        {crossJson.Contains("{ \"String\": { \"sval\": \"SrcTest\" } }, { \"String\": { \"sval\": \"MemberRole\" } }")}");
Console.WriteLine($"function name carries schema:    {crossJson.Contains("\"funcname\": [ { \"String\": { \"sval\": \"SrcTest\" } }")}");

var crossDeparsed = Parser.Deparse(cross.Value!);
Console.WriteLine($"cross deparse ok:                {crossDeparsed.Error is null}");
Console.WriteLine(crossDeparsed.Value);

Console.WriteLine();
Console.WriteLine("=== 5. Error reporting on broken SQL ===");
var broken = Parser.Parse("CREATE TABLE \"X\" (\"a\" int,,);");
Console.WriteLine(broken.Error?.ToString() ?? "no error reported (!)");

Console.WriteLine();
Console.WriteLine("=== 6. AST mutation: generic protobuf walk + deparse ===");
// The rewriter's core move: descend every message field, retarget schema names,
// deparse. Generic descent (IMessage descriptors) is what makes coverage
// independent of statement-type knowledge.
var ast = Parser.Parse(Cross).Value!;
Console.WriteLine($"AST CLR type: {ast.GetType().FullName}");
var rewritten = 0;
void Walk(Google.Protobuf.IMessage message)
{
    foreach (var field in message.Descriptor.Fields.InDeclarationOrder())
    {
        var value = field.Accessor.GetValue(message);
        if (field.FieldType == Google.Protobuf.Reflection.FieldType.Message)
        {
            if (field.IsRepeated)
            {
                foreach (var item in ((System.Collections.IEnumerable)value).OfType<Google.Protobuf.IMessage>()) Walk(item);
            }
            else if (value is Google.Protobuf.IMessage child)
            {
                Walk(child);
            }
        }
        else if (field.FieldType == Google.Protobuf.Reflection.FieldType.String
                 && field.Name is "schemaname"
                 && (string)value == "SrcTest")
        {
            field.Accessor.SetValue(message, "shadow_x");
            rewritten++;
        }
    }
}
Walk(ast);
Console.WriteLine($"schemaname fields rewritten: {rewritten}");
var mutated = Parser.Deparse(ast);
Console.WriteLine(mutated.Value);
Console.WriteLine($"no SrcTest RangeVar left:  {!mutated.Value!.Contains("\"SrcTest\".\"Members\"")}");
Console.WriteLine($"shadow target present:     {mutated.Value!.Contains("\"shadow_x\".\"Members\"")}");
Console.WriteLine($"NOTE type/func lists are String nodes, not schemaname fields: " +
    $"{mutated.Value!.Contains("\"SrcTest\".\"MemberRole\"")}");

return 0;
