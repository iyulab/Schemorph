namespace Schemorph.Provider.SqlServer.Tests;

/// <summary>
/// The lookup table in isolation, and the DacFx relay that feeds it — no live server needed. The
/// live path (a real <c>SqlException</c>, a real publish) is covered by the integration suite.
/// </summary>
public class SqlServerErrorHintsTests
{
    [Theory]
    [InlineData(-2)]    // client command timeout
    [InlineData(207)]   // invalid column name
    [InlineData(208)]   // invalid object name
    [InlineData(229)]   // permission denied on object
    [InlineData(262)]   // permission denied in database
    [InlineData(515)]   // cannot insert NULL
    [InlineData(547)]   // statement conflicted with a FOREIGN KEY / CHECK constraint
    [InlineData(1205)]  // deadlock victim
    [InlineData(1222)]  // lock request timeout
    [InlineData(1505)]  // CREATE UNIQUE INDEX found a duplicate key
    [InlineData(1785)]  // multiple cascade paths
    [InlineData(1913)]  // index or statistics already exists
    [InlineData(2601)]  // duplicate key row in a unique index
    [InlineData(2627)]  // violation of a PRIMARY KEY / UNIQUE constraint
    [InlineData(2628)]  // string or binary data would be truncated (named column)
    [InlineData(2714)]  // there is already an object named …
    [InlineData(3729)]  // cannot drop: referenced by another object
    [InlineData(3906)]  // database is read-only
    [InlineData(3960)]  // snapshot isolation update conflict
    [InlineData(4060)]  // cannot open database
    [InlineData(4922)]  // ALTER COLUMN failed: objects access this column
    [InlineData(5074)]  // object is dependent on column
    [InlineData(8115)]  // arithmetic overflow converting to a numeric type
    [InlineData(8152)]  // string or binary data would be truncated
    [InlineData(18456)] // login failed
    public void Every_curated_number_has_a_non_empty_hint(int number)
    {
        var hint = SqlServerErrorHints.TryGet(number);

        Assert.NotNull(hint);
        Assert.NotEmpty(hint);
    }

    [Theory]
    [InlineData(50000)] // a RAISERROR/THROW from user code — deliberately not curated
    [InlineData(102)]   // syntax error — the message itself is the answer
    [InlineData(0)]
    public void An_unrecognized_number_gets_no_guessed_hint(int number)
    {
        Assert.Null(SqlServerErrorHints.TryGet(number));
    }

    private const string Relay =
        "Core Microsoft SqlClient Data Provider: Msg 1505, Level 16, State 1, Line 1 The CREATE UNIQUE INDEX " +
        "statement terminated because a duplicate key was found for the object name 'dbo.Orders'.";

    [Fact]
    public void A_relayed_server_error_is_coded_and_translated_by_the_servers_number()
    {
        var engine = SqlServerProvider.RelayedServerError("SQL72014", Relay);

        Assert.NotNull(engine);
        Assert.Equal("Msg 1505", engine.Code);
        Assert.True(engine.Translated);
    }

    /// <summary>
    /// The server's own text, without the publish's framing around it — the frame names the data provider and
    /// the severity/state/line of a script the reader never sees.
    /// </summary>
    [Fact]
    public void A_relayed_server_error_keeps_the_servers_text_without_the_relay_frame()
    {
        var engine = SqlServerProvider.RelayedServerError("SQL72014", Relay);

        Assert.Equal(
            "The CREATE UNIQUE INDEX statement terminated because a duplicate key was found for the object name 'dbo.Orders'.",
            engine!.Message);
    }

    [Fact]
    public void A_relay_whose_frame_is_not_recognized_keeps_the_whole_message()
    {
        var engine = SqlServerProvider.RelayedServerError("SQL72014", "Msg 1505, something unexpected");

        Assert.Equal("Msg 1505, something unexpected", engine!.Message);
    }

    [Fact]
    public void A_dacfx_message_that_relays_no_server_error_is_not_an_engine_error()
    {
        Assert.Null(SqlServerProvider.RelayedServerError("SQL72045", "Script execution error."));
    }
}
