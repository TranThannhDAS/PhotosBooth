namespace Photobooth.Cloud.Tests;

/// <summary>Cấu hình lưu trữ sai thì ứng dụng không được khởi động.</summary>
public sealed class CloudSettingsTests
{
    [Fact]
    public void Production_WithoutRootPath_RefusesToStart()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CloudApp.Build(["--environment=Production", "--Storage:RootPath="]));
        Assert.Contains("Storage:RootPath", ex.Message);
    }

    [Fact]
    public void MissingConnectionString_RefusesToStart()
    {
        using var root = new TempFolder();
        var ex = Assert.Throws<InvalidOperationException>(
            () => CloudApp.Build(["--environment=Production", $"--Storage:RootPath={root.Path}", "--ConnectionStrings:DefaultConnection="]));
        Assert.Contains("ConnectionStrings:DefaultConnection", ex.Message);
    }

    [Fact]
    public void RelativeRootPath_RefusesToStart()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CloudApp.Build(["--Storage:RootPath=photos"]));
        Assert.Contains("tuyệt đối", ex.Message);
    }

    [Fact]
    public void RootPathInsideApplicationFolder_RefusesToStart()
    {
        var inside = Path.Combine(AppContext.BaseDirectory, "Photos");
        var ex = Assert.Throws<InvalidOperationException>(() => CloudApp.Build([$"--Storage:RootPath={inside}"]));
        Assert.Contains("publish", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("abc")]
    public void InvalidMaxFileSize_RefusesToStart(string value)
    {
        using var root = new TempFolder();
        Assert.Throws<InvalidOperationException>(
            () => CloudApp.Build([$"--Storage:RootPath={root.Path}", $"--Storage:MaxFileSizeMB={value}"]));
    }

    [Theory]
    [InlineData("photo.example.com")]
    [InlineData("ftp://photo.example.com")]
    [InlineData("https://photo.example.com/?a=1")]
    public void InvalidPublicBaseUrl_RefusesToStart(string value)
    {
        using var root = new TempFolder();
        Assert.Throws<InvalidOperationException>(
            () => CloudApp.Build([$"--Storage:RootPath={root.Path}", $"--PublicBaseUrl={value}"]));
    }
}
