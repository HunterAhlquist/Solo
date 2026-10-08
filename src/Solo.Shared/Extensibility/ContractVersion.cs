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
/// <para>1.5.0 adds the host task picker (<see cref="ISoloTaskPicker"/>). It is
/// client-only, so a package needs 1.5 only in <c>soloClientVersion</c>.</para>
/// <para>1.6.0 adds the host context menu (<see cref="ISoloContextMenu"/>),
/// native in the desktop app. It is client-only too.</para>
/// <para>1.7.0 adds <c>ISoloCredentialIssuer</c>, through which signing a user
/// out everywhere revokes the credentials an extension issued them as well as
/// core's sessions. It is server-only, so a package needs 1.7 only in
/// <c>soloApiVersion</c>.</para>
/// <para>1.8.0 fills the gaps the first round of extension ideas ran into. On
/// the server: background work as a user who granted it (<c>ISoloBackground</c>),
/// member and comment reads on <c>ISoloQuery</c>, per-user live topics, and
/// three more hookable operations (<c>task.setBoard</c>, <c>task.setAssignees</c>,
/// <c>epic.setAssignees</c>). In the browser: contributions to task cards
/// (<see cref="TaskCardExtensionAttribute"/>), board column headers
/// (<see cref="BoardColumnExtensionAttribute"/>), project and board settings
/// (<see cref="ProjectSettingsSectionAttribute"/>,
/// <see cref="BoardSettingsSectionAttribute"/>), the app bar
/// (<see cref="AppBarExtensionAttribute"/>) and core's task menu
/// (<see cref="ISoloTaskMenuContributor"/>), plus
/// <see cref="ISoloLiveTopics.SubscribeUserAsync"/>. Both halves changed, so a
/// package using any of it needs 1.8 in whichever range covers the half that
/// uses it.</para>
/// </remarks>
public static class ContractVersion
{
    public const string Current = "1.8.0";

    /// <summary>
    /// <see cref="Current"/> as the loader compares it. Derived rather than
    /// written out a second time: a bump that changed only the string left the
    /// loader refusing a package that asked for exactly this version.
    /// </summary>
    public static readonly Version Parsed = Version.Parse(Current);
}
