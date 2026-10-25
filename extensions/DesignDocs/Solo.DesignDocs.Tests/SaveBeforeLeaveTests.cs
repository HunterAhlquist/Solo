using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Solo.DesignDocs.Client;

namespace Solo.DesignDocs.Tests;

public sealed class SaveBeforeLeaveTests : ComponentTestBase
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task Navigation_stays_on_the_note_if_saving_fails(bool saved)
    {
        var cut=Render<SaveBeforeLeave>(p=>p.Add(x=>x.Save,()=>Task.FromResult(saved)).Add(x=>x.ContentName,"note"));
        var nav=Services.GetRequiredService<NavigationManager>();
        await cut.InvokeAsync(()=>nav.NavigateTo("/next-note"));
        Assert.Equal(saved,nav.Uri.EndsWith("/next-note"));
        Assert.Equal(!saved,Services.GetRequiredService<ISnackbar>().ShownSnackbars.Any());
    }
    [Fact]
    public async Task Repeated_close_attempts_share_one_save_and_allow_retry_after_failure()
    {
        var completion=new TaskCompletionSource<bool>();var calls=0;
        var cut=Render<SaveBeforeLeave>(p=>p.Add(x=>x.Save,()=>{calls++;return completion.Task;}));
        Task<bool> first=null!,second=null!;
        await cut.InvokeAsync(()=>{first=cut.Instance.TryLeaveAsync();second=cut.Instance.TryLeaveAsync();});
        Assert.Equal(1,calls);Assert.Single(cut.FindAll("[role=status]"));
        await cut.InvokeAsync(()=>completion.SetResult(false));Assert.False(await first);Assert.False(await second);
        completion=new();Task<bool> retry=null!;
        await cut.InvokeAsync(()=>{retry=cut.Instance.TryLeaveAsync();});
        await cut.InvokeAsync(()=>completion.SetResult(true));Assert.True(await retry);Assert.Equal(2,calls);
    }
}
