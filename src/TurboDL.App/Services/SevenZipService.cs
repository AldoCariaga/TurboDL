using TurboDL.Core.Archives;
using TurboDL.Core.Video;

namespace TurboDL.App.Services;

/// <summary>
/// The app's one 7-Zip, set up on first use in &lt;tools&gt;\7zip. Progress text comes from
/// <see cref="VideoToolsService.Describe"/>, which names the tool ("7-Zip").
/// </summary>
public sealed class SevenZipService(SevenZipTools tools)
{
    public bool IsInstalled => tools.IsInstalled;
    public string SevenZipPath => tools.SevenZipPath;

    /// <summary>Installs 7-Zip if it's missing. Safe to call from two places at once; the second waits for the first.</summary>
    public Task EnsureAsync(IProgress<ToolSetupProgress>? progress, CancellationToken ct) => tools.EnsureAsync(progress, ct);
}
