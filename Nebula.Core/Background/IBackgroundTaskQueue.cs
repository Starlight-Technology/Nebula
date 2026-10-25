using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Nebula.Core.Background;

public interface IBackgroundTaskQueue
{
    void QueueBackgroundWorkItem(Func<CancellationToken, Task> workItem);
    Task<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken);
}
