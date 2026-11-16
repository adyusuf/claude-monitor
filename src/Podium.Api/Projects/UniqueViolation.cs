using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Podium.Api.Projects;

/// <summary>Tells a database unique-key violation (a second identical insert that arrived at the same moment) from any other save failure.</summary>
internal static class UniqueViolation
{
    public static bool Is(DbUpdateException e) => e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>The same, on the named unique index only (so the violation of another index of the table still fails as it did).</summary>
    public static bool Is(DbUpdateException e, string index) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } p && p.ConstraintName == index;
}
