namespace Schemorph.Core.Planning;

/// <summary>
/// The object classes ADR-0002 routes through idempotent re-definition
/// (strategy 2) instead of the declarative diff. Names are Schemorph terms
/// as reported by providers.
/// </summary>
public static class ProgrammableObjects
{
    public static readonly IReadOnlySet<string> ObjectTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Procedure",
        "View",
        "ScalarFunction",
        "TableValuedFunction",
        "DmlTrigger",
    };

    public static bool IsProgrammable(string objectType) => ObjectTypes.Contains(objectType);
}
