using Microsoft.SqlServer.Dac;
using Schemorph.Provider.SqlServer;

namespace Schemorph.Provider.SqlServer.Tests;

/// <summary>
/// The comparison policy — a database-free pin on the options every compare and
/// apply run through, so the safe defaults cannot regress silently even where the
/// integration suite is env-gated off.
/// </summary>
public class CompareOptionsTests
{
    private static DacDeployOptions Configured()
    {
        var options = new DacDeployOptions();
        SqlServerProvider.ConfigureCompareOptions(options);
        return options;
    }

    [Fact]
    public void Reports_the_full_delta_but_never_blocks_or_leaves_a_partial_publish()
    {
        var options = Configured();

        Assert.False(options.BlockOnPossibleDataLoss);
        Assert.True(options.DropObjectsNotInSource);
        Assert.True(options.IncludeTransactionalScripts);
    }

    // Column order is not meaningful state: honoring it makes an additive column
    // rebuild the whole table (a code-gen's logical order != the live ordinal).
    [Fact]
    public void Column_order_is_ignored_so_additive_columns_stay_in_place()
    {
        Assert.True(Configured().IgnoreColumnOrder);
    }

    // ADR-0006: dropping a login/user/role because it is absent from the desired
    // state is the silent destruction §4 forbids — DacFx reported DROP USER as a
    // non-destructive change. Principals are out of the declarative model.
    [Theory]
    [InlineData(ObjectType.Users)]
    [InlineData(ObjectType.Logins)]
    [InlineData(ObjectType.LinkedServerLogins)]
    [InlineData(ObjectType.DatabaseRoles)]
    [InlineData(ObjectType.ApplicationRoles)]
    [InlineData(ObjectType.ServerRoles)]
    [InlineData(ObjectType.RoleMembership)]
    [InlineData(ObjectType.ServerRoleMembership)]
    [InlineData(ObjectType.Permissions)]
    public void Security_principals_are_excluded_from_the_declarative_diff(ObjectType excluded)
    {
        Assert.Contains(excluded, Configured().ExcludeObjectTypes);
    }

    [Fact]
    public void Configuring_is_idempotent_and_keeps_DacFx_default_exclusions()
    {
        var options = new DacDeployOptions();
        var defaults = options.ExcludeObjectTypes ?? Array.Empty<ObjectType>();

        SqlServerProvider.ConfigureCompareOptions(options);
        SqlServerProvider.ConfigureCompareOptions(options);   // second pass must not duplicate

        var excluded = options.ExcludeObjectTypes;
        Assert.NotNull(excluded);
        Assert.Equal(excluded.Length, excluded.Distinct().Count());
        Assert.All(defaults, d => Assert.Contains(d, excluded));
    }
}
