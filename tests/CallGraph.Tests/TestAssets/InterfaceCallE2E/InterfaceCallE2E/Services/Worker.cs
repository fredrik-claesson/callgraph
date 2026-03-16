using System;
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

    public void RunWithConditional(INotifier? notifier)
    {
        notifier?.Notify("conditional");
    }

    public void RunWithLocalFunction()
    {
        LocalStep();

        void LocalStep()
        {
            _helper.Help();
        }
    }

    public void RunWithDelegate()
    {
        ExecuteCallback(DelegateStep);
    }

    public string ReadHelperBackedValue()
    {
        return HelperBackedValue;
    }

    public void SubscribeAndHandle()
    {
        Changed += OnChanged;
    }

    public string HelperBackedValue
    {
        get
        {
            _helper.Help();
            return "value";
        }
    }

    private event Action Changed
    {
        add
        {
        }
        remove
        {
        }
    }

    private void DirectHelper()
    {
        _helper.Help();
        PrivateUtility();
    }

    private static void ExecuteCallback(Action callback)
    {
        callback();
    }

    private void DelegateStep()
    {
        _helper.Help();
    }

    private void OnChanged()
    {
        _helper.Help();
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
