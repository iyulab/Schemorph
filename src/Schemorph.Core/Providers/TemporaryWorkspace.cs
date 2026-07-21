namespace Schemorph.Core.Providers;

/// <summary>
/// Scratch space a provider needs for its own intermediate artifacts (a dacpac,
/// a staged model file) — things the user never asked for and should never have
/// to reason about.
///
/// This exists because leaking them is not cosmetic. A consumer hit an `inspect`
/// failure naming <c>…\schemorph-inspect-&lt;guid&gt;.dacpac</c>, could not connect
/// that path to any argument they had passed, reasonably concluded <c>--out</c>
/// was at fault, and wrote a workaround into a production runbook. The path was
/// an internal artifact under <see cref="Path.GetTempPath"/>, which the tool used
/// without ever checking it was usable.
///
/// So: the directory is the tool's responsibility. It is created before use, a
/// failure to create it is reported as what it is
/// (<see cref="TemporaryWorkspaceException"/>, naming the directory and how to
/// change it), and cleanup can never speak over the failure it is cleaning up
/// after.
/// </summary>
public static class TemporaryWorkspace
{
    /// <summary>Everything Schemorph writes lives under one named directory, so it is recognizable and sweepable.</summary>
    public const string DirectoryName = "schemorph";

    /// <summary>
    /// A path for a new intermediate file, in a directory that exists by the time
    /// this returns.
    /// </summary>
    /// <param name="purpose">Short tag for the artifact, e.g. "inspect".</param>
    /// <param name="root">
    /// Where the workspace lives. Defaults to <see cref="Path.GetTempPath"/>, which is
    /// what every caller uses; it is a parameter so a test can exercise an unusable
    /// location without reassigning TMP, which is process-global and would reach into
    /// whatever else is running at that moment.
    /// </param>
    public static string NewFile(string purpose, string extension, string? root = null)
    {
        var directory = Path.Combine(root ?? Path.GetTempPath(), DirectoryName);
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Name the directory and the lever that moves it. The old failure named
            // a file the user had never heard of and offered no lever at all.
            throw new TemporaryWorkspaceException(directory, ex);
        }

        return Path.Combine(directory, $"{purpose}-{Guid.NewGuid():N}{extension}");
    }

    /// <summary>
    /// Best-effort cleanup. Deliberately swallows: this runs in <c>finally</c>
    /// blocks, and <see cref="File.Delete"/> throws when the directory is gone —
    /// which is exactly the case where something has already failed. A throwing
    /// cleanup would replace the real exception with its own, so the user would
    /// be told about the tidying rather than about the failure.
    /// </summary>
    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // A leftover file in the temp directory is not worth losing a diagnosis over.
        }
    }
}

/// <summary>
/// Schemorph could not create the directory it keeps its own intermediate files
/// in. The fix is environmental (point TMP/TEMP somewhere writable), not a retry
/// and not a change of arguments — so the message says which directory and which
/// variable, and never mentions the artifact that was about to be written there.
/// </summary>
public sealed class TemporaryWorkspaceException(string directory, Exception inner)
    : Exception(
        $"Schemorph could not create its temporary workspace at '{directory}'. " +
        "It keeps intermediate files there; the location comes from the TMP/TEMP environment " +
        "variables. Point them at a directory that exists and is writable.", inner)
{
    /// <summary>The directory that could not be created.</summary>
    public string Directory { get; } = directory;
}
