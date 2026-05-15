using System.Threading;

namespace FastClip.Services;

internal sealed class OperationCoordinator
{
    private int _isRunning;

    public bool TryBegin() => Interlocked.CompareExchange(ref _isRunning, 1, 0) == 0;

    public void End() => Interlocked.Exchange(ref _isRunning, 0);
}
