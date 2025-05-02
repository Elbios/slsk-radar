// Source/Assemblies/Spotify.Slsk.Integration/Models/HarvestedFileInfo.cs
namespace Spotify.Slsk.Integration.Models
{
    public class HarvestedFileInfo
    {
        public string Username { get; set; } = string.Empty;
        public string RemoteFilePath { get; set; } = string.Empty;
        public string LocalTempPath { get; set; } = string.Empty; // Keep track for cleanup if needed elsewhere
        public string Artist { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public long FileSize { get; set; } // Store original file size for reference

        public override string ToString()
        {
            return $"'{Artist} - {Title}' (Album: {Album}) from {Username} [{RemoteFilePath}]";
        }
    }
}