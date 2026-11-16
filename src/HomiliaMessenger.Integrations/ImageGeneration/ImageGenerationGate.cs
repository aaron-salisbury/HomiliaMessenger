using System;
using System.Threading;
using System.Threading.Tasks;

namespace HomiliaMessenger.Integrations.ImageGeneration;

/// <summary>
/// Lets one image be generated at a time, and keeps an image model's files from being removed while it draws.
/// </summary>
internal sealed class ImageGenerationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Enters at once if nothing else holds the gate; returns <see langword="false"/> otherwise.
    /// </summary>
    internal bool TryEnter()
    {
        return _semaphore.Wait(0);
    }

    internal void Exit()
    {
        _semaphore.Release();
    }

    /// <summary>
    /// Waits for any generation to finish, then runs <paramref name="action"/> before another can start.
    /// </summary>
    internal async Task RunExclusiveAsync(Action action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await Task.Run(action, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
