namespace OrderSystem.Application.Common.Diagnostics;

public interface IOperationHook
{
    Task ReachAsync(string checkpoint, CancellationToken cancellationToken);
}
