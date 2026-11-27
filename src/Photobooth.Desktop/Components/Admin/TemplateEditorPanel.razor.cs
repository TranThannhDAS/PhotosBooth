using System.Globalization;
using System.IO;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Photobooth.Application.Composition;
using Photobooth.Application.Sessions;
using Photobooth.Application.Templates;
using Photobooth.Application.Templates.Editing;
using Photobooth.Desktop.Platform;
using Photobooth.Infrastructure.Templates;

namespace Photobooth.Desktop.Components.Admin;

/// <summary>
/// Visual Template Editor. Component chỉ giữ trạng thái hiển thị (zoom, lựa chọn, hộp thoại);
/// mọi thay đổi template đi qua <see cref="TemplateEditor"/> (Application) và được lưu bằng <see cref="TemplateLibrary"/>.
/// Canvas HTML chỉ để thao tác; "Xem thử" render bằng Photo Composer.
/// </summary>
public sealed partial class TemplateEditorPanel : IAsyncDisposable
{
    private const double MinZoom = 0.05;
    private const double MaxZoom = 4;
    private const int ViewportPadding = 48;
    private const int GridSize = 10;
    private static readonly double[] ZoomSteps = [0.1, 0.25, 0.33, 0.5, 0.75, 1, 1.5, 2, 3, 4];

    private readonly List<string> _addedAssets = [];
    private ElementReference _root;
    private ElementReference _viewport;
    private DotNetObjectReference<TemplateEditorPanel>? _selfReference;
    private int? _jsHandle;
    private TemplateEditor? _editor;
    private string? _assetDirectory;
    private StatusInfo? _loadError;
    private StatusInfo? _status;
    private IReadOnlyList<TemplateValidationIssue> _statusIssues = [];
    private double? _zoom;
    private double _viewportWidth;
    private double _viewportHeight;
    private bool _snap;
    private bool _busy;
    private bool _confirmClose;
    private bool _preview;
    private TemplateSampleContent _previewContent = TemplateSampleContent.SamplePhotos;
    private PhotoTemplate? _previewTemplate;
    private int _previewRevision;
    private IReadOnlyList<string> _fonts = [];
    private bool _disposed;

    [Inject]
    private TemplateLibrary Library { get; set; } = default!;

    [Inject]
    private IPhotoComposer Composer { get; set; } = default!;

