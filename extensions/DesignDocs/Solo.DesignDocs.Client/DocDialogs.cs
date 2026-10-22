using MudBlazor;
using Solo.DesignDocs.Contracts;
namespace Solo.DesignDocs.Client;

internal static class DocDialogs
{
    public static async Task<string?> Name(IDialogService dialogs, string title, string initial="")
    {
        var d=await dialogs.ShowAsync<DocNameDialog>(title,new DialogParameters<DocNameDialog>{{x=>x.InitialName,initial}});
        return await d.Result is {Canceled:false,Data:string name}?name:null;
    }
    public static async Task<bool> Confirm(IDialogService dialogs,string title,string message,string action="Delete")
    {
        var d=await dialogs.ShowAsync<DocConfirmDialog>(title,new DialogParameters<DocConfirmDialog>{{x=>x.Message,message},{x=>x.ConfirmText,action}});
        return await d.Result is {Canceled:false};
    }
}
