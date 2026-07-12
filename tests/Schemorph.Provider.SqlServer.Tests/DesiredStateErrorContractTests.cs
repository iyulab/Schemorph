using Schemorph.Core.Operations;

namespace Schemorph.Provider.SqlServer.Tests;

/// <summary>
/// The unified desired-state error contract: a broken model file fails EVERY
/// verb at the DesiredState stage (error code <c>invalid_desired_state</c>),
/// with the file named — never as a compare/execution failure (docs/errors.md:
/// "same code on every verb"). The load short-circuits before any comparison,
/// so no verb ever touches the database: these tests run with a connection
/// string that could not possibly connect.
/// </summary>
public sealed class DesiredStateErrorContractTests : IDisposable
{
    private const string NoDatabase = "Data Source=(none);Initial Catalog=never;Connect Timeout=1";

    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"schemorph-contract-{Guid.NewGuid():N}")).FullName;
    private readonly SqlServerProvider _provider = new();
    private readonly SqlServerLedgerStore _ledger = new();

    private string BrokenFile => Path.Combine(_dir, "tables", "dbo.Broken.sql");

    public DesiredStateErrorContractTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BrokenFile)!);
        File.WriteAllText(BrokenFile, "CREATE TABEL dbo.Broken (Id INT);");   // deliberate typo
    }

    [Fact]
    public async Task Diff_fails_at_the_desired_state_stage_with_the_file_named()
    {
        var result = await DiffOperation.RunAsync(
            _provider, _ledger, _dir, NoDatabase, allowDestructive: false);

        Assert.False(result.Success);
        Assert.Equal(DiffOperation.FailureStage.DesiredState, result.Stage);
        var error = Assert.Single(result.Errors);
        Assert.Equal("SCHEMORPH007", error.Code);
        Assert.Contains(BrokenFile, error.Text);
    }

    [Fact]
    public async Task Status_fails_at_the_desired_state_stage_like_diff()
    {
        var result = await StatusOperation.RunAsync(
            _provider, _ledger, new StatusOperation.Request(_dir, NoDatabase));

        Assert.False(result.Success);
        Assert.Equal(DiffOperation.FailureStage.DesiredState, result.Stage);
        Assert.Contains(result.Errors, e => e.Code == "SCHEMORPH007");
    }

    [Fact]
    public async Task Apply_fails_at_the_desired_state_stage_before_any_database_work()
    {
        var outcome = await ApplyOperation.RunAsync(
            _provider, _ledger, new ApplyOperation.Request(_dir, NoDatabase));

        Assert.False(outcome.Success);
        Assert.Equal(ApplyOperation.FailureStage.DesiredState, outcome.Stage);
        Assert.Contains(outcome.Errors, e => e.Code == "SCHEMORPH007");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
