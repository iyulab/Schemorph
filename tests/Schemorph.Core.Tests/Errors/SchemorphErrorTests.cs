using Schemorph.Core.Errors;

namespace Schemorph.Core.Tests.Errors;

public sealed class SchemorphErrorTests
{
    // The kind is the STABLE contract (agents branch on it); codes are specific.
    // Every code the CLI can emit must map to a documented kind — never "internal".
    [Theory]
    [InlineData("invalid_arguments", "usage")]
    [InlineData("schema_dir_not_found", "usage")]
    [InlineData("migrations_dir_not_found", "usage")]
    [InlineData("not_implemented", "unsupported")]
    [InlineData("invalid_desired_state", "invalid_state")]
    [InlineData("migration_failed", "invalid_state")]
    [InlineData("redefine_failed", "invalid_state")]
    [InlineData("compare_failed", "execution")]
    [InlineData("apply_failed", "execution")]
    [InlineData("inspect_failed", "execution")]
    public void Known_codes_map_to_documented_kinds(string code, string expectedKind)
    {
        Assert.Equal(expectedKind, SchemorphError.KindOf(code));
    }

    [Fact]
    public void Unknown_codes_fall_back_to_internal()
    {
        Assert.Equal("internal", SchemorphError.KindOf("something_new"));
    }

    [Fact]
    public void Create_fills_kind_from_the_code()
    {
        var error = SchemorphError.Create("apply_failed", "boom", "check the connection");

        Assert.Equal(new SchemorphError("execution", "apply_failed", "boom", "check the connection"), error);
    }

    [Fact]
    public void Exit_codes_follow_the_terraform_convention()
    {
        Assert.Equal(0, (int)ExitCode.Success);
        Assert.Equal(1, (int)ExitCode.Error);
        Assert.Equal(2, (int)ExitCode.ChangesPending);
    }
}
