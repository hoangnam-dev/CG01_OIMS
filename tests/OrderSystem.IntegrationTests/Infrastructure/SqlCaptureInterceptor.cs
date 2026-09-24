using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace OrderSystem.IntegrationTests.Infrastructure;

internal sealed class SqlCaptureInterceptor : DbCommandInterceptor
{
    private readonly List<CapturedSqlCommand> commands = [];

    public IReadOnlyList<CapturedSqlCommand> Commands => commands;

    public void Clear() => commands.Clear();

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        commands.Add(new(command.CommandText, command.Parameters.Cast<DbParameter>().Select(parameter => parameter.ParameterName).ToArray()));
        return ValueTask.FromResult(result);
    }
}

internal sealed record CapturedSqlCommand(string CommandText, IReadOnlyList<string> ParameterNames);
