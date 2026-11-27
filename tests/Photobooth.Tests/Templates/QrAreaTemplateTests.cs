using Photobooth.Application.Templates;
using Photobooth.Application.Templates.Editing;
using Photobooth.Infrastructure.Templates;
using Photobooth.Tests.TestSupport;

namespace Photobooth.Tests.Templates;

/// <summary>Vùng mã QR trong template: đọc JSON, kiểm tra hợp lệ, Template Editor (một vùng, luôn vuông).</summary>
public sealed class QrAreaTemplateTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static PhotoTemplate WithQr(params TemplateDecoration[] decorations) => new()
    {
        Id = "qr-test",
        Name = "QR",
        Canvas = new TemplateCanvas { WidthPixels = 1200, HeightPixels = 1800, Dpi = 300 },
        Slots = [new TemplateSlot { PhotoIndex = 0, X = 0, Y = 0, Width = 1200, Height = 1200 }],
        Decorations = decorations,
    };

    private static TemplateDecoration Qr(int x = 950, int y = 1550, int width = 200, int? height = null) =>
        new() { Type = DecorationKind.QrCode, X = x, Y = y, Width = width, Height = height ?? width };

    [Fact]
    public void Json_QrCodeType_LoadsAsQrArea()
    {
        var file = TestTemplateFiles.Write(_temp.Path, "qr", """
            {
              "id": "qr", "name": "Có QR",
              "canvas": { "widthPixels": 1200, "heightPixels": 1800, "dpi": 300 },
              "slots": [ { "photoIndex": 0, "x": 0, "y": 0, "width": 1200, "height": 1200 } ],
              "decorations": [ { "id": "qr", "type": "qrCode", "x": 950, "y": 1550, "width": 200, "height": 200 } ]
            }
            """);

        var (template, validation) = JsonTemplateCatalog.LoadTemplateFile(file);

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Issues));
        Assert.Equal((950, 1550, 200), (template!.QrArea!.X, template.QrArea.Y, template.QrArea.Width));
    }

    [Fact]
    public void ValidQrArea_Passes_AndTemplateWithoutQrHasNoArea()
    {
        Assert.True(TemplateValidator.Validate(WithQr(Qr())).IsValid);
        Assert.Null(WithQr().QrArea);
    }

    [Fact]
    public void MinimumSize_IsFifteenMillimetresAtCanvasDpi()
    {
        Assert.Equal(178, TemplateValidator.MinQrSizePixels(300));
        Assert.Equal(60, TemplateValidator.MinQrSizePixels(100));
    }

    [Theory]
    [InlineData(950, 1550, 200, 180)] // không vuông
    [InlineData(950, 1550, 150, 150)] // quá nhỏ để quét ở 300 DPI
    [InlineData(1100, 1550, 200, 200)] // tràn khỏi canvas
    [InlineData(-10, 1550, 200, 200)]
    public void InvalidQrArea_IsError(int x, int y, int width, int height)
    {
        var result = TemplateValidator.Validate(WithQr(Qr(x, y, width, height)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path.StartsWith("decorations[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoQrAreas_IsError()
    {
        var result = TemplateValidator.Validate(WithQr(Qr(), Qr(x: 100)));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "decorations");
    }

    [Fact]
    public void Editor_AddQrCode_IsValidSquare_AndOnlyOnce()
    {
        var editor = new TemplateEditor(TemplateDraft.FromTemplate(WithQr()));

        var first = editor.AddQrCode();
        var again = editor.AddQrCode();

        Assert.Equal(first.Key, again.Key);
        Assert.Single(editor.Current.Layers, l => l.Kind == EditorElementKind.QrCode);
        Assert.Equal(first.Bounds.Width, first.Bounds.Height);
        Assert.True(editor.Validate().IsValid, string.Join(Environment.NewLine, editor.Validate().Issues));
        Assert.Null(editor.DuplicateSelected());
    }

    [Theory]
    [InlineData(DragHandle.East, 60, 0)]
    [InlineData(DragHandle.South, 0, 80)]
    [InlineData(DragHandle.NorthWest, -50, -10)]
    public void Editor_ResizingQr_KeepsSquare(DragHandle handle, double dx, double dy)
    {
        var editor = new TemplateEditor(TemplateDraft.FromTemplate(WithQr(Qr(x: 500, y: 500))));
        var qr = editor.Current.Layers.Single(l => l.Kind == EditorElementKind.QrCode);

        Assert.True(editor.BeginDrag(qr.Key, handle));
        editor.DragTo(dx, dy);
        editor.EndDrag();

        var bounds = editor.Current.Find(qr.Key)!.Bounds;
        Assert.Equal(bounds.Width, bounds.Height);
        Assert.NotEqual(200, bounds.Width);
    }

    [Fact]
    public void Editor_SettingWidth_SetsHeightToo()
    {
        var editor = new TemplateEditor(TemplateDraft.FromTemplate(WithQr(Qr())));
        var qr = editor.Current.Layers.Single(l => l.Kind == EditorElementKind.QrCode);

        editor.SetBounds(qr.Key, qr.Bounds with { Width = 240 });
        Assert.Equal((240, 240), (editor.Current.Find(qr.Key)!.Bounds.Width, editor.Current.Find(qr.Key)!.Bounds.Height));

        editor.SetBounds(qr.Key, editor.Current.Find(qr.Key)!.Bounds with { Height = 220 });
        Assert.Equal((220, 220), (editor.Current.Find(qr.Key)!.Bounds.Width, editor.Current.Find(qr.Key)!.Bounds.Height));
    }
}
