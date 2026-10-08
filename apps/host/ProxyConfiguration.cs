using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace F1Hue.Host;

public static class ProxyConfiguration
{
    public static bool Configure(IServiceCollection services, bool desktop)
    {
        var proxies = (Environment.GetEnvironmentVariable("F1_HUE_TRUSTED_PROXIES") ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (proxies.Length == 0)
            return false;
        if (desktop)
            throw new ArgumentException("Un proxy n’est pas autorisé avec la connexion automatique de bureau.");
        var addresses = proxies.Select(value => IPAddress.TryParse(value, out var ip) ? ip
            : throw new ArgumentException("F1_HUE_TRUSTED_PROXIES attend des adresses IP séparées par des virgules, sans joker.")).ToArray();
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var address in addresses)
                options.KnownProxies.Add(address);
        });
        return true;
    }
}
