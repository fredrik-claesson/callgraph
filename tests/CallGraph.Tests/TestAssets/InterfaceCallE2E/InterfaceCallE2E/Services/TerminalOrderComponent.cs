namespace InterfaceCallE2E.Application.Services;

public interface ITerminalOrderComponent
{
    void CreateTerminalProductSubscriptionOnTerminalOrder(string orderId);
}

public sealed class TerminalOrderComponent : ITerminalOrderComponent
{
    public void CreateTerminalProductSubscriptionOnTerminalOrder(string orderId)
    {
    }
}
