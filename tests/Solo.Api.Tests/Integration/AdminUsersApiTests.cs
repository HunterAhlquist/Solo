using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Npgsql;
using Solo.Shared.Dtos;
using Xunit;

namespace Solo.Api.Tests.Integration;

/// <summary>
/// The super admin's controls over users: granting and removing super admin,
/// never down to none, and signing someone out of every device.
/// </summary>
[Collection("postgres")]
public sealed class AdminUsersApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Users = "/api/admin/users";

    private string _connectionString = null!;
    private SoloApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private UserDto _adminUser = null!;

    public async Task InitializeAsync()
    {
        if (!postgres.DockerAvailable)
        {
            return;
        }

        _connectionString = await postgres.CreateDatabaseAsync("admin_users");
        _factory = new SoloApiFactory(_connectionString);
        _admin = _factory.As("admin", "admin@example.com", "Admin");
        _adminUser = await TestClients.CompleteSetupAsync(_admin);
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>A provisioned user with no admin rights, as a client and their row.</summary>
    private async Task<(HttpClient Client, UserDto User)> MemberAsync(string name)
    {
        var project = await _admin.PostJson<ProjectDto>(
            "/api/projects", new { name = $"{name} project", alias = name.ToUpperInvariant() });
        await _admin.PostJson<ProjectMemberDto>(
            $"/api/projects/{project.Id}/members", new { email = $"{name}@example.com", role = "Read" });
        var client = _factory.As(name, $"{name}@example.com", name);
        return (client, await client.GetJson<UserDto>("/api/users/me"));
    }

    private static Task<HttpResponseMessage> SetSuperAdmin(HttpClient client, Guid userId, bool value) =>
        client.PutAsJsonAsync($"{Users}/{userId}/super-admin", new { isSuperAdmin = value }, TestClients.Json);

    [SkippableFact]
    public async Task SuperAdmin_CanBeGrantedAndRemoved_ButNeverTheLastOne()
    {
        postgres.SkipUnlessDockerAvailable();

        var (other, otherUser) = await MemberAsync("other");
        var listed = await _admin.GetJson<List<AdminUserDto>>(Users);
        Assert.True(listed.Single(u => u.Id == _adminUser.Id).IsSuperAdmin);
        Assert.False(listed.Single(u => u.Id == otherUser.Id).IsSuperAdmin);

        // Alone, the admin can't step down.
        Assert.Equal(HttpStatusCode.Conflict, (await SetSuperAdmin(_admin, _adminUser.Id, false)).StatusCode);

        var granted = await SetSuperAdmin(_admin, otherUser.Id, true);
        Assert.True((await granted.Content.ReadFromJsonAsync<AdminUserDto>(TestClients.Json))!.IsSuperAdmin);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/settings")).StatusCode);

        // With two, either can remove the other, and then the survivor is the last.
        Assert.Equal(HttpStatusCode.OK, (await SetSuperAdmin(other, _adminUser.Id, false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _admin.GetAsync("/api/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SetSuperAdmin(other, otherUser.Id, false)).StatusCode);
    }

    /// <summary>
    /// Two admins removing each other at once must not both win: the instance
    /// would be left with nobody able to grant it back.
    /// </summary>
    /// <remarks>
    /// Firing two requests together rarely interleaves them under the test
    /// server, so this stages the race instead. The test's own transaction
    /// plays the other admin: it holds the super-admin rows locked while the
    /// request starts, removes the requesting admin, and commits. A removal
    /// that counted without waiting for that lock would read two admins and
    /// leave none.
    /// </remarks>
    [SkippableFact]
    public async Task ARemovalRacingAnother_WaitsAndRefusesToRemoveTheLast()
    {
        postgres.SkipUnlessDockerAvailable();

        var (_, otherUser) = await MemberAsync("other");
        await SetSuperAdmin(_admin, otherUser.Id, true);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var rival = await connection.BeginTransactionAsync();
        await using (var lockRows = new NpgsqlCommand(
            """SELECT "Id" FROM "Users" WHERE "IsSuperAdmin" FOR UPDATE""", connection, rival))
        {
            await lockRows.ExecuteNonQueryAsync();
        }

        var removal = SetSuperAdmin(_admin, otherUser.Id, false);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(removal.IsCompleted);

        await using (var removeAdmin = new NpgsqlCommand(
            """UPDATE "Users" SET "IsSuperAdmin" = false WHERE "Id" = @id""", connection, rival))
        {
            removeAdmin.Parameters.AddWithValue("id", _adminUser.Id);
            await removeAdmin.ExecuteNonQueryAsync();
        }

        await rival.CommitAsync();

        Assert.Equal(HttpStatusCode.Conflict, (await removal).StatusCode);
        Assert.Equal(1, await CountSuperAdminsAsync());
    }

    [SkippableFact]
    public async Task OnlyASuperAdmin_ManagesUsers()
    {
        postgres.SkipUnlessDockerAvailable();

        var (member, memberUser) = await MemberAsync("member");

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(Users)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetSuperAdmin(member, memberUser.Id, true)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsync($"{Users}/{_adminUser.Id}/sign-out-everywhere", null)).StatusCode);
    }

    [SkippableFact]
    public async Task SigningAUserOutEverywhere_RevokesEverySessionTheyHold()
    {
        postgres.SkipUnlessDockerAvailable();

        var (member, memberUser) = await MemberAsync("member");
        var laptop = await member.PostJson<SessionDto>("/api/auth/sessions", new { description = "laptop" });
        var desktop = await member.PostJson<SessionDto>("/api/auth/sessions", new { description = "desktop" });
        var adminSession = await _admin.PostJson<SessionDto>("/api/auth/sessions", new { description = "admin's" });

        Assert.Equal(2, (await _admin.GetJson<List<AdminUserDto>>(Users))
            .Single(u => u.Id == memberUser.Id).ActiveSessions);

        var signedOut = await _admin.PostAsync($"{Users}/{memberUser.Id}/sign-out-everywhere", null);
        Assert.Equal(HttpStatusCode.OK, signedOut.StatusCode);
        var result = (await signedOut.Content.ReadFromJsonAsync<SignOutEverywhereResultDto>(TestClients.Json))!;
        Assert.Equal(2, result.Sessions);
        // No extension is installed here, so there is nothing else to revoke.
        Assert.Empty(result.Extensions);

        foreach (var revoked in new[] { laptop, desktop })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await WithToken(revoked.Token).GetAsync("/api/users/me")).StatusCode);
        }

        // Nobody else's.
        Assert.Equal(HttpStatusCode.OK, (await WithToken(adminSession.Token).GetAsync("/api/users/me")).StatusCode);
        Assert.Equal(0, (await _admin.GetJson<List<AdminUserDto>>(Users))
            .Single(u => u.Id == memberUser.Id).ActiveSessions);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _admin.PostAsync($"{Users}/{Guid.NewGuid()}/sign-out-everywhere", null)).StatusCode);
    }

    private HttpClient WithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<long> CountSuperAdminsAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""SELECT count(*) FROM "Users" WHERE "IsSuperAdmin" """, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
