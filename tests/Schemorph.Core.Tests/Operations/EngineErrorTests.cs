using Schemorph.Core.Errors;
using Schemorph.Core.Operations;
using Schemorph.Core.Providers;

namespace Schemorph.Core.Tests.Operations;

/// <summary>
/// An engine error is described at the boundary where it reaches the user, by the provider
/// that knows the engine — the same way whichever stage raised it. A translated one carries
/// its next step; an untranslated one says so rather than passing for Schemorph's own words
/// or being dressed in a guess.
/// </summary>
public sealed class EngineErrorTests
{
    private const string Conn = "conn";

    private static readonly EngineError Translated = new("E1", "the engine refused", "Do the next thing.");
    private static readonly EngineError Untranslated = new("E2", "the engine refused", Hint: null);

    [Fact]
    public void A_translated_error_reads_message_then_hint()
    {
        Assert.True(Translated.Translated);
        Assert.Equal("the engine refused (Do the next thing.)", Translated.Text);
        Assert.Equal("E1: the engine refused (Do the next thing.)", Translated.Describe());
    }

    [Fact]
    public void An_untranslated_error_is_marked_as_the_engine_speaking()
    {
        Assert.False(Untranslated.Translated);
        Assert.StartsWith("the engine refused (", Untranslated.Text);
        Assert.Contains(EngineError.UntranslatedMarker, Untranslated.Text);
    }

    [Fact]
    public void The_engine_error_is_found_inside_the_exceptions_that_wrap_it()
    {
        var engineException = new FormatException("raw");
        var provider = new FakeProvider
        {
            EngineErrorDescriber = ex => ex is FormatException ? Untranslated : null,
        };
        var wrapped = new InvalidOperationException("stage failed",
            new AggregateException(new ArgumentException("unrelated"), engineException));

        Assert.Equal(Untranslated, EngineErrors.Find(provider, wrapped));
        Assert.Null(EngineErrors.Find(provider, new InvalidOperationException("not the engine")));
    }

    [Fact]
    public void An_unclassified_failure_puts_a_translation_in_the_hint_slot()
    {
        var provider = new FakeProvider { EngineErrorDescriber = _ => Translated };

        var error = SchemorphError.ForUnclassified("compare_failed", new Exception("raw text"), provider);

        Assert.Equal("E1: the engine refused", error.Message);
        Assert.Equal("Do the next thing.", error.Hint);
        Assert.Equal(new EngineErrorInfo("E1", Translated: true), error.Engine);
    }

    [Fact]
    public void An_unclassified_untranslated_failure_keeps_the_hint_empty_and_marks_the_message()
    {
        var provider = new FakeProvider { EngineErrorDescriber = _ => Untranslated };

        var error = SchemorphError.ForUnclassified("compare_failed", new Exception("raw text"), provider);

        Assert.StartsWith("E2: the engine refused", error.Message);
        Assert.Contains(EngineError.UntranslatedMarker, error.Message);
        Assert.Null(error.Hint);
        Assert.Equal(new EngineErrorInfo("E2", Translated: false), error.Engine);
    }

    [Fact]
    public void An_unclassified_failure_that_is_not_the_engines_is_reported_as_before()
    {
        var error = SchemorphError.ForUnclassified("apply_failed", new Exception("raw text"), new FakeProvider());
        var withoutProvider = SchemorphError.ForUnclassified("apply_failed", new Exception("raw text"), provider: null);

        Assert.Equal(SchemorphError.Create("apply_failed", "raw text"), error);
        Assert.Equal(error, withoutProvider);
    }

    [Fact]
    public async Task A_redefine_failure_describes_the_engine_error_and_carries_it_on_the_outcome()
    {
        var ledger = new FakeLedger();
        var provider = new FakeProvider
        {
            Ledger = ledger,
            DesiredState = new FakeDesiredState(),
            Programmables = new ProgrammableAnalysis(
                new[]
                {
                    new ProgrammableObjectInfo("dbo.V", "View", "V.sql", "body",
                        "CREATE OR ALTER VIEW dbo.V -- body", Array.Empty<string>()),
                },
                Array.Empty<RawMessage>()),
            ApplyOutcome = new ApplyResult(true, Array.Empty<RawChange>(), Array.Empty<RawChange>(), Array.Empty<RawMessage>()),
            FailOnScriptContaining = "dbo.V",
            EngineErrorDescriber = ex => ex is InvalidOperationException ? Untranslated : null,
        };

        var outcome = await ApplyOperation.RunAsync(provider, ledger, new ApplyOperation.Request("schema", Conn));

        Assert.Equal(ApplyOperation.FailureStage.Redefine, outcome.Stage);
        Assert.Equal(new EngineErrorInfo("E2", Translated: false), outcome.Engine);
        var text = Assert.Single(outcome.Errors).Text;
        Assert.Contains("Re-defining dbo.V failed: E2: the engine refused", text);
        Assert.Contains(EngineError.UntranslatedMarker, text);
    }

    [Fact]
    public async Task A_publish_failure_carries_the_engine_error_and_hides_the_tools_own_bookkeeping()
    {
        var ledger = new FakeLedger();
        var provider = new FakeProvider
        {
            Ledger = ledger,
            DesiredState = new FakeDesiredState(),
            Programmables = new ProgrammableAnalysis(Array.Empty<ProgrammableObjectInfo>(), Array.Empty<RawMessage>()),
            ApplyOutcome = new ApplyResult(false, Array.Empty<RawChange>(), Array.Empty<RawChange>(), new[]
            {
                // The engine announcing the history table as a would-be drop — the gate
                // excluded it, so it is not the user's to read.
                new RawMessage("Warning", "W1", "The table [dbo].[__SchemorphHistory] is being dropped."),
                new RawMessage("Error", "E1", Translated.Text),
            })
            {
                Engine = Translated,
            },
        };

        var outcome = await ApplyOperation.RunAsync(provider, ledger, new ApplyOperation.Request("schema", Conn));

        Assert.Equal(ApplyOperation.FailureStage.Publish, outcome.Stage);
        Assert.Equal(new EngineErrorInfo("E1", Translated: true), outcome.Engine);
        var visible = Assert.Single(outcome.Errors);
        Assert.Equal("E1", visible.Code);
    }
}
