using System.Configuration;
using System.IO;
using TwitchDownloaderWPF.Properties;

namespace TwitchDownloaderWPF.Services
{
    /// <summary>
    /// Persists <see cref="Settings"/> on a best-effort basis.
    /// <para>
    /// The user settings are stored in a plain XML file under %LOCALAPPDATA%. When that file cannot be written
    /// (read-only or redirected profile, roaming sync, antivirus interference, restricted process token) the
    /// settings provider throws. Several pages save while their XAML is still initializing, so such a failure
    /// escapes as a XamlParseException during MainWindow construction and takes the whole start-up down with it.
    /// Losing a preference is acceptable; losing the launch is not.
    /// </para>
    /// </summary>
    public static class SettingsSaver
    {
        public static void TrySave()
        {
            try
            {
                Settings.Default.Save();
            }
            catch (Exception ex) when (ex is ConfigurationErrorsException or UnauthorizedAccessException or IOException)
            {
                // Keep running with the in-memory values for this session.
            }
        }
    }
}
