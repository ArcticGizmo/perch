namespace Perch.Plugins;

using System.IO.Compression;
using System.Text;

/// <summary>
/// Installs a plugin from a GitHub release: resolve the release, verify the payload zip against the
/// release's <c>SHA256SUMS.txt</c> (refuse on mismatch — the bytes on disk aren't the bytes released),
/// extract it safely, validate its manifest, and place it under the plugins directory keyed by its id.
///
/// The verify/extract/validate half (<see cref="InstallVerifiedZip"/>) is separated from the network half
/// so it can be unit-tested with an in-memory zip. Extraction is zip-slip-guarded: an entry that would
/// escape the target directory aborts the install rather than writing outside it. Consent and enablement
/// are the caller's (UI's) job — a fresh install comes back <see cref="InstalledPluginRecord.Enabled"/>
/// false with no grants, carrying the requested capabilities for a consent prompt.
/// </summary>
internal sealed class PluginInstaller
{
    private readonly IPluginDownloader _downloader;
    private readonly string _pluginsDir;
    private readonly string? _hostVersion;

    public PluginInstaller(IPluginDownloader downloader, string pluginsDir, string? hostVersion = null)
    {
        _downloader = downloader;
        _pluginsDir = pluginsDir;
        _hostVersion = hostVersion;
    }

    public async Task<PluginInstallResult> InstallAsync(PluginInstallSource source, CancellationToken ct = default)
    {
        string releaseJson;
        try { releaseJson = await _downloader.GetTextAsync(source.ReleaseApiUrl, ct); }
        catch (Exception ex) { return PluginInstallResult.Fail($"could not reach GitHub for {source.Slug}: {ex.Message}"); }

        var release = GitHubReleaseParser.Parse(releaseJson);
        if (release is null) return PluginInstallResult.Fail($"could not read the release metadata for {source.Slug}.");

        var zip = release.FindPayloadZip();
        if (zip is null)
            return PluginInstallResult.Fail($"release {release.TagName} of {source.Slug} does not have exactly one .zip payload asset.");

        var sumsAsset = release.FindAsset(GitHubReleaseParser.ChecksumsAssetName);
        if (sumsAsset is null)
            return PluginInstallResult.Fail($"release {release.TagName} of {source.Slug} has no {GitHubReleaseParser.ChecksumsAssetName}, so the download can't be verified.");

        Sha256Sums sums;
        byte[] zipBytes;
        try
        {
            var sumsBytes = await _downloader.GetBytesAsync(sumsAsset.DownloadUrl, ct);
            sums = Sha256Sums.Parse(Encoding.UTF8.GetString(sumsBytes));
            zipBytes = await _downloader.GetBytesAsync(zip.DownloadUrl, ct);
        }
        catch (Exception ex) { return PluginInstallResult.Fail($"download failed: {ex.Message}"); }

        var want = sums.Expected(zip.Name);
        if (want is null)
            return PluginInstallResult.Fail($"{GitHubReleaseParser.ChecksumsAssetName} has no entry for {zip.Name}, so it can't be verified.");

        return InstallVerifiedZip(source.Slug, release.TagName, zipBytes, want, zip.Name);
    }

