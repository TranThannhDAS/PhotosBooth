using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Composition;
using Photobooth.Application.Templates;
using Photobooth.Infrastructure.Templates;
using Photobooth.Tests.TestSupport;

namespace Photobooth.Tests.Templates;

/// <summary>Đọc template.json: hợp lệ, không hợp lệ, giá trị mặc định.</summary>
public sealed class TemplateLoadingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string WriteTemplate(string json, string folder = "sample") =>
        TestTemplateFiles.Write(_temp.Path, folder, json);

    [Fact]
    public void LoadTemplateFile_MinimalJson_AppliesDefaults()
    {
        var file = WriteTemplate("""
            {
              "id": "sample", "name": "Mẫu",
              "canvas": { "widthPixels": 600, "heightPixels": 900 },
              "slots": [ { "photoIndex": 0, "x": 0, "y": 0, "width": 600, "height": 450 } ],
              "decorations": [
                { "type": "text", "x": 0, "y": 500, "width": 600, "height": 80, "text": "Xin chào" },
                { "type": "image", "assetPath": "assets/logo.png", "x": 0, "y": 600, "width": 100, "height": 100 }
              ]
            }
            """);
        TestTemplateFiles.WritePng(_temp.Path, "sample/assets/logo.png");

        var (template, validation) = JsonTemplateCatalog.LoadTemplateFile(file);

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Issues));
        Assert.NotNull(template);
        Assert.Equal((300, "#FFFFFF"), (template.Canvas.Dpi, template.Canvas.BackgroundColor));
        var slot = template.Slots[0];
        Assert.Equal((ImageFit.Cover, 0.5, 0.5, 0, 0), (slot.Fit, slot.FocalPoint.X, slot.FocalPoint.Y, slot.ZIndex, slot.CornerRadius));
        Assert.Null(slot.Id);
        Assert.Null(slot.BackgroundColor);
        var text = template.Decorations[0];
        Assert.Equal((TextAlignment.Center, 48, false, 0), (text.TextAlign, text.FontSizePixels, text.Bold, text.ZIndex));
        Assert.Null(text.FontFamily);
        Assert.Equal(ImageFit.Contain, template.Decorations[1].Fit);
        Assert.Equal(Path.Combine(_temp.Path, "sample"), template.AssetDirectory);
    }

    [Fact]
    public void LoadTemplateFile_DoesNotModifyFile()
    {
        var file = WriteTemplate(TestTemplateFiles.ValidJson("sample"));
        var before = File.ReadAllBytes(file);
        var writeTime = File.GetLastWriteTimeUtc(file);

        JsonTemplateCatalog.LoadTemplateFile(file);

        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(file));
    }

    [Theory]
    [InlineData("{ \"id\": \"x\", ", "dòng")]                                                    // cú pháp hỏng
    [InlineData("{ \"id\": \"x\", \"name\": \"X\", \"slots\": [] }", "canvas")]                  // thiếu thuộc tính bắt buộc
    [InlineData("{ \"id\": \"x\", \"name\": \"X\", \"layer\": 1, \"canvas\": { \"widthPixels\": 10, \"heightPixels\": 10 } }", "layer")] // thuộc tính không hỗ trợ
    public void LoadTemplateFile_InvalidJson_ReportsReason(string json, string expectedInMessage)
    {
        var (template, validation) = JsonTemplateCatalog.LoadTemplateFile(WriteTemplate(json));

        Assert.Null(template);
        var error = Assert.Single(validation.Errors);
        Assert.Contains(expectedInMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("\"type\": \"star\"", "$.decorations[0].type")]
    [InlineData("\"type\": \"rectangle\", \"zIndex\": \"high\"", "$.decorations[0].zIndex")]
    public void LoadTemplateFile_UnsupportedValue_ReportsPath(string decorationProps, string expectedPath)
    {
        var json = TestTemplateFiles.ValidJson("sample").Replace("\"decorations\": []", $"\"decorations\": [ {{ {decorationProps}, \"width\": 10, \"height\": 10 }} ]");

        var (template, validation) = JsonTemplateCatalog.LoadTemplateFile(WriteTemplate(json));

        Assert.Null(template);
        Assert.Equal(expectedPath, Assert.Single(validation.Errors).Path);
    }

    [Fact]
    public void LoadTemplateFile_UnsupportedFit_IsError()
    {
        var json = TestTemplateFiles.ValidJson("sample").Replace("\"height\": 100 }", "\"height\": 100, \"fit\": \"stretch\" }");

        var (template, validation) = JsonTemplateCatalog.LoadTemplateFile(WriteTemplate(json));

        Assert.Null(template);
        Assert.Contains("fit", Assert.Single(validation.Errors).Path, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadTemplateFile_NullCollection_IsError()
    {
        var json = TestTemplateFiles.ValidJson("sample").Replace("\"decorations\": []", "\"decorations\": null");

        var (template, _) = JsonTemplateCatalog.LoadTemplateFile(WriteTemplate(json));

        Assert.Null(template);
    }
}

/// <summary>Kiểm tra nội dung template: lỗi (không dùng được) và cảnh báo (vẫn dùng được).</summary>
public class TemplateValidatorTests
{
    private static PhotoTemplate Valid => TestTemplates.FourSlots();

    private static TemplateSlot FirstSlot => Valid.Slots[0];

    private static PhotoTemplate WithSlot(TemplateSlot slot) => Valid with { Slots = [slot, .. Valid.Slots.Skip(1)] };

    private static void AssertError(PhotoTemplate template, string path)
    {
        var result = TemplateValidator.Validate(template);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == path);
    }

    private static void AssertWarningOnly(PhotoTemplate template, string path)
    {
        var result = TemplateValidator.Validate(template);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains(result.Warnings, w => w.Path == path);
    }

    [Fact]
    public void ValidTemplate_HasNoIssues()
    {
        Assert.Empty(TemplateValidator.Validate(Valid).Issues);
        Assert.Equal(4, Valid.RequiredPhotoCount);
    }

    [Theory]
    [InlineData(0, 900, 150, "canvas.widthPixels")]
    [InlineData(600, -1, 150, "canvas.heightPixels")]
    [InlineData(600, 900, 50, "canvas.dpi")]
    [InlineData(600, 900, 5000, "canvas.dpi")]
    public void Canvas_InvalidValues_AreErrors(int width, int height, int dpi, string path) =>
        AssertError(Valid with { Canvas = Valid.Canvas with { WidthPixels = width, HeightPixels = height, Dpi = dpi } }, path);

    [Fact]
    public void Canvas_InvalidBackgroundColor_IsError() =>
        AssertError(Valid with { Canvas = Valid.Canvas with { BackgroundColor = "red" } }, "canvas.backgroundColor");

    [Fact]
    public void NoSlots_IsError() => AssertError(Valid with { Slots = [] }, "slots");

    [Fact]
    public void Slot_ZeroOrNegativeSize_IsError()
    {
        AssertError(WithSlot(FirstSlot with { Width = 0 }), "slots[0].width");
        AssertError(WithSlot(FirstSlot with { Height = -5 }), "slots[0].height");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    public void Slot_InvalidPhotoIndex_IsError(int photoIndex) =>
        AssertError(WithSlot(FirstSlot with { PhotoIndex = photoIndex }), "slots[0].photoIndex");

    [Theory]
    [InlineData(-0.1, 0.5, "slots[0].focalPoint.x")]
    [InlineData(0.5, 1.5, "slots[0].focalPoint.y")]
    public void Slot_FocalPointOutOfRange_IsError(double x, double y, string path) =>
        AssertError(WithSlot(FirstSlot with { FocalPoint = new FocalPoint { X = x, Y = y } }), path);

    [Fact]
    public void Slot_CornerRadius_NegativeIsError_TooLargeIsWarning()
    {
        AssertError(WithSlot(FirstSlot with { CornerRadius = -1 }), "slots[0].cornerRadius");
        AssertWarningOnly(WithSlot(FirstSlot with { CornerRadius = 500 }), "slots[0].cornerRadius");
    }

    [Fact]
    public void ZIndex_OutOfRange_IsError() =>
        AssertError(WithSlot(FirstSlot with { ZIndex = 5000 }), "slots[0].zIndex");

    [Fact]
    public void Slot_InvalidColors_AreErrors()
    {
        AssertError(WithSlot(FirstSlot with { BackgroundColor = "#12" }), "slots[0].backgroundColor");
        AssertError(WithSlot(FirstSlot with { BorderWidth = 4, BorderColor = null }), "slots[0].borderColor");
    }

    [Fact]
    public void ElementOutsideCanvas_IsOnlyWarning()
    {
        AssertWarningOnly(WithSlot(FirstSlot with { X = -50 }), "slots[0]");
        AssertWarningOnly(
            Valid with { Decorations = [new TemplateDecoration { Type = DecorationKind.Ellipse, X = -100, Y = -100, Width = 300, Height = 300, Color = "#FF0000" }] },
            "decorations[0]");
    }

    [Fact]
    public void SamePhotoInManySlots_IsAllowed_UnusedPhotoIsWarning()
    {
        var shared = Valid with { Slots = [.. Valid.Slots, Valid.Slots[0] with { X = 20, Y = 700 }] };
        Assert.True(TemplateValidator.Validate(shared).IsValid);

        var gap = Valid with { Slots = [FirstSlot, FirstSlot with { PhotoIndex = 2, X = 310 }] };
        var result = TemplateValidator.Validate(gap);
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Message.Contains("photoIndex 1", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateIds_AreErrors()
    {
        var template = Valid with
        {
            Slots = [FirstSlot with { Id = "a" }, .. Valid.Slots.Skip(1)],
            Decorations = [new TemplateDecoration { Id = "a", Type = DecorationKind.Rectangle, Width = 10, Height = 10, Color = "#000000" }],
        };

        var result = TemplateValidator.Validate(template);

        Assert.Contains(result.Errors, e => e.Message.Contains("'a'", StringComparison.Ordinal));
    }

    [Fact]
    public void TextDecoration_EmptyTextIsError_OversizedFontIsWarning()
    {
        var text = new TemplateDecoration { Type = DecorationKind.Text, Width = 200, Height = 40, Text = "Xin chào" };
        AssertError(Valid with { Decorations = [text with { Text = " " }] }, "decorations[0].text");
        AssertError(Valid with { Decorations = [text with { FontSizePixels = 0 }] }, "decorations[0].fontSizePixels");
        AssertWarningOnly(Valid with { Decorations = [text with { FontSizePixels = 80 }] }, "decorations[0].fontSizePixels");
    }

    [Theory]
    [InlineData("../logo.png")]
    [InlineData("assets/../../logo.png")]
    [InlineData(@"C:\logo.png")]
    [InlineData(@"\\server\share\logo.png")]
    [InlineData("/etc/logo.png")]
    [InlineData("http://example.com/logo.png")]
    [InlineData("")]
    [InlineData(null)]
    public void ImageDecoration_UnsafeAssetPath_IsError(string? assetPath)
    {
        var template = Valid with { Decorations = [new TemplateDecoration { Type = DecorationKind.Image, Width = 10, Height = 10, AssetPath = assetPath }] };

        AssertError(template, "decorations[0].assetPath");
        Assert.False(TemplateAssetPath.IsSafeRelative(assetPath));
    }

    [Theory]
    [InlineData("logo.png")]
    [InlineData("assets/logo.png")]
    [InlineData(@"assets\stickers\star.png")]
    public void SafeAssetPaths_AreAccepted(string assetPath) => Assert.True(TemplateAssetPath.IsSafeRelative(assetPath));

    [Theory]
    [InlineData("Bad Id")]
    [InlineData("")]
    public void InvalidTemplateId_IsError(string id) => AssertError(Valid with { Id = id }, "id");

    [Theory]
    [InlineData("#FFFFFF", 0xFFFFFFFFu)]
    [InlineData("#80102030", 0x80102030u)]
    public void HexColor_ParsesSupportedFormats(string value, uint expected)
    {
        Assert.True(HexColor.TryParse(value, out var argb));
        Assert.Equal(expected, argb);
    }

    [Theory]
    [InlineData("FFFFFF")]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    [InlineData(null)]
    public void HexColor_RejectsInvalid(string? value) => Assert.False(HexColor.TryParse(value, out _));
}

public class TemplateTextTests
{
    [Fact]
    public void Resolve_ReplacesAllPlaceholdersFromOneTimestamp()
    {
        var timestamp = new DateTimeOffset(2026, 11, 19, 9, 5, 0, TimeSpan.FromHours(7));

        Assert.Equal(
            "19/11/2026 | 09:05 | 19/11/2026 09:05 | {other}",
            TemplateText.Resolve("{date} | {time} | {datetime} | {other}", timestamp));
    }
}

public class TemplateLayersTests
{
    [Fact]
    public void GetRenderOrder_SortsByZIndex_AcrossElementTypes()
    {
        var template = TestTemplates.TwoSlots() with
        {
            Slots =
            [
                new TemplateSlot { Id = "s0", PhotoIndex = 0, X = 0, Y = 0, Width = 10, Height = 10, ZIndex = 10 },
                new TemplateSlot { Id = "s1", PhotoIndex = 1, X = 0, Y = 0, Width = 10, Height = 10, ZIndex = -5 },
            ],
            Decorations =
            [
                new TemplateDecoration { Id = "top", Type = DecorationKind.Text, Text = "x", Width = 10, Height = 10, ZIndex = 30 },
                new TemplateDecoration { Id = "bottom", Type = DecorationKind.Rectangle, Width = 10, Height = 10, ZIndex = -10 },
            ],
        };

        var order = TemplateLayers.GetRenderOrder(template).Select(e => e.Slot?.Id ?? e.Decoration!.Id);

        Assert.Equal(["bottom", "s1", "s0", "top"], order);
    }

    [Fact]
    public void GetRenderOrder_SameZIndex_IsStable_SlotsThenDecorationsInDeclarationOrder()
    {
        var template = TestTemplates.TwoSlots() with
        {
            Slots =
            [
                new TemplateSlot { Id = "s0", PhotoIndex = 0, X = 0, Y = 0, Width = 10, Height = 10 },
                new TemplateSlot { Id = "s1", PhotoIndex = 1, X = 0, Y = 0, Width = 10, Height = 10 },
            ],
            Decorations =
            [
                new TemplateDecoration { Id = "d0", Type = DecorationKind.Rectangle, Width = 10, Height = 10 },
                new TemplateDecoration { Id = "d1", Type = DecorationKind.Rectangle, Width = 10, Height = 10 },
            ],
        };

        for (var run = 0; run < 3; run++)
        {
            Assert.Equal(["s0", "s1", "d0", "d1"], TemplateLayers.GetRenderOrder(template).Select(e => e.Slot?.Id ?? e.Decoration!.Id));
        }
    }
}

public class CropCalculatorTests
{
    [Fact]
    public void CenterCrop_WiderSource_CropsSidesKeepingAspect() =>
        Assert.Equal(new PixelRect(420, 0, 1080, 1080), CropCalculator.CenterCrop(1920, 1080, 500, 500));

    [Fact]
    public void CenterCrop_TallerSource_CropsTopAndBottom() =>
        Assert.Equal(new PixelRect(0, 250, 1000, 500), CropCalculator.CenterCrop(1000, 1000, 400, 200));

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(1.0, 840)]
    [InlineData(0.3, 36)]
    [InlineData(0.5, 420)]
    public void Cover_FocalPointX_MovesCropWindowAndClamps(double focalX, double expectedX)
    {
        var crop = CropCalculator.Cover(1920, 1080, 500, 500, focalX, 0.5);

        Assert.Equal(expectedX, crop.X, precision: 6);
        Assert.Equal(1080, crop.Width, precision: 6);
    }

    [Fact]
    public void Cover_FocalPointY_MovesCropWindowVertically()
    {
        Assert.Equal(0, CropCalculator.Cover(1000, 1000, 400, 200, 0.5, 0).Y);
        Assert.Equal(500, CropCalculator.Cover(1000, 1000, 400, 200, 0.5, 1).Y);
    }

    [Theory]
    [InlineData(1920, 1080, 520, 370, 0.2, 0.9)]
    [InlineData(1280, 720, 340, 340, 0.9, 0.1)]
    [InlineData(640, 480, 520, 640, 0.5, 0.5)]
    public void Cover_ResultHasTargetAspectAndStaysInsideSource(int sw, int sh, int tw, int th, double fx, double fy)
    {
        var crop = CropCalculator.Cover(sw, sh, tw, th, fx, fy);

        Assert.Equal((double)tw / th, crop.Width / crop.Height, precision: 6);
        Assert.True(crop.X >= 0 && crop.Y >= 0 && crop.X + crop.Width <= sw + 1e-9 && crop.Y + crop.Height <= sh + 1e-9);
    }

    [Fact]
    public void Fit_CentersInsideTarget() =>
        Assert.Equal(new PixelRect(0, 25, 100, 50), CropCalculator.Fit(200, 100, new PixelRect(0, 0, 100, 100)));
}

public sealed class JsonTemplateCatalogTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ShippedTemplates_AreValidAndIncludeRequiredLayouts()
    {
        var catalog = new JsonTemplateCatalog(TestTemplates.ShippedTemplatesDirectory, NullLogger<JsonTemplateCatalog>.Instance);

        var templates = await catalog.GetTemplatesAsync();
        var diagnostics = await catalog.GetDiagnosticsAsync();

        Assert.Equal(["grid-2x2", "hero-three", "party-overlay", "strip-four"], templates.Select(t => t.Id));
        Assert.All(templates, t =>
        {
            Assert.True(TemplateValidator.Validate(t).IsValid);
            Assert.Equal(4, t.RequiredPhotoCount);
            Assert.Equal((1200, 1800, 300), (t.Canvas.WidthPixels, t.Canvas.HeightPixels, t.Canvas.Dpi));
        });

        // Chỉ được phép có cảnh báo "tràn canvas" cho các hình tròn trang trí cố ý tràn mép của grid-2x2.
        var warnings = diagnostics.Where(d => d.Loaded).SelectMany(d => d.Issues.Select(i => (d.TemplateId, Issue: i))).ToArray();
        Assert.All(warnings, w =>
        {
            Assert.Equal("grid-2x2", w.TemplateId);
            Assert.Equal(ValidationSeverity.Warning, w.Issue.Severity);
            Assert.Contains("canvas", w.Issue.Message, StringComparison.Ordinal);
        });

        // Template cũ dạng file phẳng: giữ nguyên trên ổ đĩa, không nạp, có thông báo lý do.
        var legacy = diagnostics.Where(d => d.TemplateFile.EndsWith(".json", StringComparison.Ordinal) && !d.TemplateFile.Contains('/')).ToArray();
        Assert.All(legacy, d => Assert.False(d.Loaded));
    }

    [Fact]
    public async Task InvalidDuplicateAndLegacyTemplates_AreSkippedWithDiagnostics()
    {
        TestTemplateFiles.Write(_temp.Path, "a-ok", TestTemplateFiles.ValidJson("ok"));
        TestTemplateFiles.Write(_temp.Path, "b-dup", TestTemplateFiles.ValidJson("ok"));
        TestTemplateFiles.Write(_temp.Path, "c-broken", "{ not json");
        TestTemplateFiles.Write(_temp.Path, "d-missing-asset", TestTemplateFiles.ValidJson("asset").Replace(
            "\"decorations\": []",
            "\"decorations\": [ { \"type\": \"image\", \"assetPath\": \"assets/none.png\", \"width\": 10, \"height\": 10 } ]"));
        Directory.CreateDirectory(Path.Combine(_temp.Path, "e-empty"));
        await File.WriteAllTextAsync(Path.Combine(_temp.Path, "legacy.json"), TestTemplateFiles.ValidJson("legacy"));

        var catalog = new JsonTemplateCatalog(_temp.Path, NullLogger<JsonTemplateCatalog>.Instance);
        var templates = await catalog.GetTemplatesAsync();
        var diagnostics = (await catalog.GetDiagnosticsAsync()).ToDictionary(d => d.TemplateFile);

        Assert.Equal("ok", Assert.Single(templates).Id);
        Assert.Contains(diagnostics["b-dup/template.json"].Issues, i => i.Message.Contains("trùng", StringComparison.Ordinal));
        Assert.False(diagnostics["c-broken/template.json"].Loaded);
        Assert.Contains(diagnostics["d-missing-asset/template.json"].Issues, i => i.Path == "decorations[0].assetPath");
        Assert.False(diagnostics["e-empty/template.json"].Loaded);
        Assert.False(diagnostics["legacy.json"].Loaded);
        Assert.Contains(diagnostics["a-ok/template.json"].Issues, i => i.Severity == ValidationSeverity.Warning && i.Path == "id");
    }

    [Fact]
    public async Task CorruptAsset_IsError()
    {
        var folder = TestTemplateFiles.Write(_temp.Path, "corrupt", TestTemplateFiles.ValidJson("corrupt").Replace(
            "\"decorations\": []",
            "\"decorations\": [ { \"type\": \"image\", \"assetPath\": \"assets/bad.png\", \"width\": 10, \"height\": 10 } ]"));
        Directory.CreateDirectory(Path.Combine(_temp.Path, "corrupt", "assets"));
        await File.WriteAllBytesAsync(Path.Combine(_temp.Path, "corrupt", "assets", "bad.png"), [1, 2, 3, 4, 5]);

        var (template, validation) = JsonTemplateCatalog.LoadTemplateFile(folder);

        Assert.Null(template);
        Assert.Contains(validation.Errors, e => e.Path == "decorations[0].assetPath" && e.Message.Contains("hỏng", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingFont_IsWarningOnly()
    {
        var file = TestTemplateFiles.Write(_temp.Path, "font", TestTemplateFiles.ValidJson("font").Replace(
            "\"decorations\": []",
            "\"decorations\": [ { \"type\": \"text\", \"text\": \"A\", \"fontFamily\": \"Không Tồn Tại Font\", \"width\": 100, \"height\": 50 } ]"));

        var (template, validation) = JsonTemplateCatalog.LoadTemplateFile(file);

        Assert.NotNull(template);
        Assert.Contains(validation.Warnings, w => w.Path == "decorations[0].fontFamily");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task MissingDirectory_ReturnsEmpty()
    {
        var catalog = new JsonTemplateCatalog(Path.Combine(_temp.Path, "none"), NullLogger<JsonTemplateCatalog>.Instance);

        Assert.Empty(await catalog.GetTemplatesAsync());
    }
}
