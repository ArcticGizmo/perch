namespace Perch.Data.Roost;

/// <summary>How a pane is drawn: a full thread (every placed pane), or a mini card — the header and the last few
/// activity lines, which is also what an expanded pane shows while its thread loads.</summary>
public enum RoostPaneSize
{
    Collapsed = 0,
    Expanded = 1,
}
