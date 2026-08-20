namespace Schemorph.Provider.Postgres.Tests;

public sealed class PgMigrationLinterConcurrentIndexTests
{
    [Fact]
    public void Concurrent_index_build_is_flagged_non_transactional()
    {
        var signals = PgMigrationLinter.Lint("CREATE INDEX CONCURRENTLY ix_t_c ON t (c);");

        Assert.Contains(Schemorph.Core.Providers.MigrationLintSignal.NonTransactional, signals);
    }

    [Fact]
    public void A_plain_index_build_is_not_flagged()
    {
        var signals = PgMigrationLinter.Lint("CREATE INDEX ix_t_c ON t (c);");

        Assert.DoesNotContain(Schemorph.Core.Providers.MigrationLintSignal.NonTransactional, signals);
    }
}
