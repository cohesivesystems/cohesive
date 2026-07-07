using Cohesive.Adapters.AzureStorage;
using DurableTask.AzureStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cohesive.Adapters.DurableTask.AzureStorage;

/// <summary>
/// Azure Storage settings for Durable Task-backed process execution.
/// </summary>
public sealed record DurableTaskAzureStorageSettings
{
    /// <summary>
    /// The Durable Task hub name used by the orchestration service.
    /// </summary>
    public string? TaskHubName { get; init; }

    /// <summary>
    /// The configured Azure Blob Storage profile used by Durable Task.
    /// </summary>
    public string? AzureStorageName { get; init; } = AzureBlobStorageOptions.DefaultName;
}

/// <summary>
/// Configures Azure Storage-backed Durable Task settings from Cohesive Azure Storage profiles.
/// </summary>
public static class DurableTaskAzureStorageSettingsExtensions
{
    extension(AzureStorageOrchestrationServiceSettings settings)
    {
        /// <summary>
        /// Configures Durable Task Azure Storage settings from a named Cohesive Azure Storage profile.
        /// </summary>
        public void ConfigureDurableTaskAzureStorage(IServiceProvider sp, DurableTaskAzureStorageSettings durableTaskSettings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(sp);
            ArgumentNullException.ThrowIfNull(durableTaskSettings);

            settings.TaskHubName = DurableTaskAzureStorageHubName.Normalize(durableTaskSettings.TaskHubName);
            settings.StorageAccountClientProvider = sp.CreateDurableTaskStorageAccountClientProvider(durableTaskSettings.AzureStorageName);

            var loggerFactory = sp.GetService<ILoggerFactory>();
            if (loggerFactory is not null)
                settings.LoggerFactory = loggerFactory;
        }
    }

    extension(IServiceProvider sp)
    {
        /// <summary>
        /// Creates the Durable Task storage account client provider for the configured Azure Blob Storage profile.
        /// </summary>
        public StorageAccountClientProvider CreateDurableTaskStorageAccountClientProvider(string? azureStorageName)
        {
            ArgumentNullException.ThrowIfNull(sp);

            var options = sp.GetRequiredAzureStorageOptions(profile: azureStorageName);
            if (!string.IsNullOrWhiteSpace(options.ConnectionString))
                return new(connectionString: options.ConnectionString);

            var accountName = options.GetAccountName();
            var credential = options.TryCreateTokenCredential();
            if (!string.IsNullOrWhiteSpace(accountName) && credential is not null)
                return new(accountName: accountName, tokenCredential: credential);

            throw new InvalidOperationException("Durable Task Azure Storage is not configured. Configure an Azure storage connection string or account name.");
        }
    }
}

/// <summary>
/// Normalizes Durable Task hub names for Azure Storage-backed task hubs.
/// </summary>
public static class DurableTaskAzureStorageHubName
{
    // Durable Task appends storage-specific suffixes to hub names, so keep headroom under
    // Azure Storage table and queue name limits.
    const int MaxNormalizedLength = 45;

    /// <summary>
    /// Normalizes the supplied hub name into a lowercase alphanumeric identifier that starts with a letter.
    /// </summary>
    public static string Normalize(string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = IdentifierNormalizer.Normalize(
            value,
            IdentifierNormalizationOptions.CompactResourceName with
            {
                MaximumLength = MaxNormalizedLength
            });
        if (normalized.Length == 0)
            throw new ArgumentException("Durable Task hub name must contain at least one letter or digit.", nameof(value));

        return normalized;
    }
}
