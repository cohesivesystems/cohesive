using Microsoft.Extensions.Hosting;

namespace Cohesive.Configuration;

/// <summary>
/// Extensions for <see cref="IHostEnvironment"/> and <see cref="Environments"/>.
/// </summary>
public static class EnvironmentsExtensions
{
    extension(Environments)
    {
        /// <summary>
        /// Specifies the Local environment.
        /// </summary>
        /// <remarks>
        /// This local environment is used for local development and testing purposes.
        /// It typically runs on a developer's local machine and may point to local databases or services.
        /// </remarks>
        public static string Local => "Local";
    }

    extension(IHostEnvironment environment)
    {
        /// <summary>
        /// Checks if the current host environment name is Local.
        /// </summary>
        public bool IsLocal() =>
            environment.EnvironmentName.Equals(Environments.Local, StringComparison.OrdinalIgnoreCase);
    }
}
