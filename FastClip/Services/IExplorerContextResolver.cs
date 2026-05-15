using FastClip.Models;

namespace FastClip.Services;

internal interface IExplorerContextResolver
{
    Task<ExplorerContext> GetContextAsync(CancellationToken cancellationToken);
}
