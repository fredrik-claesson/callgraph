using InterfaceCallE2E.Contracts.Events;

namespace InterfaceCallE2E.Application.Services;

public sealed class MewsTerminalOrderCreatedEnterpriseProductSubscriptionCreationHandler
{
    public void HandleInternalAsync(MewsTerminalOrderCreated payload, TransactionContext context)
    {
        context.AdyenPaymentTerminals.CreateTerminalProductSubscriptionOnTerminalOrder(payload.OrderId);
    }
}
