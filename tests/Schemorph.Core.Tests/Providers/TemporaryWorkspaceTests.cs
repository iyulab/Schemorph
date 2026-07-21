using Schemorph.Core.Providers;

namespace Schemorph.Core.Tests.Providers;

/// <summary>
/// The tool's own scratch space. These pin the two behaviours that turned an
/// ordinary environment problem into a wrong entry in a production runbook: the
/// error must name the directory (and the lever that moves it) rather than an
/// internal artifact, and cleanup must never speak over the failure it is
/// cleaning up after.
/// </summary>
public sealed class TemporaryWorkspaceTests : IDisposable
{
    private readonly string? _originalTmp = Environment.GetEnvironmentVariable("TMP");
    private readonly string? _originalTemp = Environment.GetEnvironmentVariable("TEMP");

    private static void PointTempAt(string path)
    {
        Environment.SetEnvironmentVariable("TMP", path);
        Environment.SetEnvironmentVariable("TEMP", path);
    }

    [Fact]
    public void The_workspace_directory_is_created_before_it_is_used()
    {
        var root = Path.Combine(Path.GetTempPath(), $"schemorph-ws-{Guid.NewGuid():N}");
        PointTempAt(root);   // exists nowhere yet — several levels deep is the point
        try
        {
            var file = TemporaryWorkspace.NewFile("inspect", ".dacpac");

            Assert.True(Directory.Exists(Path.GetDirectoryName(file)));
            Assert.EndsWith(".dacpac", file);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_unusable_temp_location_is_reported_as_itself_not_as_a_missing_dacpac()
    {
        // Reproduction of the reported failure (an unusable Path.GetTempPath()).
        // Before the fix this surfaced as "Could not find a part of the path
        // '…\schemorph-inspect-<guid>.dacpac'" — a file the user never asked for,
        // which is why they blamed --out and worked around the wrong thing.
        //
        // Temp is made unusable by pointing it at a *file*: creating a directory
        // there fails on every platform, so this needs no OS branch. (The field
        // report came from an unmapped Windows drive; the class of failure is the
        // same and it is the class this guards.)
        var blocker = Path.Combine(Path.GetTempPath(), $"schemorph-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "not a directory");
        PointTempAt(blocker);
        try
        {
            var ex = Assert.Throws<TemporaryWorkspaceException>(
                () => TemporaryWorkspace.NewFile("inspect", ".dacpac"));

            Assert.Contains(blocker, ex.Message);          // names the location…
            Assert.Contains("TMP", ex.Message);            // …and the lever that moves it
            Assert.DoesNotContain(".dacpac", ex.Message);  // never the internal artifact
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMP", _originalTmp);
            Environment.SetEnvironmentVariable("TEMP", _originalTemp);
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

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TMP", _originalTmp);
        Environment.SetEnvironmentVariable("TEMP", _originalTemp);
    }
}
