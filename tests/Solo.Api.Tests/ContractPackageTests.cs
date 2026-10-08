using System.Reflection;
using Solo.Extensibility;
using Solo.Shared.Extensibility;
using Xunit;

namespace Solo.Api.Tests;

/// <summary>
/// Solo.Shared and Solo.Extensibility are published as the extension contract
/// packages. The package version is set in build/contract-package.props and
/// the contract version the host checks manifests against is
/// <see cref="ContractVersion.Current"/> — one number written twice, so these
/// pin them together.
/// </summary>
public sealed class ContractPackageTests
{
    public static TheoryData<Type> ContractAssemblies => new()
    {
        typeof(ContractVersion), // Solo.Shared
        typeof(ISoloExtension),  // Solo.Extensibility
    };

    [Theory]
    [MemberData(nameof(ContractAssemblies))]
    public void PackageVersionIsTheContractVersion(Type marker)
    {
        var informational = marker.Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        // SourceLink appends "+<commit>".
        Assert.Equal(ContractVersion.Current, informational.Split('+')[0]);
    }

    /// <summary>
    /// Extensions bind to the host's copy of these assemblies, and .NET refuses
    /// a reference to a higher assembly version than the one loaded. Tracking
    /// the minor would let a package compiled against a newer contract fail to
    /// load on an older host its manifest range admits.
    /// </summary>
    [Theory]
    [MemberData(nameof(ContractAssemblies))]
    public void AssemblyVersionStaysOnTheMajor(Type marker)
    {
        Assert.Equal(
            new Version(ContractVersion.Parsed.Major, 0, 0, 0),
            marker.Assembly.GetName().Version);
    }
}
