using Schemorph.Core.Providers;

namespace Schemorph.Provider.SqlServer;

/// <summary>
/// The provider's view of one loaded desired state: classified model files with
/// their batches pre-split. Compare and programmable analysis both build a
/// TSqlModel from the same files, so loading (directory walk, file reads,
/// classification parse, batch split) happens once per operation and this
/// handle carries the result across the boundary (IDesiredState).
/// </summary>
internal sealed class SqlServerDesiredState : IDesiredState
{
    internal sealed record ModelFile(string Path, string Text, IReadOnlyList<string> Batches);

    public IReadOnlyList<RawMessage> Warnings { get; }
    public IReadOnlyList<RawMessage> Errors { get; }
    internal IReadOnlyList<ModelFile> ModelFiles { get; }

    private SqlServerDesiredState(
        IReadOnlyList<ModelFile> modelFiles, IReadOnlyList<RawMessage> warnings, IReadOnlyList<RawMessage> errors)
    {
        ModelFiles = modelFiles;
        Warnings = warnings;
        Errors = errors;
    }

    internal static SqlServerDesiredState Load(string desiredStateDirectory)
    {
        var loaded = DesiredStateLoader.Load(desiredStateDirectory);
        var files = loaded.Errors.Count > 0
            ? Array.Empty<ModelFile>()
            : loaded.ModelFiles
                .Select(f => new ModelFile(f.Path, f.Text, SqlBatchSplitter.Split(f.Text).ToList()))
                .ToArray();
        return new SqlServerDesiredState(files, loaded.Warnings, loaded.Errors);
    }

    /// <summary>
    /// The boundary contract made loud: requests built from another provider's
    /// state, or from a state whose load failed, are caller bugs — the core
    /// checks <see cref="IDesiredState.Errors"/> before calling anything.
    /// </summary>
    internal static SqlServerDesiredState From(IDesiredState state)
    {
        if (state is not SqlServerDesiredState sql)
        {
            throw new ArgumentException(
                $"Desired state was loaded by a different provider ({state.GetType().Name}).", nameof(state));
        }
        if (sql.Errors.Count > 0)
        {
            throw new ArgumentException(
                "Desired state has load errors; fail the operation instead of passing it on.", nameof(state));
        }
        return sql;
    }
}
