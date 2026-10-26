using System.Text;
using System.Text.Json;
using Bunit;
using Solo.Client.Auth;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

public sealed class OfflineAuthTests:ComponentTestBase
{
    [Fact]
    public async Task An_expired_google_credential_keeps_the_verified_local_profile_offline_without_contacting_google()
    {
        var payload=Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":1,\"email\":\"offline@example.test\"}")).TrimEnd('=').Replace('+','-').Replace('/','_');
        JSInterop.Setup<bool>("solo.desktop.available").SetResult(false);
        JSInterop.Setup<string?>("solo.google.token").SetResult("e30."+payload+".fixture");
        JSInterop.Setup<bool>("solo.offline.isOffline").SetResult(true);
        JSInterop.Setup<string?>("solo.offline.profile").SetResult(JsonSerializer.Serialize(new UserDto(Guid.NewGuid(),"offline@example.test","Offline writer",null,false),new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await using var google=new GoogleIdentityService(JSInterop.JSRuntime,new AppConfig{Auth=new("https://accounts.google.com","fixture","google",true)},new SessionExpiry());
        var state=await google.GetAuthStateAsync();Assert.True(state.SignedIn);Assert.Equal("Offline writer",state.Display!.Identity!.Name);
        Assert.DoesNotContain(JSInterop.Invocations,i=>i.Identifier is "solo.google.init" or "solo.google.prompt" or "solo.google.retire");
    }
}
