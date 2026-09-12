// Copyright 2026 The Taisce Authors
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Taisce.AgentFramework;

/// <summary>
/// What an application needs to add governed memory: one call, and the settings from wherever that
/// application already keeps its settings.
/// </summary>
/// <remarks>
/// <para>
/// Registering by hand is still supported and is what the README shows for a single agent. This
/// exists because an application with a host builder keeps its endpoint and its credential in
/// configuration, and without it every consumer wrote the same wiring — differently.
/// </para>
/// <para>
/// <b>The credential comes from configuration, never from a literal.</b> Bind it from an
/// environment variable or a secret store, as the application binds every other secret: a token in
/// source is a token in the repository.
/// </para>
/// <para>
/// <b>No HTTP client factory.</b> The client owns one <see cref="HttpClient"/> for the lifetime of
/// the container, because taking a dependency on the factory would put a package in every
/// consumer's graph to gain nothing this client needs.
/// </para>
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>The configuration section these settings are read from by default.</summary>
    public const string DefaultSection = "Taisce";

    /// <summary>Adds the client and the context provider, configured in code.</summary>
    public static IServiceCollection AddTaisceMemory(this IServiceCollection services, Action<TaisceMemoryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.Configure(configure);
        return Register(services);
    }

    /// <summary>
    /// Adds the client and the context provider, configured from a section: <c>Endpoint</c>,
    /// <c>Token</c>, <c>DataSubjectId</c>, <c>RunId</c>, <c>MaxCharacters</c>, <c>SourceRoles</c>,
    /// <c>Hops</c>, <c>Surfaces</c> and <c>Themes</c>.
    /// </summary>
    public static IServiceCollection AddTaisceMemory(this IServiceCollection services, IConfiguration configuration, string sectionName = DefaultSection)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<TaisceMemoryOptions>(configuration.GetSection(sectionName));
        return Register(services);
    }

    private static IServiceCollection Register(IServiceCollection services)
    {
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<TaisceMemoryOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.Token))
            {
                throw new InvalidOperationException("Taisce needs an endpoint and a credential: set Taisce:Endpoint and Taisce:Token, or configure them in code.");
            }
            return new TaisceClient(options.Endpoint!, options.Token!);
        });
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<TaisceMemoryOptions>>().Value;
            return new TaisceContextProvider(provider.GetRequiredService<TaisceClient>(), options.ToProviderOptions());
        });
        return services;
    }
}

/// <summary>An application's memory settings, in the shape configuration binds to.</summary>
public sealed class TaisceMemoryOptions
{
    /// <summary>The deployment's base URL.</summary>
    public string? Endpoint { get; set; }

    /// <summary>A credential for one project. Bind it from a secret, never from source.</summary>
    public string? Token { get; set; }

    /// <summary>Who the turns are about, as registered. Null means project-wide memory.</summary>
    public string? DataSubjectId { get; set; }

    /// <summary>An identifier for this run, folded into every turn's idempotency key.</summary>
    public string? RunId { get; set; }

    /// <summary>The recall's content ceiling; null uses the deployment's.</summary>
    public int? MaxCharacters { get; set; }

    /// <summary>Which speakers' words may answer; null uses the deployment's default.</summary>
    public IList<string>? SourceRoles { get; set; }

    /// <summary>How far the walk may travel from an anchor; null uses the deployment's default.</summary>
    public int? Hops { get; set; }

    /// <summary>Which surfaces may answer; null uses all of them.</summary>
    public IList<string>? Surfaces { get; set; }

    /// <summary>Whether thematic reports may answer; null uses the deployment's default.</summary>
    public bool? Themes { get; set; }

    /// <summary>These settings as the provider takes them.</summary>
    public TaisceContextProviderOptions ToProviderOptions() => new()
    {
        DataSubjectId = DataSubjectId,
        RunId = RunId,
        MaxCharacters = MaxCharacters,
        SourceRoles = SourceRoles is null ? null : [.. SourceRoles],
        Hops = Hops,
        Surfaces = Surfaces is null ? null : [.. Surfaces],
        Themes = Themes,
    };
}