    /// <summary>Removes an installed plugin's directory (<c>&lt;pluginsDir&gt;/&lt;id&gt;</c>), best-effort.
    /// Returns true if a directory was there and is now gone. The caller also drops the persisted record.</summary>
    public bool Uninstall(string id)
    {
        var dir = Path.Combine(_pluginsDir, id);
        try
        {
            if (!Directory.Exists(dir)) return false;
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Verifies the hash, extracts safely, validates the manifest, and places the plugin. Public
    /// for tests (no network). <paramref name="expectedHash"/> is lower-case hex.</summary>
    public PluginInstallResult InstallVerifiedZip(string slug, string tag, byte[] zipBytes, string expectedHash, string zipName)
    {
        var got = Sha256Sums.Hash(zipBytes);
        if (!string.Equals(got, expectedHash, StringComparison.OrdinalIgnoreCase))
            return PluginInstallResult.Fail(
                $"checksum mismatch for {zipName} — refusing to install.\n  expected {expectedHash}\n  actual   {got}");

        var staging = Path.Combine(Path.GetTempPath(), "perch-plugin-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);

            try { ExtractSafely(zipBytes, staging); }
            catch (PluginSecurityException ex) { return PluginInstallResult.Fail(ex.Message); }
            catch (Exception ex) { return PluginInstallResult.Fail($"could not extract {zipName}: {ex.Message}"); }

            var root = LocateManifestRoot(staging);
            if (root is null)
                return PluginInstallResult.Fail($"{zipName} does not contain a {PluginRegistry.ManifestFileName}.");

            var parse = PluginManifestParser.Parse(
                File.ReadAllText(Path.Combine(root, PluginRegistry.ManifestFileName)), _hostVersion);
            if (!parse.Ok)
                return PluginInstallResult.Fail("invalid manifest:\n  - " + string.Join("\n  - ", parse.Errors));

            var manifest = parse.Manifest!;
            var installDir = Path.Combine(_pluginsDir, manifest.Id);

            Directory.CreateDirectory(_pluginsDir);
            if (Directory.Exists(installDir)) Directory.Delete(installDir, recursive: true); // update = replace
            MoveDir(root, installDir);

            var record = new InstalledPluginRecord
            {
                Id = manifest.Id,
                Source = slug,
                Tag = tag,
                Version = manifest.Version,
                AssetSha256 = got,
                Enabled = false,                 // pending consent
                GrantedCapabilities = [],
                GrantedNetwork = [],
            };
            return PluginInstallResult.Success(record, manifest, installDir);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    // Extracts every entry, refusing any whose resolved path escapes the destination (zip-slip). Directory
    // entries just create the directory; file entries are written after ensuring their parent exists.
    private static void ExtractSafely(byte[] zipBytes, string destDir)
    {
        var destFull = Path.GetFullPath(destDir + Path.DirectorySeparatorChar);
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
            if (!target.StartsWith(destFull, StringComparison.Ordinal))
                throw new PluginSecurityException($"archive entry '{entry.FullName}' escapes the target directory (zip-slip) — refusing to install.");

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    // Directory.Move fails across volumes (staging is in TempPath; the plugins dir may be elsewhere), so
    // fall back to a recursive copy. The caller cleans the staging tree either way.
    private static void MoveDir(string source, string dest)
    {
        try { Directory.Move(source, dest); return; }
        catch (IOException) { /* likely cross-volume — copy instead */ }

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest));
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, dest), overwrite: true);
    }

    // The plugin root is where perch-plugin.json lives: the staging root itself, or a single top-level
    // sub-directory (GitHub's "zip of a folder" shape). Anything else is unresolvable.
    private static string? LocateManifestRoot(string staging)
    {
        if (File.Exists(Path.Combine(staging, PluginRegistry.ManifestFileName))) return staging;

        var subdirs = Directory.GetDirectories(staging);
        if (subdirs.Length == 1 && File.Exists(Path.Combine(subdirs[0], PluginRegistry.ManifestFileName)))
            return subdirs[0];

        return null;
    }
}

/// <summary>Thrown internally when an archive tries something dangerous (zip-slip); turned into a clean
/// install failure by the installer.</summary>
internal sealed class PluginSecurityException(string message) : Exception(message);

/// <summary>The outcome of an install attempt. On success it carries the (not-yet-consented) record, the
/// parsed manifest (whose capabilities the consent UI shows), and the install directory.</summary>
internal sealed record PluginInstallResult(
    bool Ok,
    string? Error,
    InstalledPluginRecord? Record,
    PluginManifest? Manifest,
    string? InstallDir)
{
    public static PluginInstallResult Fail(string error) => new(false, error, null, null, null);

    public static PluginInstallResult Success(InstalledPluginRecord record, PluginManifest manifest, string dir) =>
        new(true, null, record, manifest, dir);
}
