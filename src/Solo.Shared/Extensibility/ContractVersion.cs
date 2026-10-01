namespace Solo.Shared.Extensibility;

/// <summary>
/// The version of this contract. Extensions declare the range they support in
/// their manifest and the host refuses to load a package outside it.
/// </summary>
/// <remarks>
/// <para>Two ranges exist because they move independently. <c>soloApiVersion</c> is
/// checked on the server against this constant; <c>soloClientVersion</c> is
/// checked in the browser against the running client build, which in the
/// Electron shell is bundled with the app and can be older than the instance.
/// On the hosted web build the two are always the same number.</para>
///
/// <para><b>Adding a member to the contract is a minor bump, and it is not
/// optional.</b> A package compiled against the new member declares a floor
/// that excludes hosts without it, and the range check then refuses the package
/// with a sentence naming the reason. Leave the number alone and the check
/// passes on a host that cannot satisfy the package — which is worse than
/// having no check, because the failure surfaces as a broken page instead of
/// "update the app".</para>
///
/// <para>1.1.0 added <see cref="ISoloClientApi"/>: the client-side seam an
/// extension's pages use to call the API.</para>
/// <para>1.2.0 adds task-detail UI contributions, assignee identity reads,
/// versioned extension fields and transactional audit contributions.</para>
/// <para>1.3.0 adds task start dates (<c>TaskDto.StartAt</c>) with the
/// <c>task.setSchedule</c> operation, project sidebar sections, the host task
/// drawer (<see cref="ISoloTaskDrawer"/>), Admin settings panels and
/// <c>ISoloQuery.CallerAsync</c>.</para>
/// <para>1.4.0 adds sections on a project's home page and surfaces on its card
/// on All projects (<see cref="ProjectHomeSectionAttribute"/>,
/// <see cref="ProjectCardSectionAttribute"/>), and live topics with presence:
/// <c>ISoloLiveUpdates</c> to publish on the server, <see cref="ISoloLiveTopics"/>
/// to subscribe in the browser.</para>
/// </remarks>
public static class ContractVersion
{
    public const string Current = "1.4.0";

    public static readonly Version Parsed = new(1, 4, 0);
}
