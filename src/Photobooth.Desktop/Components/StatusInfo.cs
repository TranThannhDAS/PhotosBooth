namespace Photobooth.Desktop.Components;

public enum StatusKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Nội dung một thông báo trạng thái hiển thị cho khách.</summary>
public sealed record StatusInfo(StatusKind Kind, string Title, string? Detail = null);
