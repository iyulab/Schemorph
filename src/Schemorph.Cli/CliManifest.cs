using System.Text.Json;
using Schemorph.Core.Planning;

namespace Schemorph.Cli;

/// <summary>
/// The `schema` verb's payload: a machine-readable description of this CLI —
/// verbs, options, output formats, exit codes, error envelope — so an agent can
/// discover the surface without parsing help text. Hand-maintained next to the
/// help text it mirrors; the acceptance test for a new verb/flag is updating
/// BOTH. Versioned like the plan format (docs/plan-format.md): minor = additive,
/// consumers ignore unknown properties.
/// </summary>
internal static class CliManifest
{
    // 1.1: docs.failureSemantics · 1.2: the apply-only stage/committed envelope
    // fields · 1.3: diff --format sql (the review document) · 1.4: the provider
    // block (capability lines + apply atomicity, sourced from the provider's own
    // declaration). All additive; consumers ignore properties they do not know.
    public const string ManifestVersion = "1.4";

    public static string ToJson(string toolVersion) => JsonSerializer.Serialize(new
    {
        manifestVersion = ManifestVersion,
        name = "schemorph",
        version = toolVersion,
        description = "Declarative, SQL-first schema management for humans and AI agents.",
        planFormatVersion = Plan.CurrentFormatVersion,
        provider = ProviderBlock(),
        environment = new[]
        {
            new
            {
                name = "SCHEMORPH_URL",
                description = "Connection string; preferred over --url (keeps credentials out of shell history).",
            },
        },
        outputConventions = new
        {
            formats = new[] { "text", "json", "sql" },
            defaultFormat = "text on a terminal; json when stdout is redirected",
            sqlFormat = "diff only: the whole plan as one review document in execution order, " +
                "planHash in the header. Read-only — apply it with --expect-plan, not with a SQL client",
            errorEnvelope = "errors are one JSON object {error:{kind,code,message,hint}} on stderr with --format json; " +
                "a failed apply adds stage and committed{declarative,redefines,migrations}. " +
                "Optional fields are absent, not null; hint is omitted when no cause was established (docs/errors.md)",
            redaction = "passwords are redacted from every output channel",
        },
        exitCodes = new[]
        {
            new { code = 0, meaning = "success; for diff: no changes pending" },
            new { code = 1, meaning = "error (typed envelope on stderr)" },
            new { code = 2, meaning = "diff only: changes are pending" },
        },
        verbs = new object[]
        {
            new
            {
                name = "inspect",
                summary = "Read a live database into desired-state SQL files.",
                options = new object[]
                {
                    new { flag = "--url", value = "connection-string", required = false, description = "Source database; SCHEMORPH_URL is used when omitted." },
                    new { flag = "--out", value = "dir", required = true, description = "Output directory for desired-state files." },
                    new { flag = "--format", value = "json|text", required = false, description = "Output form." },
                },
                exitCodes = new[] { 0, 1 },
            },
            new
            {
                name = "diff",
                summary = "Compute the change plan (never applies anything).",
                options = new object[]
                {
                    new { flag = "--url", value = "connection-string", required = false, description = "Target database; SCHEMORPH_URL is used when omitted." },
                    new { flag = "--schema", value = "dir", required = true, description = "Desired-state SQL directory; non-model .sql files (deploy scripts, seed DML) are skipped with a warning." },
                    new { flag = "--allow-destructive", value = (string?)null, required = false, description = "Include destructive changes in the plan." },
                    new { flag = "--format", value = "json|text|sql", required = false, description = "Output form; json is the plan format (docs/plan-format.md); sql is the human-review document (read-only, planHash in its header)." },
                },
                exitCodes = new[] { 0, 1, 2 },
            },
            new
            {
                name = "apply",
                summary = "Execute the change plan and record it in the history ledger.",
                options = new object[]
                {
                    new { flag = "--url", value = "connection-string", required = false, description = "Target database; SCHEMORPH_URL is used when omitted." },
                    new { flag = "--schema", value = "dir", required = true, description = "Desired-state SQL directory." },
                    new { flag = "--migrations", value = "dir", required = false, description = "Versioned migration scripts (V####__description.sql), run once each, tamper-checked." },
                    new { flag = "--allow-destructive", value = (string?)null, required = false, description = "Apply destructive changes too." },
                    new { flag = "--expect-plan", value = "hash", required = false, description = "Apply only if the computed plan matches this fingerprint (diff's planHash); a mismatch aborts with error code plan_mismatch before anything executes." },
                    new { flag = "--format", value = "json|text", required = false, description = "Output form; the json envelope embeds the executed plan." },
                },
                exitCodes = new[] { 0, 1 },
            },
            new
            {
                name = "status",
                summary = "Show drift (the plan a diff would produce right now), a ledger summary, and pending migrations. Read-only.",
                options = new object[]
                {
                    new { flag = "--url", value = "connection-string", required = false, description = "Target database; SCHEMORPH_URL is used when omitted." },
                    new { flag = "--schema", value = "dir", required = true, description = "Desired-state SQL directory." },
                    new { flag = "--migrations", value = "dir", required = false, description = "Also report pending migration scripts." },
                    new { flag = "--format", value = "json|text", required = false, description = "Output form." },
                },
                exitCodes = new[] { 0, 1, 2 },
            },
            new
            {
                name = "schema",
                summary = "Print this manifest.",
                options = Array.Empty<object>(),
                exitCodes = new[] { 0 },
            },
            new
            {
                name = "mcp",
                summary = "Run as an MCP server over stdio. Tools: schemorph_diff, schemorph_inspect, schemorph_status (read-only) and schemorph_apply (gated: requires the reviewed plan's expectedPlanHash). Resources: schemorph://schema, schemorph://schema/{kind}/{name}, schemorph://plan (needs SCHEMORPH_SCHEMA_DIR). SCHEMORPH_URL must be set in the server's environment — credentials never flow through the conversation.",
                options = Array.Empty<object>(),
                exitCodes = new[] { 0 },
            },
            new
            {
                name = "version",
                summary = "Print the tool version.",
                options = Array.Empty<object>(),
                exitCodes = new[] { 0 },
            },
        },
        docs = new
        {
            planFormat = "docs/plan-format.md",
            errors = "docs/errors.md",
            failureSemantics = "docs/failure-semantics.md",
            repository = "https://github.com/iyulab/Schemorph",
        },
    }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// The provider's declared surface, sourced from the declaration itself
    /// (the canonical layer — dev plan §2) so the manifest cannot drift from
    /// what the provider actually claims and refuses.
    /// </summary>
    private static object ProviderBlock()
    {
        var provider = ProviderSelection.Current.Provider;
        return new
        {
            name = provider.Name,
            capabilities = provider.Capabilities.Declared,
            atomicity = provider.Capabilities.Atomicity?.ToString().ToLowerInvariant(),
        };
    }
}
