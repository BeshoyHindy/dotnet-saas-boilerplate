using Boilerplate.Modules.Files.Domain;

namespace Boilerplate.Modules.Files.Services;

internal sealed class NoOpFileScanner : IFileScanner
{
    public ValueTask<ScanStatus> ScanAsync(string storageKey, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ScanStatus.Clean);
}
