using InterfaceCallE2E.Contracts.Events;

namespace InterfaceCallE2E.Application.Services;

public sealed class AdyenTerminalOrderCreator
{
    private readonly IPaymentProcessingEvents _paymentProcessingEvents;

    public AdyenTerminalOrderCreator(IPaymentProcessingEvents paymentProcessingEvents)
    {
        _paymentProcessingEvents = paymentProcessingEvents;
    }

    public void CreateAdyenTerminalOrderAsync(string orderId)
    {
        _paymentProcessingEvents.EmitMewsTerminalOrderCreated(new MewsTerminalOrderCreated(orderId));
    }
}
