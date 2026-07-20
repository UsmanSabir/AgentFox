using System;
using System.IO;

namespace AgentFox.Helpers
{
    public static class AppSettingsHelper
    {
        public static string ResolveAppSettingsPath()
        {
            var cwdPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
            return File.Exists(cwdPath) ? cwdPath : Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        }
    }
}