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

namespace Spotify.Slsk.Integration.Services.Spotify
{
    public class SpotifyPlaylistService
    {
        private readonly ILogger<SpotifyPlaylistService> _logger;
        private readonly SpotifyAPI.Web.SpotifyClient? _spotify;
        private readonly string? _clientId;
        private readonly string? _clientSecret;
        private readonly string? _refreshToken;

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
        /// Build (or overwrite) a playlist from harvested Soulseek matches.
        /// </summary>
        public async Task CreatePlaylistFromHarvestedTracksAsync(
            string seedTrackTitle,
            List<HarvestedFileInfo> harvestedTracks)
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

            _logger.LogInformation("↻  Building playlist for seed “{Seed}”…", seedTrackTitle);

            var foundTrackUris = new ConcurrentBag<string>();
            var notFound       = new ConcurrentBag<HarvestedFileInfo>();
            var gate           = new SemaphoreSlim(5, 5);

            var searchJobs = harvestedTracks.Select(async track =>
            {
                await gate.WaitAsync();
                try
                {
                    if (string.IsNullOrWhiteSpace(track.Artist) ||
                        string.IsNullOrWhiteSpace(track.Title))
                    {
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
                                     Limit  = 5,
                                     Market = "from_token"
                                 });
                    }
                    catch (APIException apiEx)
                    {
                        _logger.LogError(apiEx,
                            "Search failed for “{Query}” (status {Status}).",
                            query,
                            apiEx.Response?.StatusCode);
                        notFound.Add(track);
                        return;
                    }

                    var best = sr.Tracks?.Items?.FirstOrDefault();
                    if (best != null)
                    {
                        foundTrackUris.Add(best.Uri);
                    }
                    else
                    {
                        notFound.Add(track);
                    }
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(searchJobs);

            if (foundTrackUris.IsEmpty)
            {
                _logger.LogWarning("Nothing matched on Spotify – playlist skipped.");
                return;
            }

            try
            {
                var me         = await _spotify.UserProfile.Current();
                var playlistId = await GetOrCreatePlaylistIdAsync(me.Id, seedTrackTitle);

                await ReplacePlaylistContentAsync(playlistId, foundTrackUris.ToList());

                _logger.LogInformation(
                    "Playlist ready: {Ok} tracks added, {Miss} not found.",
                    foundTrackUris.Count,
                    notFound.Count);
            }
            catch (APIException apiEx)
            {
                _logger.LogError(apiEx,
                    "Spotify API error during playlist run (status {Status}).",
                    apiEx.Response?.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during playlist build.");
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
