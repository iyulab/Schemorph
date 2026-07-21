using Schemorph.Core.Providers;

namespace Schemorph.Core.Tests.Providers;

/// <summary>
/// The tool's own scratch space. These pin the two behaviours that turned an
/// ordinary environment problem into a wrong entry in a production runbook: the
/// error must name the directory (and the lever that moves it) rather than an
/// internal artifact, and cleanup must never speak over the failure it is
/// cleaning up after.
///
/// Note what these tests do NOT do: reassign TMP. An earlier version did, and it
/// broke an unrelated class that reads <see cref="Path.GetTempPath"/> in a field
/// initializer — environment variables are process-global and xUnit runs classes
/// in parallel, so the mutation reached into whatever happened to start at that
/// moment. The root is a parameter for exactly this reason.
/// </summary>
public sealed class TemporaryWorkspaceTests
{
    [Fact]
    public void The_workspace_directory_is_created_before_it_is_used()
    {
        // Several levels deep and non-existent — the point is that nothing above
        // it has to be there either.
        var root = Path.Combine(Path.GetTempPath(), $"schemorph-ws-{Guid.NewGuid():N}", "a", "b");
        try
        {
            var file = TemporaryWorkspace.NewFile("inspect", ".dacpac", root);

            Assert.True(Directory.Exists(Path.GetDirectoryName(file)));
            Assert.EndsWith(".dacpac", file);
            Assert.Contains(TemporaryWorkspace.DirectoryName, file);
        }
        finally
        {
            var top = Path.Combine(Path.GetTempPath(), Path.GetRelativePath(Path.GetTempPath(), root).Split(Path.DirectorySeparatorChar)[0]);
            if (Directory.Exists(top)) Directory.Delete(top, recursive: true);
        }
    }

    [Fact]
    public void An_unusable_location_is_reported_as_itself_not_as_a_missing_dacpac()
    {
        // Reproduction of the reported failure (an unusable temp location).
        // Before the fix this surfaced as "Could not find a part of the path
        // '…\schemorph-inspect-<guid>.dacpac'" — a file the user never asked for,
        // which is why they blamed --out and worked around the wrong thing.
        //
        // Unusable is produced by pointing at a *file*: creating a directory under
        // it fails on every platform, so this needs no OS branch. (The field report
        // came from an unmapped Windows drive; the class of failure is the same.)
        var blocker = Path.Combine(Path.GetTempPath(), $"schemorph-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "not a directory");
        try
        {
            var ex = Assert.Throws<TemporaryWorkspaceException>(
                () => TemporaryWorkspace.NewFile("inspect", ".dacpac", blocker));

            Assert.Contains(blocker, ex.Message);          // names the location…
            Assert.Contains("TMP", ex.Message);            // …and the lever that moves it
            Assert.DoesNotContain(".dacpac", ex.Message);  // never the internal artifact
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void Cleanup_of_a_file_whose_directory_is_gone_does_not_throw()
    {
        // File.Delete throws DirectoryNotFoundException here, and this runs in
        // finally blocks — so an unguarded delete replaces the real exception with
        // its own. The user would then be told about the tidying, not the failure.
        var vanished = Path.Combine(Path.GetTempPath(), $"gone-{Guid.NewGuid():N}", "x.dacpac");

        TemporaryWorkspace.TryDelete(vanished);   // must not throw
    }

    [Fact]
    public void Cleanup_removes_the_file_when_it_is_there()
    {
        var file = TemporaryWorkspace.NewFile("test", ".tmp");
        File.WriteAllText(file, "x");

        TemporaryWorkspace.TryDelete(file);

        Assert.False(File.Exists(file));
    }
}
