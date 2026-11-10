using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Solo.Data;
using Solo.Data.Entities;

namespace Solo.Api.Email;

/// <summary>
/// Keeps the Data Protection key ring in Postgres. The default folder is inside
/// the container, so a redeploy would lose the key and every secret it sealed,
/// such as the SMTP password.
/// </summary>
public sealed class DbXmlRepository(IServiceScopeFactory scopes) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SoloDbContext>();
        return db.DataProtectionKeys.AsNoTracking().Select(k => k.Xml).ToList().Select(XElement.Parse).ToList();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SoloDbContext>();
        db.DataProtectionKeys.Add(new DataProtectionKey
        {
            FriendlyName = friendlyName,
            Xml = element.ToString(SaveOptions.DisableFormatting),
        });
        db.SaveChanges();
    }
}
