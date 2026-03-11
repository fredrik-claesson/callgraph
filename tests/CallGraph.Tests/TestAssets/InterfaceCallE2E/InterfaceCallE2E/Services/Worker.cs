using InterfaceCallE2E.Contracts.Notifications;
using InterfaceCallE2E.Contracts.Services;

namespace InterfaceCallE2E.Application.Services;

public class Worker
{
    private readonly INotifier _notifier;
    private readonly IHelper _helper;

    public Worker(INotifier notifier, IHelper helper)
    {
        _notifier = notifier;
        _helper = helper;
    }

    public void Run()
    {
        _notifier.Notify("hello");
        DirectHelper();
        new Utility().DoWork();
    }

    private void DirectHelper()
    {
        _helper.Help();
        PrivateUtility();
    }

    protected void ProtectedUtility()
    {
    }

    internal void InternalUtility()
    {
    }

    private void PrivateUtility()
    {
    }
}
