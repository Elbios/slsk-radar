
// SoulseekRadarService.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Spotify.Slsk.Integration.Models;
using Spotify.Slsk.Integration.Models.Exceptions;
using Spotify.Slsk.Integration.Services.SoulSeek;
using Soulseek;
using FuzzySharp;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.IO;

namespace Spotify.Slsk.Integration.Services
{
    public class SoulseekRadarService
    {
        private readonly ILogger<SoulseekRadarService> _logger;
        private readonly SoulseekClient _soulseekClient;
        private readonly SoulseekRadarOptions _options;
        private readonly Random _random = new Random();

        private const int MinShareSizeFiles = 50;
        private const int RecommendationsPerUser = 2;
        private const int SimilarityThreshold = 50;
        private static readonly HashSet<string> AllowedExtensions = new HashSet<string> { ".mp3", ".flac", ".m4a", ".ogg" };

        public SoulseekRadarService(
            ILogger<SoulseekRadarService> logger,
            SoulseekClient soulseekClient,
            IConfiguration configuration)
        {
            _logger = logger;
            _soulseekClient = soulseekClient;
            _options = new SoulseekRadarOptions();
            configuration.GetSection("SoulseekRadar").Bind(_options);
            _logger.LogInformation("SoulseekRadarService initialized with options: {@Options}", _options);
        }

        public async Task DiscoverTracksAsync(
            string seedTrackQuery,
            string ssUsername,
            string ssPassword)
        {
            _logger.LogInformation("Starting Soulseek-Radar discovery for seed: '{Seed}'", seedTrackQuery);
            try
            {
                // Connect & login
                await SoulseekService.ConnectAndLoginAsync(_soulseekClient, ssUsername, ssPassword);
                _logger.LogInformation("Connected and logged in.");

                // --- Step 1: Search seed on the network ---
                _logger.LogInformation("STEP 1: Searching for '{Seed}' (timeout {T}s)", seedTrackQuery, _options.SearchTimeoutSeconds);
                IReadOnlyCollection<SearchResponse> responses;
                try
                {
                    var result = await _soulseekClient.SearchAsync(
                        SearchQuery.FromText(seedTrackQuery),
                        options: new SearchOptions(
                            searchTimeout: _options.SearchTimeoutSeconds * 1000,
                            stateChanged: e => _logger.LogTrace("Search state: {State}", e.Search.State),
                            responseReceived: e => _logger.LogTrace("Resp from {User}", e.Response.Username)
                        )
                    );
                    responses = result.Responses;
                    _logger.LogDebug("Search completed. State: {State}", result.Search.State);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Search timed out after {T}s for '{Seed}'", _options.SearchTimeoutSeconds, seedTrackQuery);
                    responses = Array.Empty<SearchResponse>();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during initial search for '{Seed}'", seedTrackQuery);
                    responses = Array.Empty<SearchResponse>();
                }

                var uniqueUsers = responses
                    .Where(r => r.HasFreeUploadSlot && r.Files.Any())
                    .GroupBy(r => r.Username)
                    .Select(g => g.OrderByDescending(r => r.UploadSpeed).First())
                    .ToList();
                _logger.LogInformation("Found {Count} unique users with free slots and matching files.", uniqueUsers.Count);
                if (!uniqueUsers.Any())
                {
                    _logger.LogWarning("No suitable candidates found. Exiting.");
                    await DisconnectGracefullyAsync();
                    return;
                }

                // --- Step 2: Fetch stats & select users ---
                _logger.LogInformation("STEP 2: Fetching stats for up to {MaxCheck} users (timeout {T}s per user)...", _options.MaxUsers * 2, _options.PerPeerTimeoutSeconds);
                var shareData = new List<(string Username, int FileCount)>();
                foreach (var resp in uniqueUsers.OrderByDescending(u => u.UploadSpeed).Take(_options.MaxUsers * 2))
                {
                    _logger.LogDebug("Fetching stats for {User}", resp.Username);
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.PerPeerTimeoutSeconds));
                        var stats = await _soulseekClient.GetUserStatisticsAsync(resp.Username, cts.Token);
                        if (stats.FileCount >= MinShareSizeFiles)
                        {
                            shareData.Add((resp.Username, stats.FileCount));
                            _logger.LogTrace(" -> {User} has {Count} files (>= {Min})", resp.Username, stats.FileCount, MinShareSizeFiles);
                        }
                        else
                        {
                            _logger.LogTrace(" -> Skipping {User}, only {Count} files", resp.Username, stats.FileCount);
                        }
                    }
                    catch (Exception ex) when (ex is UserOfflineException || ex is TimeoutException || ex is OperationCanceledException)
                    {
                        _logger.LogWarning(" -> Unable to fetch stats for {User}: {Msg}", resp.Username, ex.Message);
                    }
                }

                if (!shareData.Any())
                {
                    _logger.LogWarning("No users passed the share-size filter. Exiting.");
                    await DisconnectGracefullyAsync();
                    return;
                }

                var selectedUsers = shareData
                    .OrderBy(x => x.FileCount)
                    .Take(_options.MaxUsers)
                    .Select(x => x.Username)
                    .ToList();
                _logger.LogInformation("Selected users for crawling: {Users}", string.Join(", ", selectedUsers));

                // --- Step 3: Crawl & pick recommendations ---
                _logger.LogInformation("STEP 3: Crawling shares and picking up to {N} tracks each", RecommendationsPerUser);
                var allPicks = new Dictionary<string, List<string>>();

