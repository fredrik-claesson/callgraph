namespace InterfaceCallE2E.Contracts.Events;

public interface IPaymentProcessingEvents
{
    void EmitMewsTerminalOrderCreated(MewsTerminalOrderCreated payload);
}
