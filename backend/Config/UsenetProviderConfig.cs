using NzbWebDAV.Models;

namespace NzbWebDAV.Config;

public class UsenetProviderConfig
{
    public const string ElfHostedNewsHost = "news.elfhosted.com";

    public static bool IsElfHostedNewsHost(string? host)
    {
        return !string.IsNullOrWhiteSpace(host)
               && host.Equals(ElfHostedNewsHost, StringComparison.OrdinalIgnoreCase);
    }

    public List<ConnectionDetails> Providers { get; set; } = [];

    public int TotalPooledConnections => Math.Max(1, Providers
        .Where(x => GetEffectiveType(x) == ProviderType.Pooled)
        .Select(x => x.MaxConnections)
        .Sum());

    /// <summary>
    /// Returns the effective provider type, demoting news.elfhosted.com to BackupOnly
    /// when other non-disabled providers are configured.
    /// </summary>
    public ProviderType GetEffectiveType(ConnectionDetails provider)
    {
        if (provider.Type == ProviderType.Disabled)
            return ProviderType.Disabled;

        if (!IsElfHostedNewsHost(provider.Host))
            return provider.Type;

        var hasOtherProviders = Providers.Any(p =>
            p != provider
            && p.Type != ProviderType.Disabled
            && !IsElfHostedNewsHost(p.Host));

        return hasOtherProviders ? ProviderType.BackupOnly : provider.Type;
    }

    public class ConnectionDetails
    {
        public required ProviderType Type { get; set; }
        public required string Host { get; set; }
        public required int Port { get; set; }
        public required bool UseSsl { get; set; }
        public required string User { get; set; }
        public required string Pass { get; set; }
        public required int MaxConnections { get; set; }
    }
}