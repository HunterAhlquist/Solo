using System.Net;
using System.Net.Http.Json;
using Solo.DesignDocs.Contracts;
using Solo.Shared.Dtos;
namespace Solo.DesignDocs.Tests;
public sealed partial class VaultTests
{
    private static readonly byte[] Pixel=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jH4sAAAAASUVORK5CYII=");
    [SkippableFact]
    public async Task Images_are_members_only_isolated_and_replace_in_place()
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();var path=$"{Root}/{v.Id}/images";
        var response=await _writer.PostAsync(path+"?path=attachments/pixel.png",new ByteArrayContent(Pixel));response.EnsureSuccessStatusCode();
        var image=(await response.Content.ReadFromJsonAsync<ImageDto>(TestClients.Json))!;Assert.Equal(1,image.Width);Assert.Equal("image/png",image.ContentType);
        var bytes=await _reader.GetAsync(path+"/"+image.Id);Assert.Equal(Pixel,await bytes.Content.ReadAsByteArrayAsync());
        Assert.Contains("sandbox",bytes.Headers.GetValues("Content-Security-Policy").Single());Assert.Equal("nosniff",bytes.Headers.GetValues("X-Content-Type-Options").Single());
        var other=await _admin.PostJson<ProjectDto>("/api/projects",new{name="Other",alias="OTHER"});
        await _admin.PostJson<ProjectMemberDto>($"/api/projects/{other.Id}/members",new{email="stranger@example.com",role="Read"});
        using var stranger=_factory.As("stranger","stranger@example.com");Assert.Equal(HttpStatusCode.NotFound,(await stranger.GetAsync(path+"/"+image.Id)).StatusCode);
        var replaced=await _writer.PutAsync(path+"/"+image.Id+"?version="+image.Version,new ByteArrayContent(Pixel));replaced.EnsureSuccessStatusCode();
        Assert.Equal(image.Id,(await replaced.Content.ReadFromJsonAsync<ImageDto>(TestClients.Json))!.Id);
        Assert.Equal(HttpStatusCode.Conflict,(await _writer.PutAsync(path+"/"+image.Id+"?version="+image.Version,new ByteArrayContent(Pixel))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await _reader.PostAsync(path+"?path=no.png",new ByteArrayContent(Pixel))).StatusCode);
    }
    [SkippableTheory]
    [InlineData("attachments/bad.svg","<svg></svg>")]
    [InlineData("../escape.png","bad")]
    [InlineData("/absolute.png","bad")]
    public async Task Invalid_image_types_and_paths_are_rejected(string file,string text)
    {
        postgres.SkipUnlessDockerAvailable();var v=await Vault();var response=await _writer.PostAsync($"{Root}/{v.Id}/images?path={Uri.EscapeDataString(file)}",new StringContent(text));
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType);
    }
}
