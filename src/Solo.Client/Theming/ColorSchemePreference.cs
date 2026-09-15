using System.Text.Json;
using Microsoft.JSInterop;
using MudBlazor;
using Solo.Shared.Dtos;

namespace Solo.Client.Theming;

/// <summary>Which colour scheme the user wants. Not a site setting — a personal one.</summary>
public enum ColorSchemePreference
{
    /// <summary>Follow the operating system, and keep following it as it changes.</summary>
    System = 0,
    Light = 1,
    Dark = 2,
}
