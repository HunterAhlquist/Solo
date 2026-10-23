using Solo.DesignDocs.Client;

namespace Solo.DesignDocs.Tests;

public sealed class VaultPathTests
{
    [Fact]
    public void Relative_image_paths_disambiguate_repeated_filenames()
    {
        string[] paths = ["art/pixel.png", "design/assets/pixel.png", "design/assets/a b.png"];
        Assert.Equal("design/assets/pixel.png", VaultPaths.ResolveImage(paths, "../assets/pixel.png", "design/notes/Movement.md"));
        Assert.Equal("art/pixel.png", VaultPaths.ResolveImage(paths, "art/pixel.png", "design/notes/Movement.md"));
        Assert.Equal("design/assets/a b.png", VaultPaths.ResolveImage(paths, "../assets/a b.png", "design/notes/Movement.md"));
        Assert.Null(VaultPaths.ResolveImage(paths, "pixel.png", "Movement.md"));
        Assert.Null(VaultPaths.ResolveImage(paths, "https://outside.test/pixel.png", "design/notes/Movement.md"));
        Assert.Null(VaultPaths.ResolveImage(paths, "//outside.test/pixel.png", "Movement.md"));
    }
}
