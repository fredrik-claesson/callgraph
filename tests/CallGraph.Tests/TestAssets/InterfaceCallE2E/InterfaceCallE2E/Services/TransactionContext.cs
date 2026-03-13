namespace InterfaceCallE2E.Application.Services;

public sealed class TransactionContext
{
    public required ITerminalOrderComponent AdyenPaymentTerminals { get; init; }
}
