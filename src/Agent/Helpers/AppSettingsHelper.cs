using System;
using System.IO;

namespace AgentFox.Helpers
{
    public static class AppSettingsHelper
    {
        public const string DefaultsFileName = "appsettings.defaults.json";
        public const string UserFileName = "appsettings.user.json";

        /// <summary>
        /// Returns the stable, user-owned configuration file. The release archive never owns
        /// this path, so onboarding, Doctor, and runtime configuration tools can safely persist
        /// changes without a later update replacing them.
        /// </summary>
        public static string ResolveAppSettingsPath()
        {
            var configured = Environment.GetEnvironmentVariable("AGENTFOX_CONFIG_FILE");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(AppContext.BaseDirectory, UserFileName)
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        }

        /// <summary>The release-owned defaults that are replaced on every update.</summary>
        public static string ResolveDefaultsPath()
            => Path.Combine(AppContext.BaseDirectory, DefaultsFileName);
    }
}
