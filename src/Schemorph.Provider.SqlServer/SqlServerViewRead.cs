namespace Schemorph.Provider.SqlServer;

/// <summary>
/// The batch that reads a just re-defined view once — every column of every row — so a value the definition cannot
/// produce fails inside the re-definition's transaction (<see cref="SqlServerProvider.RedefineAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// <c>COUNT(*)</c> would not do: the engine computes only the columns a query uses, and a cast nobody reads never
/// overflows. Each column goes through <c>DATALENGTH</c> — it takes a value of any type, and has to compute the value to
/// measure it — and the lengths into one <c>CHECKSUM</c> per row, aggregated so every row is read. The column list
/// comes from the catalog after the definition ran, so it is the view as now defined.
/// </para>
/// <para>
/// The engine's own error comes through unchanged (<c>Msg 8115</c> for an overflowing conversion, <c>Msg 8134</c> for
/// a division by zero); the re-definition's failure already names the view.
/// </para>
/// </remarks>
internal static class SqlServerViewRead
{
    public static string Script(string objectName)
    {
        var quoted = string.Join(".", objectName.Split('.').Select(part => "[" + part.Replace("]", "]]") + "]"));
        var literal = quoted.Replace("'", "''");
        return $"""
            DECLARE @schemorph_view int = OBJECT_ID(N'{literal}');
            DECLARE @schemorph_read nvarchar(max) = N'SELECT @checksum = MAX(CHECKSUM(' + (
                SELECT STRING_AGG(CONVERT(nvarchar(max), N'DATALENGTH(' + QUOTENAME(name) + N')'), N', ') WITHIN GROUP (ORDER BY column_id)
                FROM sys.columns WHERE object_id = @schemorph_view)
                + N')) FROM ' + QUOTENAME(OBJECT_SCHEMA_NAME(@schemorph_view)) + N'.' + QUOTENAME(OBJECT_NAME(@schemorph_view));
            DECLARE @schemorph_checksum int;
            EXEC sys.sp_executesql @schemorph_read, N'@checksum int OUTPUT', @checksum = @schemorph_checksum OUTPUT;
            """;
    }
}
