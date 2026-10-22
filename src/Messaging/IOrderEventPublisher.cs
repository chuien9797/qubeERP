using Shared.Contracts.Events;

namespace QubeERP.Messaging;

public interface IOrderEventPublisher
{
    Task PublishAsync(OrderIntegrationEvent message, CancellationToken cancellationToken);
}
