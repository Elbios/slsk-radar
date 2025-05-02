// Source/Assemblies/Spotify.Slsk.Integration/Models/SoulseekRadarOptions.cs
namespace Spotify.Slsk.Integration.Models
{
    public class SoulseekRadarOptions
    {
        public int MaxUsers { get; set; } = 15;
        public int PerUserQuotaLargeU { get; set; } = 2;
        public int MaxPerUserQuotaSmallU { get; set; } = 4; // Derived: ceil(15/U) capped at 4
        public int FileSizeCapMB { get; set; } = 85; // Used in Step 3 (CrawlAndPickAsync) - Keep for consistency
        public int PerPeerTimeoutSeconds { get; set; } = 60;
        public int GlobalDownloadConcurrency { get; set; } = 4;
        public int DurationToleranceMs { get; set; } = 10000; // For potential future use in matching
        public string PlaylistNamePattern { get; set; } = "Soulseek-Radar · {Seed} · {Date}";
        public int SearchTimeoutSeconds { get; set; } = 15; // Added for the initial seed search
        public int CrawlTimeoutSeconds { get; set; } = 120; // Added from the separate options class below
        public int OverallTrackLimit { get; set; } = 30; // NEW: Limit for final harvested tracks
    }
}