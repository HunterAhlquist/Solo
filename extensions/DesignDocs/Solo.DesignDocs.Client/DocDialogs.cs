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
    // "remove" takes only the embed out of the note; "delete" also deletes the stored attachment.
    public static async Task<string?> RemoveAttachment(IDialogService dialogs,string name,bool sketch)
    {
        var d=await dialogs.ShowAsync<DocRemoveAttachmentDialog>(sketch?"Remove sketch":"Remove image",
            new DialogParameters<DocRemoveAttachmentDialog>{{x=>x.Name,name},{x=>x.Sketch,sketch}});
        if(await d.Result is not {Canceled:false,Data:string choice})return null;
        if(choice!="delete")return "remove";
        return await Confirm(dialogs,sketch?"Delete sketch":"Delete image",
            $"Delete “{name}” permanently? It is deleted from the vault and cannot be recovered. Other notes that embed it will show it as unavailable.",
            "Delete permanently")?"delete":null;
    }
    public static async Task<bool> Confirm(IDialogService dialogs,string title,string message,string action="Delete")
    {
        var d=await dialogs.ShowAsync<DocConfirmDialog>(title,new DialogParameters<DocConfirmDialog>{{x=>x.Message,message},{x=>x.ConfirmText,action}});
        return await d.Result is {Canceled:false};
    }
}
