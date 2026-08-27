using Perch.Data;

namespace Perch.Platform.Mac;

/// <summary>
/// macOS implementation of <see cref="IIdeHostDetector"/> — a stub for now (reports "no IDE"), so the
/// overlay simply omits the IDE glyph on the mac head. A real implementation would walk process ancestry
/// via <c>proc_listpids</c>/<c>sysctl(KERN_PROC)</c> and feed each ancestor's executable through the shared
/// <see cref="IdeHost.FromExecutable"/> table (adding the <c>.app</c> bundle names macOS reports). See
/// docs/macos-port-plan.md.
/// </summary>
public sealed class IdeHostDetector : IIdeHostDetector
{
    public IdeHost? Detect(int pid) => null;
}