    [Inject]
    private PhotoSessionService Sessions { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private ILogger<TemplateEditorPanel> Logger { get; set; } = default!;

    /// <summary>Khóa thư mục của template trong thư viện.</summary>
    [Parameter, EditorRequired]
    public string TemplateKey { get; set; } = string.Empty;

    [Parameter, EditorRequired]
    public EventCallback OnClose { get; set; }

    private TemplateDraft Draft => _editor!.Current;

    private TemplateCanvas Canvas => Draft.Canvas;

    /// <summary>CSS pixel hiển thị cho mỗi pixel canvas (zoom cố định hoặc vừa khung).</summary>
    private double Scale => _zoom ?? FitScale;

    private double FitScale
    {
        get
        {
            if (_viewportWidth <= 0 || _viewportHeight <= 0)
            {
                return 0.25;
            }

            var scale = Math.Min((_viewportWidth - ViewportPadding) / Canvas.WidthPixels, (_viewportHeight - ViewportPadding) / Canvas.HeightPixels);
            return Math.Clamp(scale, MinZoom, MaxZoom);
        }
    }

    private TemplateValidationResult Validation => _editor!.Validate();

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var entry = await Library.FindAsync(TemplateKey);
            if (entry?.Template is not { } template)
            {
                _loadError = new StatusInfo(StatusKind.Error, "Không mở được template.", "Template không tồn tại hoặc đang bị lỗi.");
                return;
            }

            _assetDirectory = template.AssetDirectory;
            _editor = new TemplateEditor(TemplateDraft.FromTemplate(template));
            _editor.Changed += OnEditorChanged;
            _fonts = Library.GetInstalledFonts();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TemplateLibraryException)
        {
            Logger.LogWarning(ex, "Không mở được template trong Editor");
            _loadError = new StatusInfo(StatusKind.Error, "Không mở được template.", "Kiểm tra thư mục thư viện template rồi thử lại.");
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_editor is not null && _jsHandle is null && !_disposed)
        {
            _selfReference ??= DotNetObjectReference.Create(this);
            _jsHandle = await JS.InvokeAsync<int>("photoboothTemplateEditor.attach", _root, _viewport, _selfReference);
            await _root.FocusAsync();
        }
    }

    private void OnEditorChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            StateHasChanged();
        }
    }

    // ================================================================ JS → .NET

    [JSInvokable]
    public Task OnViewportResized(double width, double height) => InvokeAsync(() =>
    {
        _viewportWidth = width;
        _viewportHeight = height;
        StateHasChanged();
    });

    [JSInvokable]
    public Task OnCanvasPointerDown(string? key, string? handle) => InvokeAsync(() =>
    {
        if (_editor is null)
        {
            return;
        }

        if (key is null || !Guid.TryParse(key, out var elementKey))
        {
            _editor.Select(null);
            return;
        }

        _editor.BeginDrag(elementKey, Enum.TryParse<DragHandle>(handle, out var parsed) ? parsed : DragHandle.Move);
    });

    /// <summary>Độ dời theo CSS pixel kể từ lúc nhấn → pixel canvas theo tỉ lệ hiển thị hiện tại.</summary>
    [JSInvokable]
    public Task OnCanvasDrag(double screenDeltaX, double screenDeltaY, bool keepAspect) => InvokeAsync(() =>
    {
        if (_editor is { IsDragging: true })
        {
            _editor.DragTo(CanvasGeometry.ToCanvasDelta(screenDeltaX, Scale), CanvasGeometry.ToCanvasDelta(screenDeltaY, Scale), keepAspect, _snap ? GridSize : 0);
        }
    });

    [JSInvokable]
    public Task OnCanvasDragEnd(double screenDeltaX, double screenDeltaY, bool keepAspect, bool cancelled) => InvokeAsync(() =>
    {
        if (_editor is not { IsDragging: true })
        {
            return;
        }

        if (cancelled)
        {
            _editor.CancelDrag();
            return;
        }

        _editor.DragTo(CanvasGeometry.ToCanvasDelta(screenDeltaX, Scale), CanvasGeometry.ToCanvasDelta(screenDeltaY, Scale), keepAspect, _snap ? GridSize : 0);
        _editor.EndDrag();
    });

    [JSInvokable]
    public Task OnZoomWheel(int direction) => InvokeAsync(() => StepZoom(direction));

    [JSInvokable]
    public Task OnShortcut(string command, bool shift) => InvokeAsync(async () =>
    {
        if (_editor is null || _preview || _confirmClose)
        {
            return;
        }

        switch (command)
        {
            case "undo":
                _editor.Undo();
                break;
            case "redo":
                _editor.Redo();
                break;
            case "duplicate":
                _editor.DuplicateSelected();
                break;
            case "delete":
                _editor.DeleteSelected();
                break;
            case "deselect":
                _editor.Select(null);
                break;
            case "save":
                await SaveAsync();
                break;
            case "ArrowLeft" or "ArrowRight" or "ArrowUp" or "ArrowDown":
                Nudge(command, shift ? 10 : 1);
                break;
        }
    });

    // ================================================================ Thao tác

    private void Nudge(string arrow, int step)
    {
        if (_editor?.Selected is not { } selected)
        {
            return;
        }

        var b = selected.Bounds;
        _editor.SetBounds(selected.Key, arrow switch
        {
            "ArrowLeft" => b with { X = b.X - step },
            "ArrowRight" => b with { X = b.X + step },
            "ArrowUp" => b with { Y = b.Y - step },
            _ => b with { Y = b.Y + step },
        });
    }

    private void StepZoom(int direction)
    {
        var current = Scale;
        var next = direction > 0
            ? ZoomSteps.FirstOrDefault(z => z > current + 0.001, MaxZoom)
            : ZoomSteps.LastOrDefault(z => z < current - 0.001, MinZoom);
        _zoom = Math.Clamp(next, MinZoom, MaxZoom);
    }

    private void FitZoom() => _zoom = null;

    private async Task AddImageAsync()
    {
        var file = await FilePicker.OpenAsync("Chọn ảnh trang trí (PNG giữ nền trong suốt)", FilePicker.ImageFilter);
        if (file is null || _editor is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var asset = await Library.AddAssetAsync(TemplateKey, file);
            _addedAssets.Add(asset.AssetPath);
            _editor.AddImage(asset.AssetPath, asset.WidthPixels, asset.HeightPixels);
        });
    }

    private async Task ReplaceImageAsync(Guid key)
    {
        var file = await FilePicker.OpenAsync("Chọn ảnh thay thế", FilePicker.ImageFilter);
        if (file is null || _editor is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var asset = await Library.AddAssetAsync(TemplateKey, file);
            _addedAssets.Add(asset.AssetPath);
            _editor.UpdateDecoration(key, d => d with { AssetPath = asset.AssetPath });
        });
    }

    private async Task SaveAsync()
    {
        if (_editor is null || _busy)
        {
            return;
        }

        if (Sessions.IsTemplateInActiveSession(Draft.Id))
        {
            ShowStatus(new StatusInfo(StatusKind.Warning, "Template đang được dùng trong phiên chụp chưa kết thúc.", "Hãy kết thúc phiên đó rồi lưu lại."));
            return;
        }

        await RunAsync(async () =>
        {
            var saving = _editor.Current;
            var result = await Library.SaveAsync(TemplateKey, saving.ToTemplate(null));
            if (!result.Succeeded)
            {
                ShowStatus(new StatusInfo(StatusKind.Error, result.Message ?? "Chưa lưu được template.", "Sửa các lỗi bên dưới rồi lưu lại; bản đã lưu trước đó vẫn được giữ nguyên."), result.Issues);
                return;
            }

            // Chỉ đánh dấu đã lưu nếu không có thay đổi mới trong lúc đang ghi.
            if (ReferenceEquals(_editor.Current, saving))
            {
                _editor.MarkSaved();
            }

            await Library.RemoveUnusedAddedAssetsAsync(TemplateKey, _addedAssets);
            var warnings = result.Issues.Where(i => i.Severity == ValidationSeverity.Warning).ToArray();
            ShowStatus(new StatusInfo(StatusKind.Success, "Đã lưu template.", warnings.Length > 0 ? "Template có cảnh báo nhưng vẫn dùng được." : null), warnings);
        });
    }

    private void OpenPreview()
    {
        if (_editor is null)
        {
            return;
        }

        var validation = Validation;
        if (!validation.IsValid)
        {
            ShowStatus(new StatusInfo(StatusKind.Error, "Chưa xem thử được: template còn lỗi."), validation.Issues);
            return;
        }

        _previewTemplate = Draft.ToTemplate(_assetDirectory);
        _previewRevision++;
        _preview = true;
    }

    private async Task<byte[]?> LoadPreviewAsync(CancellationToken cancellationToken) =>
        _previewTemplate is null ? null : await Composer.RenderTemplateSampleAsync(_previewTemplate, _previewContent, 1400, cancellationToken);

    private void SetPreviewContent(TemplateSampleContent content)
    {
        _previewContent = content;
        _previewRevision++;
    }

    private async Task RequestCloseAsync()
    {
        if (_editor is { HasUnsavedChanges: true })
        {
            _confirmClose = true;
            return;
        }

        await CloseAsync();
    }

    private async Task CloseAsync()
    {
        _confirmClose = false;
        try
        {
            // Asset vừa thêm nhưng bản đã lưu không dùng → dọn (asset có từ trước không bao giờ bị xóa).
            await Library.RemoveUnusedAddedAssetsAsync(TemplateKey, _addedAssets);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TemplateLibraryException)
        {
            Logger.LogWarning(ex, "Không dọn được asset tạm của Editor");
        }

        await OnClose.InvokeAsync();
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            await action();
        }
        catch (TemplateLibraryException ex)
        {
            ShowStatus(new StatusInfo(StatusKind.Error, ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning(ex, "Thao tác Template Editor thất bại");
            ShowStatus(new StatusInfo(StatusKind.Error, "Không thực hiện được thao tác.", "Kiểm tra ổ đĩa và quyền truy cập."));
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowStatus(StatusInfo status, IReadOnlyList<TemplateValidationIssue>? issues = null)
    {
        _status = status;
        _statusIssues = issues ?? [];
    }

    // ================================================================ Hiển thị

    private Task<byte[]?> LoadAssetAsync(string? assetPath, CancellationToken cancellationToken) =>
        assetPath is null ? Task.FromResult<byte[]?>(null) : LoadAssetCoreAsync(assetPath, cancellationToken);

    private async Task<byte[]?> LoadAssetCoreAsync(string assetPath, CancellationToken cancellationToken) =>
        await Library.ReadAssetAsync(TemplateKey, assetPath, cancellationToken);

    private static string ContentTypeOf(string? assetPath) => Path.GetExtension(assetPath ?? string.Empty).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "image/jpeg",
    };

    private string Percent(double value, int total) =>
        (value * 100.0 / Math.Max(1, total)).ToString("0.####", CultureInfo.InvariantCulture) + "%";

    private string Px(double canvasPixels) => (canvasPixels * Scale).ToString("0.##", CultureInfo.InvariantCulture) + "px";

    private string CanvasStyle => string.Create(
        CultureInfo.InvariantCulture,
        $"width: {Canvas.WidthPixels * Scale:0.##}px; height: {Canvas.HeightPixels * Scale:0.##}px; background: {CssColor(Canvas.BackgroundColor) ?? "#FFFFFF"};");

    private string ElementStyle(EditorElement element, int layer)
    {
        var b = element.Bounds;
        return $"left: {Percent(b.X, Canvas.WidthPixels)}; top: {Percent(b.Y, Canvas.HeightPixels)}; width: {Percent(b.Width, Canvas.WidthPixels)}; height: {Percent(b.Height, Canvas.HeightPixels)}; z-index: {layer + 1};";
    }

    private string SlotStyle(TemplateSlot slot)
    {
        var radius = Math.Clamp(slot.CornerRadius, 0, Math.Min(slot.Width, slot.Height) / 2.0);
        var border = slot.BorderWidth > 0 ? $"box-shadow: inset 0 0 0 {Px(slot.BorderWidth)} {CssColor(slot.BorderColor) ?? "transparent"};" : string.Empty;
        return $"border-radius: {Px(radius)}; background: {CssColor(slot.BackgroundColor) ?? "rgba(203, 213, 225, 0.85)"}; {border}";
    }

    private string ShapeStyle(TemplateDecoration d)
    {
        var radius = d.Type == DecorationKind.Ellipse ? "50%" : Px(Math.Clamp(d.CornerRadius, 0, Math.Min(d.Width, d.Height) / 2.0));
        var stroke = d.StrokeWidth > 0 && d.StrokeColor is not null ? $"box-shadow: inset 0 0 0 {Px(d.StrokeWidth / 2.0)} {CssColor(d.StrokeColor)};" : string.Empty;
        return $"border-radius: {radius}; background: {CssColor(d.Color) ?? "transparent"}; {stroke}";
    }

    private string TextStyle(TemplateDecoration d) => string.Create(
        CultureInfo.InvariantCulture,
        $"font-family: '{(d.FontFamily ?? "Segoe UI").Replace("'", string.Empty, StringComparison.Ordinal)}'; font-size: {Px(d.FontSizePixels)}; font-weight: {(d.Bold ? 700 : 400)}; color: {CssColor(d.Color ?? "#000000")}; justify-content: {d.TextAlign switch { TextAlignment.Left => "flex-start", TextAlignment.Right => "flex-end", _ => "center" }};");

    /// <summary>#AARRGGBB (Engine) → rgba() (CSS). Màu sai → null.</summary>
    private static string? CssColor(string? hex)
    {
        if (!HexColor.TryParse(hex, out var argb))
        {
            return null;
        }

        var a = ((argb >> 24) & 0xFF) / 255.0;
        return string.Create(CultureInfo.InvariantCulture, $"rgba({(argb >> 16) & 0xFF}, {(argb >> 8) & 0xFF}, {argb & 0xFF}, {a:0.###})");
    }

    private static string KindLabel(EditorElementKind kind) => kind switch
    {
        EditorElementKind.Slot => "Ô ảnh",
        EditorElementKind.Text => "Chữ",
        EditorElementKind.Rectangle => "Hình chữ nhật",
        EditorElementKind.Ellipse => "Hình elip",
        EditorElementKind.QrCode => "Vùng mã QR",
        _ => "Ảnh trang trí",
    };

    private static string LayerTitle(EditorElement element) => element.Kind switch
    {
        EditorElementKind.Slot => $"Ô ảnh — ảnh {element.Slot!.PhotoIndex + 1}",
        EditorElementKind.Text => $"Chữ “{Shorten(element.Decoration!.Text)}”",
        EditorElementKind.Image => $"Ảnh {Path.GetFileName(element.Decoration!.AssetPath)}",
        _ => KindLabel(element.Kind),
    };

    private static string Shorten(string? text) => text is { Length: > 18 } ? text[..18] + "…" : text ?? string.Empty;

    private string ResolvedText(TemplateDecoration d) => TemplateText.Resolve(d.Text ?? string.Empty, DateTimeOffset.Now);

    private bool FontMissing(string? font) =>
        !string.IsNullOrWhiteSpace(font) && _fonts.Count > 0 && !_fonts.Contains(font, StringComparer.OrdinalIgnoreCase);

    private static readonly (DragHandle Handle, string Css)[] Handles =
    [
        (DragHandle.NorthWest, "nw"), (DragHandle.North, "n"), (DragHandle.NorthEast, "ne"), (DragHandle.East, "e"),
        (DragHandle.SouthEast, "se"), (DragHandle.South, "s"), (DragHandle.SouthWest, "sw"), (DragHandle.West, "w"),
    ];

    // ================================================================ Thuộc tính (một bước Undo mỗi lần ghi nhận)

    private void SetSlot(Guid key, Func<TemplateSlot, TemplateSlot> change) => _editor!.UpdateSlot(key, change);

    private void SetDecoration(Guid key, Func<TemplateDecoration, TemplateDecoration> change) => _editor!.UpdateDecoration(key, change);

    private void SetBound(EditorElement element, char field, double value)
    {
        var b = element.Bounds;
        var v = (int)value;
        _editor!.SetBounds(element.Key, field switch
        {
            'x' => b with { X = v },
            'y' => b with { Y = v },
            'w' => b with { Width = Math.Max(1, v) },
            _ => b with { Height = Math.Max(1, v) },
        });
    }

    private void CenterSelected(bool horizontal, bool vertical)
    {
        if (_editor?.Selected is { } selected)
        {
            _editor.SetBounds(selected.Key, CanvasGeometry.Center(selected.Bounds, Canvas.WidthPixels, Canvas.HeightPixels, horizontal, vertical));
        }
    }

    private void SetFocalFromClick(Guid key, Microsoft.AspNetCore.Components.Web.MouseEventArgs e, double boxWidth, double boxHeight)
    {
        var x = Math.Round(Math.Clamp(e.OffsetX / boxWidth, 0, 1), 2);
        var y = Math.Round(Math.Clamp(e.OffsetY / boxHeight, 0, 1), 2);
        SetSlot(key, s => s with { FocalPoint = new FocalPoint { X = x, Y = y } });
    }

    private void SetCanvas(Func<TemplateCanvas, TemplateCanvas> change) => _editor!.SetCanvas(change(Canvas));

    /// <summary>Bật viền thì cần màu viền (Engine bắt buộc): mặc định trắng.</summary>
    private void SetSlotBorder(Guid key, int width) =>
        SetSlot(key, s => s with { BorderWidth = width, BorderColor = width > 0 ? s.BorderColor ?? "#FFFFFF" : s.BorderColor });

    private void SetStroke(Guid key, int width) =>
        SetDecoration(key, d => d with { StrokeWidth = width, StrokeColor = width > 0 ? d.StrokeColor ?? "#000000" : d.StrokeColor });

    /// <summary>Ô chọn focal point cùng tỉ lệ với ô ảnh, cạnh dài 200 CSS px.</summary>
    private static (double Width, double Height) FocalBox(TemplateSlot slot)
    {
        const double longSide = 200;
        var w = Math.Max(1, slot.Width);
        var h = Math.Max(1, slot.Height);
        return w >= h ? (longSide, longSide * h / w) : (longSide * w / h, longSide);
    }

    private static string FocalBoxStyle((double Width, double Height) box) =>
        string.Create(CultureInfo.InvariantCulture, $"width: {box.Width:0.##}px; height: {box.Height:0.##}px;");

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_editor is not null)
        {
            _editor.Changed -= OnEditorChanged;
        }

        if (_jsHandle is { } handle)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await JS.InvokeVoidAsync("photoboothTemplateEditor.detach", timeout.Token, handle);
            }
            catch (Exception ex) when (ex is JSDisconnectedException or JSException or OperationCanceledException or ObjectDisposedException)
            {
                // WebView đang đóng: listener bị hủy cùng trang.
            }
        }

        _selfReference?.Dispose();
    }
}
