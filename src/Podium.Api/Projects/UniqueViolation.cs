using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Podium.Api.Projects;

/// <summary>Tells a database unique-key violation (a second identical insert that arrived at the same moment) from any other save failure.</summary>
internal static class UniqueViolation
{
    public static bool Is(DbUpdateException e) => e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
