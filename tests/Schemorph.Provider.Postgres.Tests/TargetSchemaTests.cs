namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// Pins <see cref="PostgresProvider.TargetSchemaOf"/> against the engine's own
/// search_path rules. The dangerous shape is the server DEFAULT —
/// <c>"$user", public</c> — which, taken literally, matches no schema and turns
/// inspect into a silently empty result (the cycle-76 latent bug).
/// </summary>
public class TargetSchemaTests
{
    private static string Of(string searchPath, string? username = null)
    {
        // Built through the builder so the connection-string layer's own quoting
        // cannot eat the quotes that belong to the search_path value.
        var builder = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Database = "db",
            SearchPath = searchPath,
            Username = username,
        };
        return PostgresProvider.TargetSchemaOf(builder.ConnectionString);
    }

    [Fact]
    public void No_search_path_falls_back_to_public()
    {
        Assert.Equal("public", PostgresProvider.TargetSchemaOf("Host=localhost;Database=db"));
    }

    [Theory]
    [InlineData("$user")]
    [InlineData("\"$user\"")]           // the server default is QUOTED and still special
    [InlineData("\"$user\", public")]
    public void User_token_resolves_to_the_connection_user(string searchPath)
    {
        Assert.Equal("app_owner", Of(searchPath, username: "app_owner"));
    }

    [Fact]
    public void User_token_without_a_username_is_skipped_like_a_missing_schema()
    {
        Assert.Equal("public", Of("\"$user\", public"));
        Assert.Equal("sales", Of("$user, sales"));
    }

    [Fact]
    public void Unquoted_names_fold_to_lower_case_like_the_engine_folds_them()
    {
        Assert.Equal("sales", Of("Sales, public"));
    }

    [Fact]
    public void Quoted_names_are_taken_verbatim()
    {
        Assert.Equal("Sales", Of("\"Sales\", public"));
        Assert.Equal("we\"ird", Of("\"we\"\"ird\""));
    }

    [Fact]
    public void Empty_entries_are_skipped()
    {
        Assert.Equal("sales", Of(" , sales"));
        Assert.Equal("public", Of("\"\", public"));
    }
}
