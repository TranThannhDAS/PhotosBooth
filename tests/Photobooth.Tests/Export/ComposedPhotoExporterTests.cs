using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Photobooth.Application.Export;
using Photobooth.Application.Templates;
using Photobooth.Infrastructure.Export;
using Photobooth.Tests.TestSupport;
using SkiaSharp;

namespace Photobooth.Tests.Export;

public sealed class ComposedPhotoExporterTests : IDisposable
{
    private static readonly TemplateCanvas FourBySix = new() { WidthPixels = 1200, HeightPixels = 1800, Dpi = 300 };

    private readonly TempDirectory _temp = new();
    private readonly ComposedPhotoExporter _exporter = new(new ExportOptions(), NullLogger<ComposedPhotoExporter>.Instance);
    private readonly string _composed;
    private readonly string _outputDirectory;

    public ComposedPhotoExporterTests()
    {
        _composed = Path.Combine(_temp.Path, "composed.png");
        File.WriteAllBytes(_composed, TestImages.CreateSolid(1200, 1800, new SKColor(30, 120, 200)));
        _outputDirectory = Path.Combine(_temp.Path, "out");
        Directory.CreateDirectory(_outputDirectory);
    }

    public void Dispose() => _temp.Dispose();

    private Task<ExportResult> Export(ExportFormat format, string? directory = null, string baseName = "Photobooth_test") =>
        _exporter.ExportAsync(new ExportRequest(_composed, format, directory ?? _outputDirectory, baseName, FourBySix));

    [Fact]
    public async Task ExportAsync_Jpeg_CreatesDecodableJpegWithSameSize()
    {
        var result = await Export(ExportFormat.Jpeg);

        Assert.True(result.Succeeded);
        Assert.EndsWith("Photobooth_test.jpg", result.FilePath);
        var bytes = await File.ReadAllBytesAsync(result.FilePath!);
        Assert.Equal([0xFF, 0xD8], bytes[..2]);
        Assert.Equal((1200, 1800), TestImages.SizeOf(bytes));
    }

    [Fact]
    public async Task ExportAsync_Png_CopiesComposedImageUnchanged()
    {
        var result = await Export(ExportFormat.Png);

        Assert.True(result.Succeeded);
        Assert.Equal(await File.ReadAllBytesAsync(_composed), await File.ReadAllBytesAsync(result.FilePath!));
    }

    [Fact]
    public async Task ExportAsync_Pdf_PageSizeIsFourBySixInches()
    {
        var result = await Export(ExportFormat.Pdf);

        Assert.True(result.Succeeded);
        var text = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(result.FilePath!));
        Assert.StartsWith("%PDF", text);
        Assert.Matches(new Regex(@"/MediaBox\s*\[\s*0\s+0\s+288\s+432\s*\]"), text);
    }

    [Fact]
    public async Task ExportAsync_ExistingFile_IsNeverOverwritten()
    {
        var existing = Path.Combine(_outputDirectory, "Photobooth_test.jpg");
        byte[] original = [1, 2, 3];
        await File.WriteAllBytesAsync(existing, original);

        var second = await Export(ExportFormat.Jpeg);
        var third = await Export(ExportFormat.Jpeg);

        Assert.EndsWith("Photobooth_test_2.jpg", second.FilePath);
        Assert.EndsWith("Photobooth_test_3.jpg", third.FilePath);
        Assert.Equal(original, await File.ReadAllBytesAsync(existing));
        Assert.Empty(Directory.GetFiles(_outputDirectory, "*.tmp"));
    }

    [Fact]
    public async Task ExportAsync_MissingDirectory_ReturnsDirectoryNotFound()
    {
        var result = await Export(ExportFormat.Png, Path.Combine(_temp.Path, "missing"));

        Assert.Equal(ExportError.DirectoryNotFound, result.Error);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("")]
    public async Task ExportAsync_InvalidDirectory_ReturnsInvalidPath(string directory)
    {
        var result = await Export(ExportFormat.Png, directory);

        Assert.Equal(ExportError.InvalidPath, result.Error);
    }

    [Fact]
    public async Task ExportAsync_InvalidBaseName_ReturnsInvalidPath()
    {
        var result = await Export(ExportFormat.Png, baseName: "a/b");

        Assert.Equal(ExportError.InvalidPath, result.Error);
    }

    [Fact]
    public async Task ExportAsync_MissingSource_ReturnsSourceMissing()
    {
        var result = await _exporter.ExportAsync(new ExportRequest(Path.Combine(_temp.Path, "none.png"), ExportFormat.Jpeg, _outputDirectory, "x", FourBySix));

        Assert.Equal(ExportError.SourceMissing, result.Error);
    }

    [Fact]
    public async Task ExportAsync_CorruptSource_ReturnsEncodingFailed()
    {
        var corrupt = Path.Combine(_temp.Path, "corrupt.png");
        await File.WriteAllBytesAsync(corrupt, [1, 2, 3, 4]);

        var result = await _exporter.ExportAsync(new ExportRequest(corrupt, ExportFormat.Pdf, _outputDirectory, "x", FourBySix));

        Assert.Equal(ExportError.EncodingFailed, result.Error);
        Assert.Empty(Directory.GetFiles(_outputDirectory));
    }
}
