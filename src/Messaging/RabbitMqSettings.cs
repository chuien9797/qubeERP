using RabbitMQ.Client;

namespace QubeERP.Messaging;

public sealed class RabbitMqSettings
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "qubeerp";
    public string Password { get; set; } = "";
    public string VirtualHost { get; set; } = "/";
    public int OperationTimeoutSeconds { get; set; } = 5;

    public ConnectionFactory CreateFactory() => new()
    {
        HostName = HostName,
        Port = Port,
        UserName = UserName,
        Password = Password,
        VirtualHost = VirtualHost,
        AutomaticRecoveryEnabled = false,
        RequestedConnectionTimeout = TimeSpan.FromSeconds(OperationTimeoutSeconds),
        ContinuationTimeout = TimeSpan.FromSeconds(OperationTimeoutSeconds),
        ConsumerDispatchConcurrency = 1
    };
}
