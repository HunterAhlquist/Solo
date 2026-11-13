using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Solo.Api.Auth;
using Solo.Api.Email;
using Solo.Api.Realtime;
using Solo.Api.Services;
using Solo.Data;
using Solo.Data.Domain;
using Solo.Data.Entities;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Hubs;

namespace Solo.Api.Controllers;

[Route("api/projects")]
[Authorize]
public sealed class ProjectsController(
    SoloDbContext db,
    CurrentUserAccessor currentUser,
    IBoardNotifier notifier,
    EmailOutbox outbox)
    : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectSummaryDto>>> List(CancellationToken ct)
    {
        var userId = currentUser.User?.Id;
        if (userId is null)
        {
            return Forbid();
        }

        return await db.ProjectMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.Project!.Name)
            .Select(m => new ProjectSummaryDto(
                m.ProjectId, m.Project!.Name, m.Project.Alias, m.Project.Icon, m.Project.ImageId,
                m.Role, m.IsOwner))
            .ToListAsync(ct);
    }

    /// <summary>Project creation is reserved for the super admin.</summary>
    [HttpPost]
    [Authorize(Policy = AuthConstants.SuperAdminPolicy)]
    public async Task<ActionResult<ProjectDto>> Create(CreateProjectRequest request, CancellationToken ct)
    {
        var user = currentUser.User!;
        var project = new Project
        {
            Name = request.Name,
            Alias = Aliases.Normalize(request.Alias),
            Icon = request.Icon,
            ImageId = request.ImageId,
        };
        project.Members.Add(new ProjectMember
        {
            Email = user.Email,
            UserId = user.Id,
            Role = ProjectRole.ReadWrite,
            IsOwner = true,
        });
        project.Labels.AddRange(LabelDefaults.ForProject(project.Id));
        db.Projects.Add(project);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e, "IX_Projects_Alias"))
        {
            return AliasTakenProblem("project");
        }

        var created = await LoadProject(project.Id, ct);
        return CreatedAtAction(nameof(Get), new { projectId = project.Id }, created!.ToDto());
    }

    [HttpGet("{projectId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectReadPolicy)]
    public async Task<ActionResult<ProjectDto>> Get(Guid projectId, CancellationToken ct)
    {
        var project = await LoadProject(projectId, ct);
        return project is null ? NotFound() : project.ToDto();
    }

    /// <summary>Alias lookup: what the client's aliased routes resolve through.</summary>
    [HttpGet("by-alias/{projectAlias}")]
    [Authorize(Policy = AuthConstants.ProjectReadPolicy)]
    public async Task<ActionResult<ProjectDto>> GetByAlias(string projectAlias, CancellationToken ct)
    {
        var alias = Aliases.Normalize(projectAlias);
        var project = await db.Projects.AsNoTracking()
            .Include(p => p.Members).ThenInclude(m => m.User)
            .FirstOrDefaultAsync(p => p.Alias == alias, ct);
        return project is null ? NotFound() : project.ToDto();
    }

    [HttpPut("{projectId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectOwnerPolicy)]
    public async Task<ActionResult<ProjectDto>> Update(
        Guid projectId, UpdateProjectRequest request, CancellationToken ct)
    {
        var project = await LoadProject(projectId, ct, track: true);
        if (project is null)
        {
            return NotFound();
        }

        project.Name = request.Name;
        project.Alias = Aliases.Normalize(request.Alias);
        project.Icon = request.Icon;
        project.ImageId = request.ImageId;
        UseClientVersion(db, project, request.Version);
        try
        {
            var conflict = await SaveOrConflictAsync(db, ct);
            if (conflict is not null)
            {
                return conflict;
            }
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e, "IX_Projects_Alias"))
        {
            return AliasTakenProblem("project");
        }

        await notifier.ProjectEntityChanged(projectId, EntityTypes.Project, projectId,
            ChangeKind.Updated, nameof(project.Name), nameof(project.Alias),
            nameof(project.Icon), nameof(project.ImageId));
        return project.ToDto();
    }

    [HttpDelete("{projectId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectOwnerPolicy)]
    public async Task<IActionResult> Delete(Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects
            .Include(p => p.Boards).Include(p => p.Members).Include(p => p.Labels)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null)
        {
            return NotFound();
        }

        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ------------------------------------------------------------------
    // Members
    // ------------------------------------------------------------------

    [HttpGet("{projectId:guid}/members")]
    [Authorize(Policy = AuthConstants.ProjectReadPolicy)]
    public async Task<ActionResult<IReadOnlyList<ProjectMemberDto>>> ListMembers(
        Guid projectId, CancellationToken ct)
    {
        var members = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == projectId)
            .Include(m => m.User)
            .OrderBy(m => m.Email)
            .ToListAsync(ct);
        return members.Select(m => m.ToDto()).ToList();
    }

    /// <summary>
    /// Invite by email. If that email has already signed in the membership
    /// activates immediately; otherwise it is pending until first sign-in.
    /// </summary>
    [HttpPost("{projectId:guid}/members")]
    [Authorize(Policy = AuthConstants.ProjectOwnerPolicy)]
    public async Task<ActionResult<ProjectMemberDto>> InviteMember(
        Guid projectId, InviteMemberRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var exists = await db.ProjectMembers.AsNoTracking()
            .AnyAsync(m => m.ProjectId == projectId && m.Email == email, ct);
        if (exists)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Already a member",
                detail: "A member or pending invite with this email already exists.");
        }

        var knownUser = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        var member = new ProjectMember
        {
            ProjectId = projectId,
            Email = email,
            UserId = knownUser?.Id,
            Role = request.Role,
        };
        db.ProjectMembers.Add(member);
        // In the same save as the member, so an invite never exists without its email or the other way round.
        var emailed = await QueueInviteAsync(projectId, email, ct);
        await db.SaveChangesAsync(ct);
        if (emailed)
        {
            outbox.Wake();
        }

        await notifier.ProjectEntityChanged(projectId, EntityTypes.Project, projectId,
            ChangeKind.Updated, "Members");
        member.User = knownUser;
        return CreatedAtAction(nameof(ListMembers), new { projectId }, member.ToDto());
    }

    /// <summary>Emails a pending invite again, for when the first one went astray.</summary>
    [HttpPost("{projectId:guid}/members/{memberId:guid}/invite")]
    [Authorize(Policy = AuthConstants.ProjectOwnerPolicy)]
    public async Task<IActionResult> ResendInvite(Guid projectId, Guid memberId, CancellationToken ct)
    {
        var member = await db.ProjectMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.Id == memberId, ct);
        if (member is null)
        {
            return NotFound();
        }

        if (member.UserId is not null)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Already joined",
                detail: "This person has already signed in, so there's no invite to send.");
        }

        if (!await QueueInviteAsync(projectId, member.Email, ct))
        {
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Email is off",
                detail: "Email isn't set up on this instance.");
        }

        await db.SaveChangesAsync(ct);
        outbox.Wake();
        return NoContent();
    }

    /// <summary>Adds the invite email to this save, or does nothing and says so when email is off.</summary>
    private async Task<bool> QueueInviteAsync(Guid projectId, string email, CancellationToken ct)
    {
        if (await EmailOutbox.ReadySettingsAsync(db, ct) is not { } settings)
        {
            return false;
        }

        var project = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.Name).FirstAsync(ct);
        var inviter = currentUser.User!.DisplayName ?? currentUser.User.Email;
        EmailOutbox.Queue(db, email, EmailTemplates.Invite(
            await outbox.BrandAsync(db, ct), inviter, project, email, EmailOutbox.BaseAddress(settings)), "invite");
        return true;
    }

    [HttpPut("{projectId:guid}/members/{memberId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectOwnerPolicy)]
    public async Task<ActionResult<ProjectMemberDto>> UpdateMember(
        Guid projectId, Guid memberId, UpdateMemberRequest request, [FromServices] LiveAccess live, CancellationToken ct)
    {
        var member = await db.ProjectMembers.Include(m => m.User)
            .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.Id == memberId, ct);
        if (member is null)
        {
            return NotFound();
        }

        if (member.IsOwner && !request.IsOwner &&
            !await OtherOwnerExists(projectId, memberId, ct))
        {
            return LastOwnerProblem();
        }

        member.Role = request.Role;
        member.IsOwner = request.IsOwner;
        var conflict = await SaveOrConflictAsync(db, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        if (member.UserId is { } userId) await live.RevalidateAsync(userId, ct);

        await notifier.ProjectEntityChanged(projectId, EntityTypes.Project, projectId,
            ChangeKind.Updated, "Members");
        return member.ToDto();
    }

    [HttpDelete("{projectId:guid}/members/{memberId:guid}")]
    [Authorize(Policy = AuthConstants.ProjectOwnerPolicy)]
    public async Task<IActionResult> RemoveMember(
        Guid projectId, Guid memberId, [FromServices] LiveAccess live, CancellationToken ct)
    {
        var member = await db.ProjectMembers
            .FirstOrDefaultAsync(m => m.ProjectId == projectId && m.Id == memberId, ct);
        if (member is null)
        {
            return NotFound();
        }

        if (member.IsOwner && !await OtherOwnerExists(projectId, memberId, ct))
        {
            return LastOwnerProblem();
        }

        db.ProjectMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        if (member.UserId is { } userId)
        {
            // Before the broadcast below, which they are no longer entitled to.
            await live.RevalidateAsync(userId, ct);
        }

        await notifier.ProjectEntityChanged(projectId, EntityTypes.Project, projectId,
            ChangeKind.Updated, "Members");
        return NoContent();
    }

    private Task<Project?> LoadProject(Guid projectId, CancellationToken ct, bool track = false)
    {
        var query = db.Projects
            .Include(p => p.Members).ThenInclude(m => m.User)
            .AsQueryable();
        if (!track)
        {
            query = query.AsNoTracking();
        }

        return query.FirstOrDefaultAsync(p => p.Id == projectId, ct);
    }

    private Task<bool> OtherOwnerExists(Guid projectId, Guid exceptMemberId, CancellationToken ct) =>
        db.ProjectMembers.AsNoTracking()
            .AnyAsync(m => m.ProjectId == projectId && m.Id != exceptMemberId && m.IsOwner, ct);

    private ObjectResult LastOwnerProblem() => Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Last owner",
        detail: "A project must keep at least one owner.");
}
