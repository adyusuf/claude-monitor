using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Podium.Api.Tests.Infrastructure;

/// <summary>A command interceptor for a second host: a command whose text has these parts fails with this error, or cancels a token.</summary>
public sealed class CommandFault(params string[] parts) : DbCommandInterceptor
{
    /// <summary>The error a matching command fails with; null: it does not fail.</summary>
    public Exception? Error { get; set; }

    /// <summary>A token source cancelled when a matching command is about to run; null: nothing is cancelled.</summary>
    public CancellationTokenSource? Cancel { get; set; }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Hit(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Hit(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void Hit(DbCommand command)
    {
        if (!parts.All(p => command.CommandText.Contains(p, StringComparison.Ordinal))) return;
        Cancel?.Cancel();
        if (Error is { } error) throw error;
    }
}
