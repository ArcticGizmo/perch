namespace Perch.Data.Control;

/// <summary>An image to send as a base64 content block: its media type (e.g. <c>image/png</c>) and the
/// base64-encoded bytes. Built from a <see cref="MessageAttachment"/> at send time.</summary>
internal readonly record struct ImageContent(string MediaType, string Base64);

/// <summary>What an attachment on a user message is — a plain file (its path goes to Claude as text) or an
/// image (also sent as a base64 image content block, and shown as a thumbnail).</summary>
internal enum AttachmentKind { File, Image }

/// <summary>A file or image the user attached to a message via drag-drop or paste. <see cref="Path"/> is an
/// absolute path on disk — the real file for a dropped file, or a temp PNG we wrote for a pasted image — so
/// the thread can open it and preview it. <see cref="MediaType"/> is set for images (e.g. <c>image/png</c>),
/// used to build the outgoing content block.</summary>
internal sealed class MessageAttachment
{
    public required AttachmentKind Kind { get; init; }
    public required string Path { get; init; }
    public string? MediaType { get; init; }

    /// <summary>The <c>[Image #N]</c> placeholder this image left in the composer text (see <see
    /// cref="ImageMarker"/>), or null for a plain file / an attachment from before markers existed.</summary>
    public string? Marker { get; init; }

    /// <summary>True for an image pasted from the clipboard, whose temp file name says nothing to the user.</summary>
    public bool Pasted { get; init; }

    public string DisplayName => System.IO.Path.GetFileName(Path);

    /// <summary>What the chip shows: the marker for a pasted image, the marker and file name for a dropped or picked
    /// one (so it can be matched to its place in the text), else just the file name.</summary>
    public string ChipLabel => Marker is null ? DisplayName : Pasted ? Marker : $"{Marker} {DisplayName}";
}
