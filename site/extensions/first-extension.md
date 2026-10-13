---
title: Your first extension
lede: Build a small extension with one endpoint and one page. Then package it and install it on an instance.
---

## What you build

The name of the extension is **Hello**. It adds:

- One endpoint, `GET /api/ext/hello/greeting`, that greets the signed-in user.
- One page, `/hello`, that shows the greeting.
- One entry at the bottom of the sidebar that opens the page.

The finished code is in the repository: [`samples/HelloExtension`](https://github.com/HunterAhlquist/Solo/tree/main/samples/HelloExtension).

> This page builds the extension by hand, to show each part. To get the same projects from Visual Studio, Rider or `dotnet new`, use the [project template](template.md).
{: .tip}

## What you need

- The .NET 10 SDK.
- The two contract packages. See [Get the contract packages](index.md#get-the-contract-packages).
- A Solo instance where you are a super admin. [Install](../admin/install.md) tells you how to run one.

## 1. Create the folders

```
HelloExtension/
├── extension.json
├── Hello/                  the server half
│   └── Contracts/          types that the two halves share
└── Hello.Client/           the client half
```

## 2. Write the server half

Create `Hello/Hello.csproj`. The contract package is its only Solo reference.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="HunterAhlquist.Solo.Extensibility" Version="1.9.0" />
  </ItemGroup>

</Project>
```

### The entry point

Each server half has exactly one class that implements `ISoloExtension`. Solo calls it one time at startup.

Create `Hello/HelloExtension.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Solo.Extensibility;

namespace Hello;

public sealed class HelloExtension : ISoloExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionContext context)
    {
    }
}
```

This extension registers no services. Solo finds the controller itself.

### The response type

Create `Hello/Contracts/Greeting.cs`. The client half compiles the same file, so the two halves agree on the type.

```csharp
namespace Hello;

public sealed record Greeting(string Message);
```

### The endpoint

Create `Hello/GreetingController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Solo.Extensibility;

namespace Hello;

[ApiController]
[Route("greeting")]
public sealed class GreetingController(ISoloQuery query) : ControllerBase
{
    [HttpGet]
    [Authorize]
    public async Task<Greeting> Get(CancellationToken ct)
    {
        var caller = await query.CallerAsync(ct);
        return new Greeting($"Hello, {caller.DisplayName ?? caller.Email ?? "there"}.");
    }
}
```

Two things to know:

- Solo puts `/api/ext/hello/` in front of each route of the extension. The endpoint is at `/api/ext/hello/greeting`.
- `[Authorize]` lets each user of the instance call the endpoint. `ISoloQuery.CallerAsync` returns that user.

## 3. Write the client half

Create `Hello.Client/Hello.Client.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <StaticWebAssetsEnabled>false</StaticWebAssetsEnabled>
  </PropertyGroup>

  <ItemGroup>
    <SupportedPlatform Include="browser" />
    <PackageReference Include="HunterAhlquist.Solo.Shared" Version="1.9.0" />
    <PackageReference Include="Microsoft.AspNetCore.Components.Web" Version="10.0.12" />
    <PackageReference Include="MudBlazor" Version="9.11.0" />
  </ItemGroup>

  <ItemGroup>
    <Compile Include="..\Hello\Contracts\*.cs" Link="Contracts\%(Filename)%(Extension)" />
  </ItemGroup>

</Project>
```

- `StaticWebAssetsEnabled` is `false` because static files go in the `content/` folder of the package.
- The versions of `Microsoft.AspNetCore.Components.Web` and `MudBlazor` are the versions that the instance runs. The instance supplies the two libraries at run time.
- The `Compile` item includes `Greeting.cs` from the server half.

Create `Hello.Client/_Imports.razor`:

```html
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Components.Web
@using MudBlazor
@using Solo.Shared.Extensibility
@using Hello
```

Create `Hello.Client/HelloPage.razor`:

```html
@page "/hello"
@attribute [Authorize]
@inject ISoloClientApi Api

<PageTitle>Hello · Solo</PageTitle>

<MudText Typo="Typo.h4">@(_greeting?.Message ?? "…")</MudText>

@code {
    private Greeting? _greeting;

    protected override async Task OnInitializedAsync() =>
        _greeting = await Api.GetAsync<Greeting>("api/ext/hello/greeting");
}
```

`ISoloClientApi` is the API client of Solo itself. The request uses the sign-in of the current user.

## 4. Write the manifest

Create `extension.json` in the top folder:

```json
{
  "id": "hello",
  "name": "Hello",
  "version": "1.0.0",
  "soloApiVersion": ">=1.3 <2.0",
  "soloClientVersion": ">=1.1 <2.0",
  "server": "server/Hello.dll",
  "client": "client/Hello.Client.dll",
  "nav": [
    { "route": "/hello", "label": "Hello", "icon": "Chat" }
  ]
}
```

- `id` becomes the route segment: `/api/ext/hello/`.
- The server half needs contract 1.3, because `CallerAsync` is from that version. The client half needs 1.1, because `ISoloClientApi` is from that version.
- The route of the `nav` entry has no `{projectAlias}`. Solo then shows the entry one time, at the bottom of the sidebar.

## 5. Build

```bash
dotnet build Hello/Hello.csproj -c Release
dotnet build Hello.Client/Hello.Client.csproj -c Release
```

## 6. Make the package

A package is a `.zip` file. The manifest is at its root. Each half has its own folder.

```bash
mkdir -p pkg/server pkg/client
cp extension.json pkg/
cp Hello/bin/Release/net10.0/Hello.dll pkg/server/
cp Hello.Client/bin/Release/net10.0/Hello.Client.dll pkg/client/
(cd pkg && zip -r ../hello.zip .)
```

Copy only your own assemblies. The build folder also contains `Solo.Shared.dll`, `Solo.Extensibility.dll` and MudBlazor. The instance has its own copies, so leave them out.

## 7. Install and check

1. Copy `hello.zip` into the extensions directory of the instance.
2. Restart the instance.
3. Sign in as a super admin and open the **Admin settings**. The **Extensions** card shows **Hello** with the state **loaded**.
4. Select **Hello** at the bottom of the sidebar. The page shows the greeting with your name.

[Install an extension](../admin/packages.md#install-an-extension) has the commands for Docker Compose.

If the extension does not show as loaded, the **Extensions** card gives the reason. See [Troubleshooting](troubleshooting.md).

## Next

- Store data: [Your own data](server.md#your-own-data).
- React to changes on a board: [Hooks and events](hooks-and-events.md).
- Add a section to the sidebar or the task drawer: [Contribute to core pages](client.md#contribute-to-core-pages).
- Build faster with no `.zip` file: [The development loop](package.md#the-development-loop).
