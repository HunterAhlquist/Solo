using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Solo.Client.Api;
using Solo.Client.Auth;
using Solo.Client.State;
using Solo.Shared.Dtos;

namespace Solo.Client.Tests;

/// <summary>
/// The wizard's steps are gated on real API calls. MudStepper's Next button raises
/// <c>StepAction.Complete</c> for the step being left (not <c>Activate</c> for the
/// one being entered), and getting that backwards silently fires the final handoff
/// on step one — which 401s, because nobody has signed in yet.
/// </summary>
public class SetupWizardTests : ComponentTestBase
{
    private readonly StubHttp _http = new();

    public SetupWizardTests()
    {
        var api = _http.CreateClient();
        var config = new AppConfig
        {
            Auth = new AuthConfigDto("https://accounts.google.com", "cid", "Google", false),
            SetupCompleted = false,
        };

        Services.AddSingleton(api);
        Services.AddSingleton(new SetupClient(new HttpClient(_http) { BaseAddress = new Uri("https://solo.test/") }));
        Services.AddSingleton(new UserSession(api));
        Services.AddSingleton(config);
        Services.AddSingleton<SessionExpiry>();
        Services.AddScoped<GoogleIdentityService>();
    }


    private IRenderedComponent<Pages.Setup> RenderWizard()
    {
        var anonymous = Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
        return Render<Pages.Setup>(p => p.AddCascadingValue(anonymous));
    }

    private static void Fill(IRenderedComponent<Pages.Setup> wizard, string username, string password)
    {
        var inputs = wizard.FindAll("input");
        inputs[0].Change(username);
        wizard.FindAll("input")[1].Change(password);
    }

    private static void ClickNext(IRenderedComponent<Pages.Setup> wizard) =>
        wizard.FindAll("button").First(b => b.TextContent.Contains("Next", StringComparison.OrdinalIgnoreCase)).Click();

    [Fact]
    public void The_first_Next_verifies_the_static_credentials()
    {
        _http.Respond(System.Net.HttpStatusCode.NoContent); // POST /api/setup/verify

        var wizard = RenderWizard();
        Fill(wizard, "setup", "s3cret");
        ClickNext(wizard);

        Assert.Equal("POST /api/setup/verify", _http.Paths.Single());
    }

    [Fact]
    public void The_first_Next_never_fires_the_final_handoff()
    {
        // The bug: Next on step one called POST /api/setup/complete, which needs a
        // bearer token nobody has yet, so the wizard answered 401 forever.
        _http.Respond(System.Net.HttpStatusCode.NoContent);

        var wizard = RenderWizard();
        Fill(wizard, "setup", "s3cret");
        ClickNext(wizard);

        Assert.DoesNotContain(_http.Paths, p => p.Contains("/api/setup/complete"));
    }

    [Fact]
    public void The_credentials_typed_into_the_form_are_the_ones_sent()
    {
        _http.Respond(System.Net.HttpStatusCode.NoContent);

        var wizard = RenderWizard();
        Fill(wizard, "operator", "hunter2");
        ClickNext(wizard);

        var auth = _http.Requests.Single().Headers.Authorization!;
        Assert.Equal("Setup", auth.Scheme);
        Assert.Equal(
            "operator:hunter2",
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!)));
    }

    [Fact]
    public void Rejected_credentials_keep_the_wizard_on_the_first_step()
    {
        _http.Respond(System.Net.HttpStatusCode.Unauthorized);

        var wizard = RenderWizard();
        Fill(wizard, "setup", "wrong");
        ClickNext(wizard);

        // Still asking for operator credentials, and no retention call was made.
        Assert.Contains("Operator credentials", wizard.Markup);
        Assert.DoesNotContain(_http.Paths, p => p.Contains("/api/setup/settings"));
    }

    [Fact]
    public void A_plain_rejection_reads_as_wrong_credentials()
    {
        // What the server's status-code pages put in an ordinary 401.
        _http.Respond(System.Net.HttpStatusCode.Unauthorized, """{"title":"Unauthorized","status":401}""");

        var wizard = RenderWizard();
        Fill(wizard, "setup", "wrong");
        ClickNext(wizard);

        Assert.Equal("Those setup credentials were rejected.", Services.GetRequiredService<ISnackbar>()
            .ShownSnackbars.Single().Message?.ToString());
    }

    [Fact]
    public void A_disabled_setup_account_says_why_instead_of_blaming_the_typing()
    {
        // The operator typed the template's placeholder correctly; "rejected"
        // would send them back to retype it.
        _http.Respond(System.Net.HttpStatusCode.Unauthorized, """
            {"title":"The setup account is disabled","status":401,
             "detail":"Setup__Password is still the template's CHANGE_ME placeholder."}
            """);

        var wizard = RenderWizard();
        Fill(wizard, "setup", "CHANGE_ME_SETUP_PASSWORD");
        ClickNext(wizard);

        Assert.Contains("Operator credentials", wizard.Markup);
        var shown = Services.GetRequiredService<ISnackbar>().ShownSnackbars.Single();
        Assert.Equal("Setup__Password is still the template's CHANGE_ME placeholder.", shown.Message?.ToString());
        Assert.Equal(Severity.Error, shown.Severity);
    }

    [Fact]
    public void Accepted_credentials_advance_to_the_retention_step()
    {
        _http.Respond(System.Net.HttpStatusCode.NoContent);   // verify
        _http.RespondJson(new InstanceSettingsDto(90, false, null, 1)); // settings, if reached

        var wizard = RenderWizard();
        Fill(wizard, "setup", "s3cret");
        ClickNext(wizard);

        Assert.Contains("Audit retention", wizard.Markup);
    }

    [Fact]
    public void The_second_Next_saves_the_retention_setting()
    {
        _http.Respond(System.Net.HttpStatusCode.NoContent);
        _http.RespondJson(new InstanceSettingsDto(90, false, null, 1));

        var wizard = RenderWizard();
        Fill(wizard, "setup", "s3cret");
        ClickNext(wizard);
        ClickNext(wizard);

        Assert.Equal("PUT /api/setup/settings", _http.Paths[^1]);
    }
}
