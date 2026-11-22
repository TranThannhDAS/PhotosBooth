using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Export;
using Photobooth.Application.Photos;
using Photobooth.Application.Sessions;
using Photobooth.Application.Templates;
using Photobooth.Application.Templates.Editing;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Storage;
using Photobooth.Infrastructure.Templates;
using Photobooth.Tests.TestSupport;
using SkiaSharp;

namespace Photobooth.Tests.Templates;

/// <summary>Phase 3.2 — Visual Template Editor: mô hình chỉnh sửa, ánh xạ sang Template Model, lưu và xem thử.</summary>
public sealed class TemplateEditorTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PhotoboothStoragePaths _paths;
    private readonly SkiaPhotoComposer _composer;

    public TemplateEditorTests()
    {
        _paths = new PhotoboothStoragePaths(Path.Combine(_temp.Path, "AppData"));
        _composer = new SkiaPhotoComposer(_paths, NullLogger<SkiaPhotoComposer>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    private TemplateLibrary CreateLibrary() =>
        new(Path.Combine(_paths.RootDirectory, "Templates"), null, _composer, NullLogger<TemplateLibrary>.Instance);

    private static TemplateEditor NewEditor(int width = 1200, int height = 1800) => new(new TemplateDraft
    {
        Id = "editor-test",
        Name = "Thử Editor",
        Canvas = new TemplateCanvas { WidthPixels = width, HeightPixels = height, Dpi = 300 },
    });

    private static TemplateSlot SlotOf(TemplateEditor editor, EditorElement element) => editor.Current.Find(element.Key)!.Slot!;

    // ------------------------------------------------------------ 13. Canvas

    [Theory]
    [InlineData("4x6", 300, 1200, 1800)]
    [InlineData("6x4", 300, 1800, 1200)]
    [InlineData("2x6", 300, 600, 1800)]
    [InlineData("4x6", 150, 600, 900)]
    public void CanvasPreset_ComputesPixelsFromInchesAndDpi(string key, int dpi, int width, int height) =>
        Assert.Equal((width, height), CanvasPreset.Find(key)!.ToPixels(dpi));

    [Fact]
    public void NewCanvas_IsValidOnceItHasASlot()
    {
        var editor = NewEditor();
        Assert.Contains(editor.Validate().Errors, i => i.Path == "slots");

        editor.AddSlot();

        Assert.True(editor.Validate().IsValid);
        Assert.Equal(1200, editor.Current.ToTemplate(null).Canvas.WidthPixels);
    }

    [Fact]
    public void TemplateIds_SlugifyVietnameseNames_AndStayUnique()
    {
        Assert.Equal("dam-cuoi-hong-2026", TemplateIds.Slugify("  Đám cưới Hồng 2026!! "));
        Assert.Equal("template", TemplateIds.Slugify("***"));
        Assert.Equal("wedding-3", TemplateIds.MakeUnique("Wedding", id => id is "wedding" or "wedding-2"));
    }

    // ------------------------------------------------------------ 14–17. Ô ảnh, kéo, resize, zoom

    [Fact]
    public void AddSlot_PlacesCenteredSlotForNextPhoto()
    {
        var editor = NewEditor();

        var first = editor.AddSlot();
        var second = editor.AddSlot();

        Assert.Equal(0, first.Slot!.PhotoIndex);
        Assert.Equal(1, second.Slot!.PhotoIndex);
        Assert.Equal(new CanvasRect(360, 720, 480, 360), first.Bounds);
        Assert.NotEqual(first.ElementId, second.ElementId);
        Assert.Equal(second.Key, editor.SelectedKey);
    }

    [Fact]
    public void Drag_MoveAndResize_AreOneUndoStepEach_AndStoreCanvasCoordinates()
    {
        var editor = NewEditor();
        var slot = editor.AddSlot();

        editor.BeginDrag(slot.Key, DragHandle.Move);
        editor.DragTo(10, 10);
        editor.DragTo(100.4, -75.6);
        editor.EndDrag();
        Assert.Equal(new CanvasRect(460, 644, 480, 360), editor.Current.Find(slot.Key)!.Bounds);

        editor.BeginDrag(slot.Key, DragHandle.SouthEast);
        editor.DragTo(120, 40);
        editor.EndDrag();
        var saved = editor.Current.ToTemplate(null).Slots[0];
        Assert.Equal((460, 644, 600, 400), (saved.X, saved.Y, saved.Width, saved.Height));

        Assert.True(editor.Undo());
        Assert.Equal(new CanvasRect(460, 644, 480, 360), editor.Current.Find(slot.Key)!.Bounds);
        Assert.True(editor.Undo());
        Assert.Equal(new CanvasRect(360, 720, 480, 360), editor.Current.Find(slot.Key)!.Bounds);
    }

    [Theory]
    [InlineData(DragHandle.West, -50, 0, 50, 100, 150, 100)]
    [InlineData(DragHandle.North, 0, 30, 100, 130, 100, 70)]
    [InlineData(DragHandle.East, -500, 0, 100, 100, 1, 100)]
    [InlineData(DragHandle.NorthWest, 20, 20, 120, 120, 80, 80)]
    public void Resize_Handles_MoveOnlyTheirEdges_AndNeverInvert(DragHandle handle, double dx, double dy, int x, int y, int w, int h) =>
        Assert.Equal(new CanvasRect(x, y, w, h), CanvasGeometry.Apply(new CanvasRect(100, 100, 100, 100), handle, dx, dy));

    [Fact]
    public void Resize_KeepAspect_PreservesRatioFromCorner()
    {
        var rect = CanvasGeometry.Apply(new CanvasRect(0, 0, 400, 300), DragHandle.SouthEast, 200, 0, keepAspect: true);

        Assert.Equal(new CanvasRect(0, 0, 600, 450), rect);
    }

    [Fact]
    public void Snap_RoundsDraggedEdgesToGrid()
    {
        Assert.Equal(new CanvasRect(110, 90, 100, 100), CanvasGeometry.Apply(new CanvasRect(100, 100, 100, 100), DragHandle.Move, 12, -8, gridSize: 10));
        Assert.Equal(new CanvasRect(100, 100, 120, 100), CanvasGeometry.Apply(new CanvasRect(100, 100, 100, 100), DragHandle.East, 17, 0, gridSize: 10));
    }

    [Theory]
    [InlineData(1200, 400, 30, 90)]
    [InlineData(1200, 1200, 30, 30)]
    [InlineData(1200, 2400, 30, 15)]
    public void ScreenDelta_ConvertsToCanvasPixels_AtAnyZoom(int canvasWidth, double displayWidth, double screenDelta, double canvasDelta) =>
        Assert.Equal(canvasDelta, CanvasGeometry.ToCanvasDelta(screenDelta, displayWidth / canvasWidth), 9);

    [Fact]
    public void ScreenDelta_RejectsInvalidScale() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CanvasGeometry.ToCanvasDelta(10, 0));

    // ------------------------------------------------------------ 18–20. photoIndex

    [Fact]
    public void RequiredPhotoCount_ComesFromPhotoIndex_NotSlotCount()
    {
        var editor = NewEditor();
        var a = editor.AddSlot();
        editor.Select(a.Key);
        editor.DuplicateSelected();
        var c = editor.AddSlot();
        editor.Select(c.Key);
        editor.DuplicateSelected();

        Assert.Equal(4, editor.Current.Slots.Count());
        Assert.Equal([0, 0, 1, 1], editor.Current.ToTemplate(null).Slots.Select(s => s.PhotoIndex));
        Assert.Equal(2, editor.Current.RequiredPhotoCount);
        Assert.True(editor.Validate().IsValid);
    }

    [Fact]
    public void MissingIntermediatePhotoIndex_IsDetected_AndRenumberKeepsSharing()
    {
        var editor = NewEditor();
        var a = editor.AddSlot();
        var b = editor.AddSlot();
        editor.Select(b.Key);
        editor.DuplicateSelected();
        editor.UpdateSlot(a.Key, s => s with { PhotoIndex = 0 });
        foreach (var slot in editor.Current.Slots.Where(s => s.Key != a.Key).ToArray())
        {
            editor.UpdateSlot(slot.Key, s => s with { PhotoIndex = 2 });
        }

        Assert.Contains(editor.Validate().Errors, i => i.Message.Contains("photoIndex 1", StringComparison.Ordinal));

        Assert.True(editor.RenumberPhotoIndexes());

        Assert.Equal([0, 1, 1], editor.Current.ToTemplate(null).Slots.Select(s => s.PhotoIndex));
        Assert.True(editor.Validate().IsValid);
    }

    // ------------------------------------------------------------ 21–25. Thuộc tính và trang trí

    [Fact]
    public void FitFocalPointAndCornerRadius_AreSavedOnTheSlot()
    {
        var editor = NewEditor();
        var slot = editor.AddSlot();

        editor.UpdateSlot(slot.Key, s => s with { Fit = ImageFit.Cover, FocalPoint = new FocalPoint { X = 0.5, Y = 0.4 }, CornerRadius = 24 });

        var saved = editor.Current.ToTemplate(null).Slots[0];
        Assert.Equal(ImageFit.Cover, saved.Fit);
        Assert.Equal(new FocalPoint { X = 0.5, Y = 0.4 }, saved.FocalPoint);
        Assert.Equal(24, saved.CornerRadius);

        editor.UpdateSlot(slot.Key, s => s with { FocalPoint = new FocalPoint { X = 1.5, Y = 0.4 } });
        Assert.Contains(editor.Validate().Errors, i => i.Path.EndsWith("focalPoint.x", StringComparison.Ordinal));
    }

    [Fact]
    public void TextRectangleAndImage_AreSavedAsEngineDecorations()
    {
        var editor = NewEditor();
        editor.AddSlot();
        var text = editor.AddText();
        editor.UpdateDecoration(text.Key, d => d with { Text = "Ngày {date}", FontFamily = "Arial", FontSizePixels = 64, Bold = true, Color = "#FF0000", TextAlign = TextAlignment.Left });
        var rect = editor.AddRectangle();
        editor.UpdateDecoration(rect.Key, d => d with { Color = "#80000000", CornerRadius = 12 });
        var image = editor.AddImage("assets/logo.png", 800, 400);

        var decorations = editor.Current.ToTemplate(null).Decorations;

        Assert.Equal([DecorationKind.Text, DecorationKind.Rectangle, DecorationKind.Image], decorations.Select(d => d.Type));
        Assert.Equal(("Ngày {date}", "Arial", 64, true, "#FF0000", TextAlignment.Left), (decorations[0].Text, decorations[0].FontFamily, decorations[0].FontSizePixels, decorations[0].Bold, decorations[0].Color, decorations[0].TextAlign));
        Assert.Equal(("#80000000", 12), (decorations[1].Color, decorations[1].CornerRadius));
        Assert.Equal("assets/logo.png", decorations[2].AssetPath);
        Assert.Equal((600, 300), (decorations[2].Width, decorations[2].Height));
        Assert.Throws<ArgumentException>(() => editor.AddImage("../outside.png", 10, 10));
    }

    [Fact]
    public void LayerOrder_MatchesPhotoComposerRenderOrder_AfterMovesAndZIndexEdits()
    {
        var editor = NewEditor();
        var slotA = editor.AddSlot();
        var overlay = editor.AddRectangle();
        var logo = editor.AddText();
        var slotB = editor.AddSlot();

        // Ô mới nằm ngay trên ô ảnh cao nhất (dưới khung phủ).
        Assert.Equal([slotA.Key, slotB.Key, overlay.Key, logo.Key], editor.Current.Layers.Select(l => l.Key));

        Assert.True(editor.MoveLayer(slotB.Key, +1));
        Assert.True(editor.MoveLayer(logo.Key, -1));
        Assert.True(editor.MoveLayer(slotA.Key, -1) == false);

        Assert.Equal([slotA.Key, overlay.Key, logo.Key, slotB.Key], editor.Current.Layers.Select(l => l.Key));
        AssertRenderOrderMatches(editor);

        Assert.True(editor.SetZIndex(overlay.Key, -100));
        Assert.Equal(overlay.Key, editor.Current.Layers[0].Key);
        Assert.Equal(-100, editor.Current.Find(overlay.Key)!.ZIndex);
        AssertRenderOrderMatches(editor);
    }

    [Fact]
    public void OpeningExistingTemplate_KeepsZIndexValues()
    {
        var file = Path.Combine(TestTemplates.ShippedTemplatesDirectory, "party-overlay", "template.json");
        var (template, _) = JsonTemplateCatalog.LoadTemplateFile(file);

        var draft = TemplateDraft.FromTemplate(template!);
        var back = draft.ToTemplate(template!.AssetDirectory);

        Assert.Equal(template.Slots, back.Slots);
        Assert.Equal(template.Decorations.OrderBy(d => d.ZIndex), back.Decorations);
        Assert.Equal(
            TemplateLayers.GetRenderOrder(template).Select(e => (object?)e.Slot ?? e.Decoration),
            TemplateLayers.GetRenderOrder(back).Select(e => (object?)e.Slot ?? e.Decoration));
    }

    private static void AssertRenderOrderMatches(TemplateEditor editor)
    {
        var template = editor.Current.ToTemplate(null);
        var rendered = TemplateLayers.GetRenderOrder(template).Select(e => (object?)e.Slot ?? e.Decoration).ToArray();
        var layers = editor.Current.Layers.Select(l => (object?)l.Slot ?? l.Decoration).ToArray();
        Assert.Equal(layers, rendered);
        Assert.True(TemplateValidator.Validate(template).IsValid);
    }

    // ------------------------------------------------------------ 26. Undo/Redo

    [Fact]
    public void UndoRedo_RestoresStates_AndTracksUnsavedChanges()
    {
        var editor = NewEditor();
        Assert.False(editor.HasUnsavedChanges);

        var slot = editor.AddSlot();
        editor.UpdateSlot(slot.Key, s => s with { CornerRadius = 30 });
        Assert.True(editor.HasUnsavedChanges);

        Assert.True(editor.Undo());
        Assert.Equal(0, SlotOf(editor, slot).CornerRadius);
        Assert.True(editor.Undo());
        Assert.Empty(editor.Current.Layers);
        Assert.False(editor.HasUnsavedChanges);
        Assert.False(editor.Undo());

        Assert.True(editor.Redo());
        Assert.True(editor.Redo());
        Assert.Equal(30, SlotOf(editor, slot).CornerRadius);

        editor.Select(slot.Key);
        Assert.True(editor.DeleteSelected());
        Assert.False(editor.CanRedo);
        Assert.Empty(editor.Current.Layers);
    }

    [Fact]
    public void NoOpChanges_DoNotCreateUndoSteps()
    {
        var editor = NewEditor();
        var slot = editor.AddSlot();
        editor.Undo();
        editor.Redo();

        Assert.False(editor.UpdateSlot(slot.Key, s => s with { }));
        editor.BeginDrag(slot.Key, DragHandle.Move);
        editor.DragTo(0, 0);
        editor.EndDrag();

        Assert.True(editor.Undo());
        Assert.False(editor.CanUndo);
    }

    // ------------------------------------------------------------ 27. Lưu và mở lại

    [Fact]
    public async Task SaveAndReopen_KeepsEveryProperty()
    {
        var library = CreateLibrary();
        var entry = await library.CreateAsync(new NewTemplateRequest("Lưu thử", null, 1200, 1800, 300, "#FFFFFF"));
        var editor = new TemplateEditor(TemplateDraft.FromTemplate(entry.Template!));
        var png = Path.Combine(_temp.Path, "logo.png");
        await File.WriteAllBytesAsync(png, TestImages.CreateHalfTransparentPng(200, 100, SKColors.Red));
        var asset = await library.AddAssetAsync(entry.Key, png);

        var slot = editor.AddSlot();
        editor.UpdateSlot(slot.Key, s => s with { Fit = ImageFit.Contain, BackgroundColor = "#EEEEEE", BorderWidth = 4, BorderColor = "#000000" });
        var text = editor.AddText();
        editor.UpdateDecoration(text.Key, d => d with { Text = "{datetime}" });
        editor.AddRectangle(ellipse: true);
        editor.AddImage(asset.AssetPath, asset.WidthPixels, asset.HeightPixels);
        var expected = editor.Current.ToTemplate(null);

        var result = await library.SaveAsync(entry.Key, expected);
        Assert.True(result.Succeeded, string.Join(" ", result.Issues));
        editor.MarkSaved();
        Assert.False(editor.HasUnsavedChanges);

        var reloaded = (await CreateLibrary().FindAsync(entry.Key))!.Template!;
        Assert.Equal(expected.Slots, reloaded.Slots);
        Assert.Equal(expected.Decorations, reloaded.Decorations);
        Assert.Equal(expected.Canvas, reloaded.Canvas);
        Assert.Equal(TemplateDraft.FromTemplate(reloaded).Layers.Select(l => (object?)l.Slot ?? l.Decoration), editor.Current.Layers.Select(l => (object?)l.Slot ?? l.Decoration));
    }

    // ------------------------------------------------------------ 28. Xem thử bằng Photo Composer

    [Fact]
    public async Task Preview_IsRenderedByPhotoComposer_IdenticalToRealComposition()
    {
        var template = new PhotoTemplate
        {
            Id = "preview-check",
            Name = "Xem thử",
            Canvas = new TemplateCanvas { WidthPixels = 600, HeightPixels = 400, Dpi = 100, BackgroundColor = "#123456" },
            Slots =
            [
                new TemplateSlot { PhotoIndex = 0, X = 20, Y = 20, Width = 260, Height = 360, CornerRadius = 30, FocalPoint = new FocalPoint { X = 0.3, Y = 0.5 } },
                new TemplateSlot { PhotoIndex = 1, X = 320, Y = 20, Width = 260, Height = 200, Fit = ImageFit.Contain, BackgroundColor = "#FFFFFF" },
            ],
            Decorations = [new TemplateDecoration { Type = DecorationKind.Rectangle, X = 300, Y = 250, Width = 280, Height = 120, Color = "#80FF0000", CornerRadius = 20, ZIndex = 5 }],
        };

        var preview = await _composer.RenderTemplateSampleAsync(template, TemplateSampleContent.SamplePhotos, 600);

        // Cùng ảnh mẫu đưa qua luồng ghép ảnh thật (ComposeAsync) → phải giống từng điểm ảnh.
        var photos = new List<CompositionPhoto>();
        for (var i = 0; i < 2; i++)
        {
            using var image = TemplateSamplePhotos.Create(i);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            var file = Path.Combine(_paths.RootDirectory, $"sample-{i}.png");
            Directory.CreateDirectory(_paths.RootDirectory);
            await File.WriteAllBytesAsync(file, data.ToArray());
            photos.Add(new CompositionPhoto(file, false));
        }

        var composed = await _composer.ComposeAsync(new CompositionRequest(Guid.NewGuid(), template, photos, ColorFilter.None, DateTimeOffset.Now));

        using var expected = SKBitmap.Decode(composed.FilePath);
        using var actual = SKBitmap.Decode(preview);
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        for (var y = 0; y < expected.Height; y += 7)
        {
            for (var x = 0; x < expected.Width; x += 7)
            {
                Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public async Task Preview_RejectsInvalidTemplate_InsteadOfRenderingWrongly()
    {
        var editor = NewEditor();

        await Assert.ThrowsAsync<CompositionException>(() =>
            _composer.RenderTemplateSampleAsync(editor.Current.ToTemplate(null), TemplateSampleContent.SamplePhotos, 400));
    }

    // ------------------------------------------------------------ 29. Dùng được trong luồng Kiosk

    [Fact]
    public async Task TemplateFromEditor_CanBeCapturedAndExported_ThroughKioskFlow()
    {
        var library = CreateLibrary();
        var created = await library.CreateAsync(new NewTemplateRequest("Kiosk thử", null, 600, 900, 150, "#FFFFFF"));
        var editor = new TemplateEditor(TemplateDraft.FromTemplate(created.Template!));
        var second = editor.AddSlot();
        editor.Select(second.Key);
        editor.DuplicateSelected();
        editor.AddText();
        Assert.True((await library.SaveAsync(created.Key, editor.Current.ToTemplate(null))).Succeeded);

        Assert.DoesNotContain(await library.GetTemplatesAsync(), t => t.Id == created.Key);
        await library.SetEnabledAsync(created.Key, true);
        var template = Assert.Single(await library.GetTemplatesAsync(), t => t.Id == created.Key);
        Assert.Equal(2, template.RequiredPhotoCount);

        var workspaces = TestSessions.OpenWorkspace(_paths, Path.Combine(_temp.Path, "Sự kiện"));
        var sessions = TestSessions.Create(_paths, workspaces);
        var camera = new FakeCameraDevice { NextFrame = _ => new CapturedFrame(TestImages.CreateQuadrants(), JpegImageInfo.ContentType) };
        Assert.NotNull(sessions.StartNewSession());
        Assert.True(sessions.SelectTemplate(template));
        sessions.MarkPreviewReady();
        Assert.True((await sessions.CaptureSequenceAsync(camera, null)).Succeeded);
        Assert.Equal(2, camera.CaptureCount);
        Assert.Equal(CompositionOutcome.Succeeded, await sessions.ComposeAsync());

        var output = Path.Combine(_temp.Path, "Xuất");
        Directory.CreateDirectory(output);
        var results = await sessions.ExportAsync([ExportFormat.Jpeg, ExportFormat.Pdf], output);
        Assert.All(results, r => Assert.True(r.Result.Succeeded));
        Assert.Equal(2, Directory.GetFiles(output).Length);
    }

    // ------------------------------------------------------------ 30. Manual Crop không ghi vào template

    [Fact]
    public async Task ManualCropInSession_NeverChangesLibraryTemplateJson()
    {
        var library = CreateLibrary();
        var created = await library.CreateAsync(new NewTemplateRequest("Crop thử", null, 600, 900, 150, "#FFFFFF"));
        await library.SetEnabledAsync(created.Key, true);
        var file = Path.Combine(library.RootDirectory, created.Key, "template.json");
        var before = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file)));
        var template = (await library.GetTemplatesAsync()).Single(t => t.Id == created.Key);

        var workspaces = TestSessions.OpenWorkspace(_paths, Path.Combine(_temp.Path, "Sự kiện"));
        var sessions = TestSessions.Create(_paths, workspaces);
        var camera = new FakeCameraDevice { NextFrame = _ => new CapturedFrame(TestImages.CreateQuadrants(), JpegImageInfo.ContentType) };
        Assert.NotNull(sessions.StartNewSession());
        Assert.True(sessions.SelectTemplate(template));
        sessions.MarkPreviewReady();
        Assert.True((await sessions.CaptureSequenceAsync(camera, null)).Succeeded);
        Assert.Equal(CompositionOutcome.Succeeded, await sessions.ComposeAsync());
        Assert.Equal(CompositionOutcome.Succeeded, await sessions.ApplySlotCropAsync(0, new SlotCrop(2, 0.2, 0.8)));
        Assert.True(await sessions.CompleteSessionAsync());
        await sessions.FlushAsync();

        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file))));
        Assert.Equal(template.Slots, (await CreateLibrary().FindAsync(created.Key))!.Template!.Slots);
    }
}
