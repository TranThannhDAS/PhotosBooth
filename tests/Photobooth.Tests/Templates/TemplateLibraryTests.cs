using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Camera;
using Photobooth.Application.Composition;
using Photobooth.Application.Photos;
using Photobooth.Application.Sessions;
using Photobooth.Application.Templates;
using Photobooth.Infrastructure.Imaging;
using Photobooth.Infrastructure.Storage;
using Photobooth.Infrastructure.Templates;
using Photobooth.Tests.TestSupport;
using SkiaSharp;

namespace Photobooth.Tests.Templates;

/// <summary>Phase 3.1 — Template Manager (Template Library dùng chung, tách khỏi Event Workspace).</summary>
public sealed class TemplateLibraryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PhotoboothStoragePaths _paths;
    private readonly string _bundled;

    public TemplateLibraryTests()
    {
        _paths = new PhotoboothStoragePaths(Path.Combine(_temp.Path, "AppData"));
        _bundled = Path.Combine(_temp.Path, "Bundled");
        TestTemplateFiles.Write(_bundled, "alpha", TestTemplateFiles.ValidJson("alpha"));
        TestTemplateFiles.Write(_bundled, "beta", WithLogoJson("beta"));
        TestTemplateFiles.WritePng(Path.Combine(_bundled, "beta"), "assets/logo.png");
    }

    public void Dispose() => _temp.Dispose();

    private string LibraryRoot => Path.Combine(_paths.RootDirectory, "Templates");

    private TemplateLibrary CreateLibrary() =>
        new(LibraryRoot, _bundled, new SkiaPhotoComposer(_paths, NullLogger<SkiaPhotoComposer>.Instance), NullLogger<TemplateLibrary>.Instance);

    private static string WithLogoJson(string id) => $$"""
        {
          "id": "{{id}}", "name": "Có logo",
          "canvas": { "widthPixels": 300, "heightPixels": 300 },
          "slots": [ { "photoIndex": 0, "x": 0, "y": 0, "width": 200, "height": 200 } ],
          "decorations": [ { "type": "image", "assetPath": "assets/logo.png", "x": 210, "y": 210, "width": 80, "height": 80 } ]
        }
        """;

    private static NewTemplateRequest NewRequest(string name = "Đám cưới Hồng") => new(name, "Mô tả", 1200, 1800, 300, "#FFF0F5");

    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    // ------------------------------------------------------------ 1. Đọc danh sách

    [Fact]
    public async Task List_FirstRun_SeedsBundledTemplates_AndReportsInvalidOnes()
    {
        TestTemplateFiles.Write(_bundled, "broken", """{ "id": "broken", "name": "Lỗi", "canvas": { "widthPixels": 10, "heightPixels": 10 }, "slots": [] }""");
        var library = CreateLibrary();

        var entries = await library.ListAsync();

        Assert.Equal(["alpha", "beta", "broken"], entries.Select(e => e.Key));
        Assert.True(entries[0].IsValid && entries[0].Enabled);
        Assert.False(entries[2].IsValid);
        Assert.Contains(entries[2].Issues, i => i.Severity == ValidationSeverity.Error);
        Assert.Equal(["alpha", "beta"], (await library.GetTemplatesAsync()).Select(t => t.Id));
        Assert.True(File.Exists(Path.Combine(LibraryRoot, "beta", "assets", "logo.png")));
    }

    [Fact]
    public async Task List_ExistingLibrary_IsNeverReseededOrOverwritten()
    {
        var library = CreateLibrary();
        await library.DeleteAsync("alpha");

        var again = CreateLibrary();

        Assert.Equal(["beta"], (await again.ListAsync()).Select(e => e.Key));
    }

    // ------------------------------------------------------------ 2. Tạo template

    [Fact]
    public async Task Create_WritesValidMinimalTemplate_DisabledByDefault()
    {
        var library = CreateLibrary();

        var entry = await library.CreateAsync(NewRequest());

        Assert.Equal("dam-cuoi-hong", entry.Key);
        var template = Assert.IsType<PhotoTemplate>(entry.Template);
        Assert.Equal("Đám cưới Hồng", template.Name);
        Assert.Equal((1200, 1800, 300), (template.Canvas.WidthPixels, template.Canvas.HeightPixels, template.Canvas.Dpi));
        Assert.Equal(1, template.RequiredPhotoCount);
        Assert.True(TemplateValidator.Validate(template).IsValid);
        Assert.False(entry.Enabled);
        Assert.DoesNotContain(await library.GetTemplatesAsync(), t => t.Id == entry.Key);
        Assert.True(File.Exists(Path.Combine(LibraryRoot, entry.Key, SkiaPhotoComposer.ThumbnailFileName)));

        // Tên tiếng Việt được giữ nguyên trong file (không escape).
        Assert.Contains("Đám cưới Hồng", await File.ReadAllTextAsync(Path.Combine(LibraryRoot, entry.Key, "template.json")));
    }

    [Fact]
    public async Task Create_InvalidCanvas_Throws_AndWritesNothing()
    {
        var library = CreateLibrary();

        await Assert.ThrowsAsync<TemplateLibraryException>(() => library.CreateAsync(NewRequest() with { WidthPixels = 20000 }));
        await Assert.ThrowsAsync<TemplateLibraryException>(() => library.CreateAsync(NewRequest(" ")));

        Assert.Equal(["alpha", "beta"], (await library.ListAsync()).Select(e => e.Key));
    }

    // ------------------------------------------------------------ 3. Sao chép

    [Fact]
    public async Task Duplicate_CopiesJsonAndAssets_WithNewId_OriginalUnchanged()
    {
        var library = CreateLibrary();
        await library.ListAsync();
        var originalFile = Path.Combine(LibraryRoot, "beta", "template.json");
        var before = Hash(originalFile);

        var copy = await library.DuplicateAsync("beta");

        Assert.Equal("beta-ban-sao", copy.Key);
        Assert.Equal("beta-ban-sao", copy.Template!.Id);
        Assert.Equal("Có logo (bản sao)", copy.Template.Name);
        Assert.Equal(before, Hash(originalFile));
        var copiedAsset = Path.Combine(LibraryRoot, "beta-ban-sao", "assets", "logo.png");
        Assert.True(File.Exists(copiedAsset));

        // Asset là bản riêng: xóa bản gốc không làm hỏng bản sao.
        await library.DeleteAsync("beta");
        var entry = await library.FindAsync("beta-ban-sao");
        Assert.True(entry!.IsValid);
        Assert.Equal(Path.Combine(LibraryRoot, "beta-ban-sao"), entry.Template!.AssetDirectory);
    }

    // ------------------------------------------------------------ 4. Đổi tên

    [Fact]
    public async Task Rename_ChangesNameOnly_KeepsIdAndLayout()
    {
        var library = CreateLibrary();
        var before = (await library.FindAsync("alpha"))!.Template!;

        var renamed = await library.RenameAsync("alpha", "  Tên mới  ", "Mô tả mới");

        Assert.Equal("Tên mới", renamed.Template!.Name);
        Assert.Equal("Mô tả mới", renamed.Template.Description);
        Assert.Equal("alpha", renamed.Template.Id);
        Assert.Equal(before.Slots, renamed.Template.Slots);
        await Assert.ThrowsAsync<TemplateLibraryException>(() => library.RenameAsync("alpha", "", null));
    }

    // ------------------------------------------------------------ 5. Xóa

    [Fact]
    public async Task Delete_RemovesOnlyThatTemplate_AndItsEnabledState()
    {
        var library = CreateLibrary();
        await library.SetEnabledAsync("alpha", false);

        await library.DeleteAsync("alpha");

        Assert.False(Directory.Exists(Path.Combine(LibraryRoot, "alpha")));
        Assert.True((await library.FindAsync("beta"))!.IsValid);
        Assert.DoesNotContain("alpha", await File.ReadAllTextAsync(Path.Combine(LibraryRoot, TemplateLibrary.StateFileName)));
        await Assert.ThrowsAsync<TemplateLibraryException>(() => library.DeleteAsync("alpha"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../AppData")]
    [InlineData("C:\\Windows")]
    [InlineData(".staging")]
    public async Task Delete_UnsafeKey_IsRejected(string key)
    {
        var library = CreateLibrary();
        await library.ListAsync();

        await Assert.ThrowsAsync<TemplateLibraryException>(() => library.DeleteAsync(key));
        Assert.True(Directory.Exists(LibraryRoot));
    }

    // ------------------------------------------------------------ 6–9. Nhập

    [Fact]
    public async Task Import_ValidFolder_CopiesTemplateAndAssets()
    {
        var library = CreateLibrary();
        var source = Path.Combine(_temp.Path, "Incoming");
        TestTemplateFiles.Write(source, "my-template", WithLogoJson("gamma"));
        TestTemplateFiles.WritePng(Path.Combine(source, "my-template"), "assets/logo.png");

        var result = await library.ImportAsync(Path.Combine(source, "my-template"));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("gamma", result.Entry!.Key);
        Assert.True(result.Entry.IsValid);
        Assert.False(result.Entry.Enabled);
        Assert.True(File.Exists(Path.Combine(LibraryRoot, "gamma", "assets", "logo.png")));
    }

    [Fact]
    public async Task Import_InvalidJson_ReportsIssues_AndWritesNothing()
    {
        var library = CreateLibrary();
        var folder = Path.GetDirectoryName(TestTemplateFiles.Write(_temp.Path, "bad",
            """{ "id": "bad", "name": "Lỗi", "canvas": { "widthPixels": 100, "heightPixels": 100 }, "slots": [ { "photoIndex": 0, "x": 0, "y": 0, "width": 10, "height": 10 }, { "photoIndex": 2, "x": 0, "y": 0, "width": 10, "height": 10 } ] }"""))!;

        var result = await library.ImportAsync(folder);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, i => i.Message.Contains("photoIndex 1", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(LibraryRoot, "bad")));
    }

    [Fact]
    public async Task Import_MissingAsset_IsRejectedWithClearError()
    {
        var library = CreateLibrary();
        var folder = Path.GetDirectoryName(TestTemplateFiles.Write(_temp.Path, "no-logo", WithLogoJson("no-logo")))!;

        var result = await library.ImportAsync(folder);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, i => i.Path.EndsWith(".assetPath", StringComparison.Ordinal) && i.Message.Contains("assets/logo.png", StringComparison.Ordinal));
        Assert.Equal(["alpha", "beta"], (await library.ListAsync()).Select(e => e.Key));
    }

    [Fact]
    public async Task Import_DuplicateId_GetsNewId_ExistingTemplateNotOverwritten()
    {
        var library = CreateLibrary();
        await library.ListAsync();
        var existing = Hash(Path.Combine(LibraryRoot, "alpha", "template.json"));
        var folder = Path.GetDirectoryName(TestTemplateFiles.Write(_temp.Path, "other", TestTemplateFiles.ValidJson("alpha")))!;

        var result = await library.ImportAsync(folder);

        Assert.True(result.Succeeded);
        Assert.Equal("alpha-2", result.Entry!.Key);
        Assert.Equal("alpha-2", result.Entry.Template!.Id);
        Assert.Contains("alpha-2", result.Message, StringComparison.Ordinal);
        Assert.Equal(existing, Hash(Path.Combine(LibraryRoot, "alpha", "template.json")));
    }

    // ------------------------------------------------------------ 10. Xuất và nhập lại

    [Fact]
    public async Task Export_ThenImport_RoundTripsTemplateAndAssets()
    {
        var library = CreateLibrary();
        await library.ListAsync();
        var zip = Path.Combine(_temp.Path, "beta.zip");

        await library.ExportAsync("beta", zip);
        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.Contains(archive.Entries, e => e.FullName == "template.json");
            Assert.Contains(archive.Entries, e => e.FullName == "assets/logo.png");
        }

        var result = await library.ImportAsync(zip);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("beta-2", result.Entry!.Key);
        var original = (await library.FindAsync("beta"))!.Template!;
        Assert.Equal(original.Slots, result.Entry.Template!.Slots);
        Assert.Equal(original.Decorations, result.Entry.Template.Decorations);
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(LibraryRoot, "beta", "assets", "logo.png")),
            await File.ReadAllBytesAsync(Path.Combine(LibraryRoot, "beta-2", "assets", "logo.png")));
        Assert.False(Directory.Exists(Path.Combine(LibraryRoot, ".staging")));
    }

    [Fact]
    public async Task Import_ZipWithPathTraversal_IsRejected_AndNothingWrittenOutside()
    {
        var library = CreateLibrary();
        await library.ListAsync();
        var zip = Path.Combine(_temp.Path, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("template.json").Open()))
            {
                writer.Write(TestTemplateFiles.ValidJson("evil"));
            }

            using (var writer = new StreamWriter(archive.CreateEntry("../../escaped.txt").Open()))
            {
                writer.Write("x");
            }
        }

        var result = await library.ImportAsync(zip);

        Assert.False(result.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(LibraryRoot, "evil")));
        Assert.Empty(Directory.GetFiles(_temp.Path, "escaped.txt", SearchOption.AllDirectories));
    }

    // ------------------------------------------------------------ 11. Bật/tắt

    [Fact]
    public async Task SetEnabled_HidesFromKiosk_ButKeepsInManager_AndSurvivesRestart()
    {
        var library = CreateLibrary();

        await library.SetEnabledAsync("alpha", false);

        Assert.Equal(["beta"], (await library.GetTemplatesAsync()).Select(t => t.Id));
        Assert.Contains(await library.ListAsync(), e => e.Key == "alpha" && !e.Enabled && e.IsValid);

        var restarted = CreateLibrary();
        Assert.Equal(["beta"], (await restarted.GetTemplatesAsync()).Select(t => t.Id));
        await restarted.SetEnabledAsync("alpha", true);
        Assert.Equal(["alpha", "beta"], (await restarted.GetTemplatesAsync()).Select(t => t.Id));
    }

    // ------------------------------------------------------------ Lưu từ Editor

    [Fact]
    public async Task Save_InvalidTemplate_DoesNotOverwriteValidFile()
    {
        var library = CreateLibrary();
        var entry = (await library.FindAsync("alpha"))!;
        var file = Path.Combine(LibraryRoot, "alpha", "template.json");
        var before = Hash(file);

        var broken = entry.Template! with { Slots = [entry.Template.Slots[0] with { PhotoIndex = 1 }] };
        var result = await library.SaveAsync("alpha", broken);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, i => i.Message.Contains("photoIndex 0", StringComparison.Ordinal));
        Assert.Equal(before, Hash(file));
    }

    [Fact]
    public async Task Save_CannotChangeId()
    {
        var library = CreateLibrary();
        var entry = (await library.FindAsync("alpha"))!;

        var result = await library.SaveAsync("alpha", entry.Template! with { Id = "beta" });

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AddAsset_CopiesWithUniqueName_AndRejectsNonImages()
    {
        var library = CreateLibrary();
        await library.ListAsync();
        var png = Path.Combine(_temp.Path, "Logo Mới.png");
        await File.WriteAllBytesAsync(png, TestImages.CreateHalfTransparentPng(40, 20, SKColors.Red));

        var first = await library.AddAssetAsync("alpha", png);
        var second = await library.AddAssetAsync("alpha", png);

        Assert.StartsWith("assets/logo-moi-", first.AssetPath, StringComparison.Ordinal);
        Assert.NotEqual(first.AssetPath, second.AssetPath);
        Assert.Equal((40, 20), (first.WidthPixels, first.HeightPixels));
        Assert.Equal(await File.ReadAllBytesAsync(png), await library.ReadAssetAsync("alpha", first.AssetPath));

        var text = Path.Combine(_temp.Path, "note.png");
        await File.WriteAllTextAsync(text, "không phải ảnh");
        await Assert.ThrowsAsync<TemplateLibraryException>(() => library.AddAssetAsync("alpha", text));
        await Assert.ThrowsAsync<TemplateLibraryException>(() => library.ReadAssetAsync("alpha", "../beta/template.json"));
    }

    [Fact]
    public async Task RemoveUnusedAddedAssets_DeletesOnlyNewUnreferencedFiles()
    {
        var library = CreateLibrary();
        await library.ListAsync();
        var png = Path.Combine(_temp.Path, "sticker.png");
        await File.WriteAllBytesAsync(png, TestImages.CreateSolid(10, 10, SKColors.Blue));
        var added = await library.AddAssetAsync("beta", png);

        await library.RemoveUnusedAddedAssetsAsync("beta", [added.AssetPath, "assets/logo.png"]);

        Assert.False(File.Exists(Path.Combine(LibraryRoot, "beta", added.AssetPath)));
        Assert.True(File.Exists(Path.Combine(LibraryRoot, "beta", "assets", "logo.png")));
    }

    // ------------------------------------------------------------ 12. Phiên cũ không đổi

    [Fact]
    public async Task EditingLibraryTemplate_DoesNotChangeArchivedSession()
    {
        var library = CreateLibrary();
        var template = (await library.FindAsync("beta"))!.Template!;
        var eventFolder = Path.Combine(_temp.Path, "Sự kiện");
        var workspaces = TestSessions.OpenWorkspace(_paths, eventFolder);
        var sessions = TestSessions.Create(_paths, workspaces);
        var camera = new FakeCameraDevice { NextFrame = _ => new CapturedFrame(TestImages.CreateQuadrants(), JpegImageInfo.ContentType) };

        Assert.NotNull(sessions.StartNewSession());
        Assert.True(sessions.SelectTemplate(template));
        sessions.MarkPreviewReady();
        Assert.True((await sessions.CaptureSequenceAsync(camera, null)).Succeeded);
        Assert.Equal(CompositionOutcome.Succeeded, await sessions.ComposeAsync());
        Assert.True(await sessions.CompleteSessionAsync());
        var directory = sessions.Current!.Directory!;
        await sessions.FlushAsync();
        var snapshot = Path.Combine(directory, "Template", "template.json");
        var snapshotHash = Hash(snapshot);
        var outputHash = Hash(sessions.Current.Versions[^1].FilePath);

        // Quản trị viên sửa (dời ô ảnh) rồi xóa asset bằng cách xóa hẳn template trong thư viện.
        var changed = template with { Slots = [template.Slots[0] with { X = 50 }] };
        Assert.True((await library.SaveAsync("beta", changed)).Succeeded);
        await library.DeleteAsync("beta");

        var archive = TestSessions.CreateArchive(_paths, workspaces);
        var reopened = await archive.LoadSessionAsync(Path.GetFileName(directory));

        Assert.Equal(snapshotHash, Hash(snapshot));
        Assert.Equal(outputHash, Hash(reopened.Session.Versions[^1].FilePath));
        Assert.Equal(0, reopened.Session.Template!.Slots[0].X);
        Assert.True(reopened.Session.CanEdit, reopened.EditUnavailableReason);
    }
}
