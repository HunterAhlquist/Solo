using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Solo.Api.Auth;
using Solo.Api.Services;
using Solo.Shared.Dtos;

namespace Solo.Api.Controllers;

[Route("api/users")]
[Authorize]
public sealed class UsersController : ApiControllerBase
{
    /// <summary>The provisioned profile of the signed-in user; the client's first call after sign-in.</summary>
    [HttpGet("me")]
    public ActionResult<UserDto> Me([FromServices] CurrentUserAccessor currentUser) =>
        currentUser.User is null ? Forbid() : currentUser.User.ToDto();
}
