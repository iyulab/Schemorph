namespace Schemorph.Provider.Postgres.Tests;

/// <summary>
/// Pins the "render only what differs from the engine's own choice" rule: a
/// plain identity column must stay a clean one-liner, and every non-default
/// sequence option must survive into the rendered DDL — dropping one silently
/// is the same defect class as rendering a generated column as DEFAULT.
/// </summary>
public class IdentityOptionsTests
{
    private static string? Options(
        long start = 1, long increment = 1, long? min = null, long? max = null,
        long cache = 1, bool cycle = false, string type = "integer")
        => CatalogReader.IdentityOptions(
            start, increment,
            min ?? 1, max ?? int.MaxValue, cache, cycle, type);

    [Fact]
    public void All_engine_defaults_render_nothing()
    {
        Assert.Null(Options());
        Assert.Null(Options(max: long.MaxValue, type: "bigint"));
        Assert.Null(Options(max: short.MaxValue, type: "smallint"));
    }

    [Fact]
    public void Each_non_default_option_is_rendered()
    {
        Assert.Equal("INCREMENT BY 5 START WITH 100", Options(start: 100, increment: 5));
        Assert.Equal("MINVALUE 10 START WITH 10", Options(start: 10, min: 10));
        Assert.Equal("MAXVALUE 9999", Options(max: 9999));
        Assert.Equal("CACHE 20", Options(cache: 20));
        Assert.Equal("CYCLE", Options(cycle: true));
    }

    [Fact]
    public void A_descending_identity_defaults_to_the_type_floor()
    {
        // increment < 0 flips the engine defaults: MAXVALUE -1, MINVALUE the
        // type's own floor, START at MAXVALUE.
        Assert.Equal("INCREMENT BY -1",
            Options(start: -1, increment: -1, min: int.MinValue, max: -1));
    }
}
