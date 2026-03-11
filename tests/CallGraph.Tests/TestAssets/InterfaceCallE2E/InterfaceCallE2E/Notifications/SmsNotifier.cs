using InterfaceCallE2E.Contracts.Notifications;

namespace InterfaceCallE2E.Infrastructure.Notifications;

public class SmsNotifier : INotifier
{
    public void Notify(string message)
    {
        ProtectedLog(message);
        InternalLog(message);
        PrivateLog(message);
    }

    protected void ProtectedLog(string message)
    {
    }

    internal void InternalLog(string message)
    {
    }

    private void PrivateLog(string message)
    {
    }
}
