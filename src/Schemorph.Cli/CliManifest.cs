using System.Text.Json;
using Schemorph.Core.Planning;
using Schemorph.Core.Providers;

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
    // declaration) · 1.5: provider.vocabulary — the full capability line set,
    // so a consumer can compute "not supported" for this provider without
    // hand-maintaining another provider's declared list. 1.6: desiredStateFormatVersion
    // — versions the `inspect`/`schemorph://schema` file-tree rendering contract
    // (docs/desired-state-format.md), independent of planFormatVersion. 1.7: diff
    // --migrations — the pending migrations an apply runs join the plan it is gated on.
    // 1.8: --pre on diff, apply and status (pre-stage scripts), and exit code 2 on apply —
    // only the pre-stage ran, the rest is planned on the next run.
    // All additive; consumers ignore properties they do not know.
    public const string ManifestVersion = "1.8";

    /// <summary>
    /// Every verb with its options and exit codes — the manifest's <c>verbs</c>, and the one list
    /// <see cref="UnknownOption"/> checks a command line against, so an option is known to the parser
    /// exactly when the manifest advertises it.
    /// </summary>
    private static readonly object[] Verbs = new object[]
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
                new { flag = "--migrations", value = "dir", required = false, description = "Versioned migration scripts; the pending ones join the plan (name + checksum) and its planHash. Pass the same directory to apply." },
                new { flag = "--pre", value = "dir", required = false, description = "Pre-stage scripts (V####__description.sql), run once before the declarative stage; the plan is computed as of after the pending ones, which join it (pre[]) and its planHash. Pass the same directory to apply." },
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
                new { flag = "--pre", value = "dir", required = false, description = "Pre-stage scripts, run first and recorded once each; where the provider cannot plan past them, only they run and the exit code is 2." },
                new { flag = "--allow-destructive", value = (string?)null, required = false, description = "Apply destructive changes too." },
                new { flag = "--expect-plan", value = "hash", required = false, description = "Apply only if the computed plan matches this fingerprint (diff's planHash); a mismatch aborts with error code plan_mismatch before anything executes." },
                new { flag = "--format", value = "json|text", required = false, description = "Output form; the json envelope embeds the executed plan." },
            },
            exitCodes = new[] { 0, 1, 2 },
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
                new { flag = "--pre", value = "dir", required = false, description = "Pre-stage scripts: drift is planned as of after the pending ones, which are listed." },
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
    };

    /// <summary>
    /// The first option on <paramref name="args"/> that <paramref name="verb"/> does not take, or null.
    /// A flag the manifest lists with a value consumes the argument after it. A verb the manifest does
    /// not list is not checked here — the dispatcher answers that one with usage.
    /// </summary>
    /// <remarks>
    /// An option the tool does not know used to be ignored: a misspelled <c>--migration</c>, or a flag a
    /// newer release added, ran the command as if it had not been given — a plan without the scripts
    /// the caller meant to include, with nothing saying so.
    /// </remarks>
    public static (string Unknown, IReadOnlyList<string> Known)? UnknownOption(string verb, string[] args)
    {
        var spec = JsonSerializer.SerializeToElement(Verbs).EnumerateArray()
            .FirstOrDefault(v => v.GetProperty("name").GetString() == verb);
        if (spec.ValueKind == JsonValueKind.Undefined) return null;

        var options = spec.GetProperty("options").EnumerateArray()
            .ToDictionary(o => o.GetProperty("flag").GetString()!, o => o.GetProperty("value").ValueKind == JsonValueKind.String);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith('-')) continue;
            if (!options.TryGetValue(args[i], out var takesValue)) return (args[i], options.Keys.ToList());
            if (takesValue) i++;
        }
        return null;
    }

    public static string ToJson(string toolVersion) => JsonSerializer.Serialize(new
    {
        manifestVersion = ManifestVersion,
        name = "schemorph",
        version = toolVersion,
        description = "Declarative, SQL-first schema management for humans and AI agents.",
        planFormatVersion = Plan.CurrentFormatVersion,
        desiredStateFormatVersion = DesiredStateFormat.CurrentVersion,
        provider = ProviderBlock(),
        environment = new[]
        {
            new
            {
                name = "SCHEMORPH_URL",
                description = "Connection string; preferred over --url (keeps credentials out of shell history).",
            },
            new
            {
                name = "SCHEMORPH_PROVIDER",
                description = "Database provider: sqlserver (default) or postgres. The provider block below reflects the selection.",
            },
        },
        outputConventions = new
        {
            formats = new[] { "text", "json", "sql" },
            defaultFormat = "text on a terminal; json when stdout is redirected",
            sqlFormat = "diff only: the whole plan as one review document in execution order, " +
                "planHash in the header. Read-only — apply it with --expect-plan, not with a SQL client",
            errorEnvelope = "errors are one JSON object {error:{kind,code,message,hint}} on stderr with --format json; " +
                "a failed apply adds stage and committed{declarative,redefines,migrations,pre}. " +
                "Optional fields are absent, not null; hint is omitted when no cause was established (docs/errors.md)",
            redaction = "passwords are redacted from every output channel",
        },
        exitCodes = new[]
        {
            new { code = 0, meaning = "success; for diff: no changes pending" },
            new { code = 1, meaning = "error (typed envelope on stderr)" },
            new { code = 2, meaning = "work is pending: diff — anything an apply would run (a change, a pending migration or pre-stage script); status — drift or scripts waiting to run; apply — only the pre-stage ran (the provider plans the rest once it is done), run it again" },
        },
        verbs = Verbs,
        docs = new
        {
            planFormat = "docs/plan-format.md",
            desiredStateFormat = "docs/desired-state-format.md",
            errors = "docs/errors.md",
            failureSemantics = "docs/failure-semantics.md",
            repository = "https://github.com/iyulab/Schemorph",
        },
    }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// The provider's declared surface, sourced from the declaration itself
    /// (the canonical layer — dev plan §2) so the manifest cannot drift from
    /// what the provider actually claims and refuses. <c>vocabulary</c> is the
    /// full capability line set (since 1.5) — without it, a capability absent
    /// from <c>capabilities</c> reads as "not supported", and a consumer had no
    /// way to tell that from "not a capability this tool models at all" short of
    /// hand-maintaining another provider's declared list to diff against.
    /// </summary>
    private static object ProviderBlock()
    {
        var provider = ProviderSelection.Current.Provider;
        return new
        {
            name = provider.Name,
            capabilities = provider.Capabilities.Declared,
            vocabulary = CapabilityVocabulary.All,
            atomicity = provider.Capabilities.Atomicity?.ToString().ToLowerInvariant(),
        };
    }
}
