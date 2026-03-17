using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

    public async Task RunWithLambdaSelfCallAsync()
    {
        await ExecuteStateAsync(
            state => ProcessStateUpdateAsync(state),
            () => Task.CompletedTask);
    }

    public IEnumerable<int> BuildChargebackValues(IEnumerable<int> values)
    {
        var unprocessedValues = GetUnprocessedValues(values);
        foreach (var value in unprocessedValues)
        {
            yield return GetInvertedValue(value);
        }
    }

    public TimeSpan? ResolveTimeout()
    {
        return ShouldUseTimeout() ? TimeSpan.FromMinutes(5) : null;
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

    private static async Task ExecuteStateAsync(Func<int, Task> onSuccess, Func<Task> onFallback)
    {
        await onSuccess(1);
        await onFallback();
    }

    private Task ProcessStateUpdateAsync(int state)
    {
        _helper.Help();
        return Task.CompletedTask;
    }

    private static IEnumerable<int> GetUnprocessedValues(IEnumerable<int> values)
    {
        foreach (var value in values.Where(v => v > 0))
        {
            yield return value;
        }
    }

    private static int GetInvertedValue(int value)
    {
        return -value;
    }

    private bool ShouldUseTimeout()
    {
        return true;
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
