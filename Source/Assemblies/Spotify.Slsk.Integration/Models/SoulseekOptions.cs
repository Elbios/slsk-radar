using System;
namespace Spotify.Slsk.Integration.Models
{
    public class SoulseekOptions
    {
        public bool AllowFlac { get; set; } = true;
        public bool SkipResults { get; set; } = false;
        public int SearchTimeout { get; set; } = 10;

    }
}

