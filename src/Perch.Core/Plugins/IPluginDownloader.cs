namespace Perch.Plugins;

/// <summary>
/// The network seam for installing from GitHub, kept thin so the installer's verify/extract/validate
/// orchestration is unit-testable against an in-memory fake. The real implementation
/// (<see cref="HttpPluginDownloader"/>) wraps <c>HttpClient</c>.
/// </summary>
internal interface IPluginDownloader
{
    /// <summary>Fetches the release-metadata JSON from a GitHub API URL (throws on transport failure).</summary>
    Task<string> GetTextAsync(string url, CancellationToken ct);

    /// <summary>Downloads an asset's bytes by its download URL (throws on transport failure).</summary>
    Task<byte[]> GetBytesAsync(string url, CancellationToken ct);
}