                foreach (var user in selectedUsers)
                {
                    _logger.LogInformation("Processing user {User}...", user);
                    var seedResp = uniqueUsers.First(r => r.Username == user);
                    List<string> picks;
                    try
                    {
                        using var crawlCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.CrawlTimeoutSeconds));
                        picks = await CrawlAndPickAsync(user, seedResp, crawlCts.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error during crawl for {User}", user);
                        continue;
                    }

                    allPicks[user] = picks;
                    _logger.LogInformation(" -> Found {Count} picks for {User}: {Picks}", picks.Count, user, string.Join(", ", picks));
                }

                _logger.LogInformation("Discovery complete. Total recommendations: {Total}", allPicks.Sum(kvp => kvp.Value.Count));
                // TODO: return or use allPicks
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in DiscoverTracksAsync");
            }
            finally
            {
                await DisconnectGracefullyAsync();
            }
        }

        private async Task<List<string>> CrawlAndPickAsync(
            string username,
            SearchResponse seedResp,
            CancellationToken cancellationToken)
        {
            var picks = new List<string>();
            const char Sep = '\\';

            _logger.LogDebug("Browsing share for {User}", username);
            var browse = await _soulseekClient.BrowseAsync(username,
                new BrowseOptions(responseTimeout: _options.PerPeerTimeoutSeconds * 1000),
                cancellationToken);

            var allDirs = browse.Directories.ToDictionary(d => d.Name, d => d);
            var locked = new HashSet<string>(browse.LockedDirectories.Select(d => d.Name));

            // Seed context
            var seedPath = seedResp.Files.First().Filename;
            int idx = seedPath.LastIndexOf(Sep);
            var seedDir = seedPath.Substring(0, idx);
            var seedFile = seedPath[(idx + 1)..];
            var seedAlbum = Normalize(GetLastPathComponent(seedDir));
            var seedArtist = Normalize(GetLastPathComponent(GetParentPath(seedDir)));
            var seedTitle = Normalize(Path.GetFileNameWithoutExtension(seedFile));

            var current = seedDir;
            var visited = new HashSet<string>();

            while (picks.Count < RecommendationsPerUser)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip if folder too similar to full title
                var level = GetLastPathComponent(current);
                if (Fuzz.Ratio(Normalize(level), seedTitle) >= SimilarityThreshold)
                {
                    var up = GetParentPath(current);
                    if (up == null) break;
                    current = up;
                    continue;
                }

                var parent = GetParentPath(current);
                var atRoot = parent == null;

                // Gather siblings or root-fallback children
                var candidates = allDirs.Keys
                    .Where(p => (atRoot ? GetParentPath(p) == current : GetParentPath(p) == parent)
                                && p != current
                                && !locked.Contains(p)
                                && !visited.Contains(p))
                    .ToList();
                Shuffle(candidates);

                foreach (var sib in candidates)
                {
                    visited.Add(sib);
                    var name = GetLastPathComponent(sib);
                    var norm = Normalize(name);
                    if (Fuzz.Ratio(norm, seedArtist) >= SimilarityThreshold ||
                        Fuzz.Ratio(norm, seedAlbum)  >= SimilarityThreshold)
                        continue;

                    // Collect all files under this subtree
                    var files = allDirs
                        .Where(kv => kv.Key == sib || kv.Key.StartsWith(sib + Sep))
                        .SelectMany(kv => kv.Value.Files.Select(f => (kv.Key, f)))
                        .Where(x => x.f.Size > 0 && IsAllowedExtension(x.f.Filename))
                        .ToList();
                    Shuffle(files);

                    foreach (var (dir, file) in files)
                    {
                        var title = Normalize(Path.GetFileNameWithoutExtension(file.Filename));
                        if (Fuzz.Ratio(title, seedTitle) < SimilarityThreshold)
                        {
                            var pick = dir + Sep + file.Filename;
                            picks.Add(pick);
                            _logger.LogInformation("Picked: {Pick}", pick);
                            break;
                        }
                    }

                    if (picks.Count >= RecommendationsPerUser) break;
                }

                if (picks.Count >= RecommendationsPerUser || atRoot) break;
                current = parent;
            }

            return picks;
        }

        private async Task DisconnectGracefullyAsync()
        {
            if (_soulseekClient != null && _soulseekClient.State != SoulseekClientStates.Disconnected)
            {
                _logger.LogInformation("Disconnecting Soulseek client...");
                _soulseekClient.Disconnect();
                await Task.Delay(100);
                _logger.LogInformation("Disconnected.");
            }
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var parts = s.Replace('_', '-')
                         .ToLowerInvariant()
                         .Split(new[] { ' ', '-', '(', ')', '[', ']', '{', '}', '.', ',' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts);
        }

        private static bool IsAllowedExtension(string filename)
        {
            var ext = Path.GetExtension(filename)?.ToLowerInvariant();
            return !string.IsNullOrEmpty(ext) && AllowedExtensions.Contains(ext);
        }

        private void Shuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private static string GetParentPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int idx = path.LastIndexOf("\\");
            if (idx <= 0) return null;
            return path.Substring(0, idx);
        }

        private static string GetLastPathComponent(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int idx = path.LastIndexOf("\\");
            return path.Substring(idx + 1);
        }
    }

    public class SoulseekRadarOptions
    {
        public int SearchTimeoutSeconds { get; set; } = 30;
        public int PerPeerTimeoutSeconds { get; set; } = 15;
        public int CrawlTimeoutSeconds { get; set; } = 120;
        public int MaxUsers { get; set; } = 10;
    }
}
