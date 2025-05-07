// Source/Assemblies/Spotify.Slsk.Integration/Services/Spotify/SpotifyPlaylistService.cs
using Microsoft.Extensions.Logging;
using SpotifyAPI.Web;                  // Core namespace
using SpotifyAPI.Web.Auth;             // Auth namespace
using Spotify.Slsk.Integration.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;                      // HttpStatusCode
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace Spotify.Slsk.Integration.Services.Spotify
{
    public class SpotifyPlaylistService
    {
        private readonly ILogger<SpotifyPlaylistService> _logger;
        private readonly SpotifyAPI.Web.SpotifyClient? _spotify;
        private readonly string? _clientId;
        private readonly string? _clientSecret;
        private readonly string? _refreshToken;
		// Helper record to store track details for file output
		private record TrackLinkInfo(string Artist, string Title, string SpotifyUrl);
		
        public SpotifyPlaylistService(ILogger<SpotifyPlaylistService> logger)
        {
            _logger = logger;

            _clientId     = Environment.GetEnvironmentVariable("SPOTIFY_CLIENT_ID");
            _clientSecret = Environment.GetEnvironmentVariable("SPOTIFY_CLIENT_SECRET");
            _refreshToken = Environment.GetEnvironmentVariable("SPOTIFY_REFRESH_TOKEN");

            if (string.IsNullOrWhiteSpace(_clientId) ||
                string.IsNullOrWhiteSpace(_clientSecret) ||
                string.IsNullOrWhiteSpace(_refreshToken))
            {
                _logger.LogError(
                    "Spotify credentials (SPOTIFY_CLIENT_ID, SPOTIFY_CLIENT_SECRET, SPOTIFY_REFRESH_TOKEN) " +
                    "are missing. Spotify features disabled.");
                return;
            }

            try
            {
                // Exchange the refresh‑token for a fresh access‑token
                var token = new OAuthClient()
                            .RequestToken(new AuthorizationCodeRefreshRequest(
                                _clientId,
                                _clientSecret,
                                _refreshToken))
                            .GetAwaiter()
                            .GetResult();

                _spotify = new SpotifyAPI.Web.SpotifyClient(token.AccessToken);
                _logger.LogInformation("Spotify client initialised – token valid for {Minutes} min.",
                                       token.ExpiresIn / 60);
            }
            catch (APIException apiEx)
            {
                _logger.LogError(apiEx,
                    "Spotify API error during bootstrap (status {Status}).",
                    apiEx.Response?.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unrecoverable error while initialising Spotify client.");
            }
        }
        /// <summary>
        /// Helper class to store details for file output.
        /// </summary>
        private class FoundTrackDetail
        {
            public string Artist { get; set; }
            public string Title { get; set; }
            public string SpotifyUrl { get; set; }
        }

        /// <summary>
        /// Normalizes a string to be a valid filename by replacing invalid characters.
        /// </summary>
        private string NormalizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "Untitled";
            }
            string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            string invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);
            string normalizedName = Regex.Replace(name, invalidRegStr, "_");
            return normalizedName.Length > 100 ? normalizedName.Substring(0, 100) : normalizedName; // Optional: Truncate long names
        }
        /// <summary>
        /// Build (or overwrite) a playlist from harvested Soulseek matches,
        /// or output track links to a file if skipCreationOutputLinksOnly is true.
        /// </summary>
        public async Task CreatePlaylistFromHarvestedTracksAsync(
            string seedTrackTitle,
            List<HarvestedFileInfo> harvestedTracks,
            bool skipCreationOutputLinksOnly = false)
        {
            if (_spotify == null)
            {
                _logger.LogError("Spotify client not initialised – aborting.");
                return;
            }
            if (harvestedTracks == null || harvestedTracks.Count == 0)
            {
                _logger.LogWarning("No harvested tracks supplied; nothing to do.");
                return;
            }

            _logger.LogInformation(skipCreationOutputLinksOnly
                ? "↻ Searching Spotify matches for seed “{Seed}” to output to file..."
                : "↻ Building playlist for seed “{Seed}”…", seedTrackTitle);

            var foundTrackUris = new ConcurrentBag<string>(); // For playlist URIs
            var foundTracksForFile = skipCreationOutputLinksOnly ? new ConcurrentBag<FoundTrackDetail>() : null; // For file output
            var notFound = new ConcurrentBag<HarvestedFileInfo>(); // For tracks not found or unusable
            var gate = new SemaphoreSlim(5, 5); // Limit concurrent Spotify API calls

            var searchJobs = harvestedTracks.Select(async track =>
            {
                await gate.WaitAsync();
                try
                {
                    if (string.IsNullOrWhiteSpace(track.Artist) ||
                        string.IsNullOrWhiteSpace(track.Title))
                    {
                        _logger.LogWarning("Track has missing Artist or Title: User '{Username}', Path '{FilePath}'. Skipping.", track.Username, track.RemoteFilePath);
                        notFound.Add(track);
                        return;
                    }

                    var query =
                        $"artist:\"{track.Artist.Replace("\"", "\\\"")}\" " +
                        $"track:\"{track.Title.Replace("\"", "\\\"")}\"";

                    SearchResponse sr;
                    try
                    {
                        sr = await _spotify.Search.Item(
                                 new SearchRequest(
                                     SearchRequest.Types.Track,
                                     query)
                                 {
                                     Limit = 5, // Get a few results to find the best match
                                     Market = "from_token" // Use user's market
                                 });
                    }
                    catch (APIException apiEx)
                    {
                        _logger.LogError(apiEx,
                            "Spotify search API failed for query “{Query}” (status {Status}).",
                            query,
                            apiEx.Response?.StatusCode);
                        notFound.Add(track);
                        return;
                    }

                    var bestMatch = sr.Tracks?.Items?.FirstOrDefault(); // Simplistic best match, could be improved
                    if (bestMatch != null)
                    {
                        if (skipCreationOutputLinksOnly)
                        {
                            string spotifyLink = null;
                            bestMatch.ExternalUrls?.TryGetValue("spotify", out spotifyLink);

                            if (!string.IsNullOrEmpty(spotifyLink))
                            {
                                foundTracksForFile.Add(new FoundTrackDetail
                                {
                                    Artist = track.Artist, // Use original artist/title
                                    Title = track.Title,
                                    SpotifyUrl = spotifyLink
                                });
                            }
                            else
                            {
                                _logger.LogWarning("Track “{Artist} - {Title}” found on Spotify (URI: {Uri}) but has no external URL. Skipping for file output.", track.Artist, track.Title, bestMatch.Uri);
                                notFound.Add(track); // Track is unusable for file output
                            }
                        }
                        else // For playlist creation
                        {
                            foundTrackUris.Add(bestMatch.Uri);
                        }
                    }
                    else
                    {
                        notFound.Add(track);
                    }
                }
                catch (Exception ex) // Catch any other unexpected errors within a single job
                {
                    _logger.LogError(ex, "Unexpected error processing track: {Artist} - {Title}.", track.Artist, track.Title);
                    notFound.Add(track); // Ensure it's marked as not found/processed
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(searchJobs);

            if (skipCreationOutputLinksOnly)
            {
                if (foundTracksForFile == null || foundTracksForFile.IsEmpty)
                {
                    _logger.LogWarning("No Spotify tracks with URLs found for seed “{Seed}”. Output file skipped. {NotFoundCount} tracks could not be matched or lacked a URL.", seedTrackTitle, notFound.Count);
                    return;
                }

                try
                {
                    string timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                    string normalizedSeedTitle = NormalizeFileName(seedTrackTitle);
                    string fileName = $"{normalizedSeedTitle}_{timestamp}.txt";
                    
                    // Consider making the output directory configurable
                    string outputDirectory = Path.Combine(Directory.GetCurrentDirectory(), "SpotifyTracklists");
                    Directory.CreateDirectory(outputDirectory); // Ensure directory exists
                    string filePath = Path.Combine(outputDirectory, fileName);

                    var lines = foundTracksForFile
                        .OrderBy(f => f.Artist) // Optional: order by artist, then title
                        .ThenBy(f => f.Title)
                        .Select(f => $"{f.Artist},{f.Title},{f.SpotifyUrl}")
                        .ToList();
                    
                    await File.WriteAllLinesAsync(filePath, lines);

                    _logger.LogInformation(
                        "Output file ready: {FilePath}. {WrittenCount} tracks written. {SkippedCount} tracks could not be matched or lacked a Spotify URL.",
                        filePath,
                        foundTracksForFile.Count,
                        notFound.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error during file output generation for seed “{Seed}”.", seedTrackTitle);
                }
            }
            else // Playlist creation logic
            {
                if (foundTrackUris.IsEmpty)
                {
                    _logger.LogWarning("Nothing matched on Spotify for seed “{Seed}” – playlist skipped. {NotFoundCount} tracks could not be matched.", seedTrackTitle, notFound.Count);
                    return;
                }

                try
                {
                    var me = await _spotify.UserProfile.Current();
                    var playlistId = await GetOrCreatePlaylistIdAsync(me.Id, seedTrackTitle); // Assuming this method exists
                    
                    // Spotify API limits adding items to a playlist to 100 per request.
                    // Chunk the URIs if necessary.
                    var trackUrisList = foundTrackUris.ToList();
                    const int maxItemsPerRequest = 100;
                    
                    // For replacing content, typically you clear it first then add.
                    // The ReplacePlaylistContentAsync should handle this logic.
                    // If it takes all items at once and handles chunking, great.
                    // Otherwise, you might need to call _spotify.Playlists.ClearItems(playlistId)
                    // and then loop _spotify.Playlists.AddItems(playlistId, new PlaylistAddItemsRequest(chunk))

                    await ReplacePlaylistContentAsync(playlistId, trackUrisList); // Assuming this method exists and handles chunking if needed

                    _logger.LogInformation(
                        "Playlist for seed “{Seed}” ready: {Ok} tracks processed for playlist, {Miss} original tracks not found/matched.",
                        foundTrackUris.Count,
                        notFound.Count);
                }
                catch (APIException apiEx)
                {
                    _logger.LogError(apiEx,
                        "Spotify API error during playlist creation for seed “{Seed}” (status {Status}).",
                        seedTrackTitle,
                        apiEx.Response?.StatusCode);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error during playlist build for seed “{Seed}”.", seedTrackTitle);
                }
            }
        }

        private async Task<string> GetOrCreatePlaylistIdAsync(string userId, string seed)
        {
            var name = $"Soulseek‑Radar · {seed} · {DateTime.Now:yyyy‑MM‑dd}";

            var first   = await _spotify.Playlists.CurrentUsers();
            var all     = await _spotify.PaginateAll(first);
            var current = all.FirstOrDefault(p =>
                            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (current != null)
                return current.Id;

            var created = await _spotify.Playlists.Create(
                              userId,
                              new PlaylistCreateRequest(name)
                              {
                                  Public      = false,
                                  Description = $"Generated {DateTime.Now:yyyy‑MM‑dd HH:mm}"
                              });

            return created.Id;
        }

        private async Task ReplacePlaylistContentAsync(string id, List<string> uris)
        {
            const int batch = 100;
            var offset      = 0;
            var first       = true;

            while (offset < uris.Count)
            {
                var slice = uris.Skip(offset).Take(batch).ToList();
                if (slice.Count == 0) break;

                if (first)
                {
                    await _spotify.Playlists.ReplaceItems(
                              id,
                              new PlaylistReplaceItemsRequest(slice));
                    first = false;
                }
                else
                {
                    await _spotify.Playlists.AddItems(
                              id,
                              new PlaylistAddItemsRequest(slice));
                }

                offset += batch;
            }
        }
    }
}
