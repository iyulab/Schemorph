namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// The lookup table in isolation — no live server needed. <see cref="PgSqlStateHintApplyTests"/>
/// covers one entry end-to-end through a real <c>PostgresException</c>; this covers the rest of
/// the curated allowlist and the "unrecognized code" default.
/// </summary>
public class PgSqlStateHintsTests
{
    [Theory]
    [InlineData("23502")] // not_null_violation
    [InlineData("23503")] // foreign_key_violation
    [InlineData("23505")] // unique_violation
    [InlineData("23514")] // check_violation
    [InlineData("23P01")] // exclusion_violation
    [InlineData("25006")] // read_only_sql_transaction
    [InlineData("28000")] // invalid_authorization_specification
    [InlineData("28P01")] // invalid_password
    [InlineData("3D000")] // invalid_catalog_name
    [InlineData("40001")] // serialization_failure
    [InlineData("40P01")] // deadlock_detected
    [InlineData("42501")] // insufficient_privilege
    [InlineData("42703")] // undefined_column
    [InlineData("42P01")] // undefined_table
    [InlineData("42710")] // duplicate_object
    [InlineData("42P07")] // duplicate_table
    [InlineData("53300")] // too_many_connections
    [InlineData("55006")] // object_in_use
    [InlineData("57014")] // query_canceled
    public void Every_curated_code_has_a_non_empty_hint(string sqlState)
    {
        var hint = PgSqlStateHints.TryGet(sqlState);

        Assert.NotNull(hint);
        Assert.NotEmpty(hint);
    }

    [Theory]
    [InlineData("XX000")] // internal_error — deliberately not in the allowlist
    [InlineData("08006")] // connection_failure — deliberately not in the allowlist
    [InlineData("")]
    public void An_unrecognized_code_gets_no_guessed_hint(string sqlState)
    {
        Assert.Null(PgSqlStateHints.TryGet(sqlState));
    }
}
