namespace OrderService.Messaging;

public sealed class OutboxSettings
{
    public int PollIntervalSeconds { get; set; } = 2;
    public int BatchSize { get; set; } = 25;
    public int MaxBackoffSeconds { get; set; } = 60;
}
