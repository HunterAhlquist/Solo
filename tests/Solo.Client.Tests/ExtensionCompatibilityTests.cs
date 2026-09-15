using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Solo.Client.Extensions;
using Solo.Client.Layout;
using Solo.Client.State;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;

namespace Solo.Client.Tests;

/// <summary>
/// The compat check itself. This is the whole reason the client re-checks a
/// range the server already validated: in the Electron shell the bundled client
/// and the instance version independently.
/// </summary>
public class ExtensionCompatibilityTests
{
    [Theory]
    [InlineData(">=1.0 <2.0", true)]
    [InlineData(">=2.0 <3.0", false)]
    public void A_range_is_checked_against_this_builds_contract(string range, bool expected)
    {
        Assert.True(VersionRange.TryParse(range, out var parsed, out _));
        Assert.Equal(expected, parsed.Includes(ContractVersion.Parsed));
    }

    /// <summary>
    /// The rule a minor bump exists to enforce: a package that needs a member
    /// this build does not have is refused, and one whose floor this build meets
    /// loads.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="ContractVersion"/> rather than written as
    /// literals, so bumping the contract does not turn this into a chore — the
    /// property being asserted is the same at every version.
    /// </remarks>
    [Fact]
    public void A_package_needing_a_newer_contract_than_this_build_is_refused()
    {
        var current = ContractVersion.Parsed;

        Assert.True(VersionRange.TryParse(
            $">={current.Major}.{current.Minor} <{current.Major + 1}.0", out var met, out _));
        Assert.True(met.Includes(current), "a floor this build meets should load");

        Assert.True(VersionRange.TryParse(
            $">={current.Major}.{current.Minor + 1} <{current.Major + 1}.0", out var unmet, out _));
        Assert.False(unmet.Includes(current), "a floor one minor above this build should be refused");
    }
}
