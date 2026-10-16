using RabbitMQ.Client;

namespace OrderSystem.Infrastructure.Messaging;

internal sealed class RabbitMqConnectionProvider : IRabbitMqConnectionProvider, IDisposable
{
    private readonly IConnectionFactory _connectionFactory;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private IConnection? _connection;
    private int _disposeSignaled;

    public RabbitMqConnectionProvider(IConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
    }

    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeSignaled) != 0, this);

        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeSignaled) != 0, this);

            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            _connection?.Dispose();
            _connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);

            return _connection;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeSignaled, 1) != 0)
        {
            return;
        }

        _connectionGate.Wait();
        try
        {
            _connection?.Dispose();
            _connection = null;
        }
        finally
        {
            _connectionGate.Release();
            _connectionGate.Dispose();
        }
    }
}
