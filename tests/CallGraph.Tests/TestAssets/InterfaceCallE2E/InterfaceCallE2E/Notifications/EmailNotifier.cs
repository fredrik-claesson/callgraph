using InterfaceCallE2E.Contracts.Notifications;

namespace InterfaceCallE2E.Infrastructure.Notifications;

public class EmailNotifier : INotifier
{
    public void Notify(string message)
    {
        PrivateLog(message);
        ProtectedLog(message);
        InternalLog(message);
    }

    private void PrivateLog(string message)
    {
    }

    protected void ProtectedLog(string message)
    {
    }

    internal void InternalLog(string message)
    {
    }
}
