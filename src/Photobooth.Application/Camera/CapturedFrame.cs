namespace Photobooth.Application.Camera;

/// <summary>Một khung hình đã được camera mã hóa (hiện tại là JPEG) tại thời điểm chụp.</summary>
public sealed record CapturedFrame(byte[] Data, string ContentType);

