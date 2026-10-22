using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QubeERP.Messaging;

namespace OrderEventWorker;

public static class WorkerProgram
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddOptions<RabbitMqSettings>().BindConfiguration("RabbitMQ")
            .Validate(value => value.OperationTimeoutSeconds is >= 1 and <= 30 && value.Port is >= 1 and <= 65535,
                "Invalid RabbitMQ timeout or port.").ValidateOnStart();
        builder.Services.AddSingleton<OrderEventProcessor>();
        builder.Services.AddHostedService<OrderEventConsumer>();
        await builder.Build().RunAsync();
    }
}
