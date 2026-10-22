using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace Solo.DesignDocs.Controllers;

public sealed class DesignDocsErrors : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is DbUpdateConcurrencyException ||
            context.Exception is DbUpdateException { InnerException: PostgresException {
                SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation } })
        {
            context.Result = new ObjectResult(new ProblemDetails { Status = 409, Title = "Content changed",
                Detail = "That name is already used, or the content changed elsewhere. Refresh and try again." }) { StatusCode = 409 };
            context.ExceptionHandled = true;
        }
    }
}
