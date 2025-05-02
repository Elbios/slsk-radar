// SoulseekRadarService.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Spotify.Slsk.Integration.Models;
using Spotify.Slsk.Integration.Models.Exceptions;
using Spotify.Slsk.Integration.Services.SoulSeek;
using Soulseek;
using FuzzySharp;               // <-- Fuzzy matching
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

        private const int MinShareSizeFiles = 50;
        private const int RecommendationsPerUser = 2;
        private const int SimilarityThreshold = 80;      // Fuzzy ratio threshold

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
                await SoulseekService.ConnectAndLoginAsync(_soulseekClient, ssUsername, ssPassword);
                _logger.LogInformation("Connected and logged in.");

                // Step 1: Search seed
                _logger.LogInformation(
                    "STEP 1: Searching for '{Seed}' (timeout {T}s)",
                    seedTrackQuery, _options.SearchTimeoutSeconds);

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
                    _logger.LogWarning("Search timed out after {T}s for '{Seed}'",
                        _options.SearchTimeoutSeconds, seedTrackQuery);
                    responses = Array.Empty<SearchResponse>();
                }

                // Filter & dedupe
                var uniqueUsers = responses
                    .Where(r => r.HasFreeUploadSlot)
                    .GroupBy(r => r.Username)
                    .Select(g => g.OrderByDescending(r => r.UploadSpeed).First())
                    .ToList();
                _logger.LogInformation("Found {Count} unique users with free slots.", uniqueUsers.Count);
                if (!uniqueUsers.Any())
                {
                    _logger.LogWarning("No candidates. Exiting.");
                    await DisconnectGracefullyAsync();
                    return;
                }

                // Step 2: Fetch stats & select
                _logger.LogInformation(
                    "STEP 2: Fetching stats (timeout {T}s)...", _options.PerPeerTimeoutSeconds);

                var shareData = new List<(string Username, int Size)>();
                foreach (var u in uniqueUsers
                    .OrderByDescending(u => u.UploadSpeed)
                    .Take(_options.MaxUsers * 2))
                {
                    _logger.LogDebug("Stats for {U}", u.Username);
                    try
                    {
                        using var cts = new CancellationTokenSource(
                            TimeSpan.FromSeconds(_options.PerPeerTimeoutSeconds));
                        var stats = await _soulseekClient
                            .GetUserStatisticsAsync(u.Username, cts.Token);

                        if (stats.FileCount >= MinShareSizeFiles)
                        {
                            shareData.Add((u.Username, stats.FileCount));
                            _logger.LogTrace(" → {U}: {F} files", u.Username, stats.FileCount);
                        }
                        else
                        {
                            _logger.LogTrace(" → Skipping {U}: only {F} files", u.Username, stats.FileCount);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Skipping stats for {U}", u.Username);
                    }
                }

                if (!shareData.Any())
                {
                    _logger.LogWarning("No users passed share-size filter. Exiting.");
                    await DisconnectGracefullyAsync();
                    return;
                }

                var selectedUsers = shareData
                    .OrderBy(x => x.Size)
                    .Take(_options.MaxUsers)
                    .Select(x => x.Username)
                    .ToList();
                _logger.LogInformation("Selected users: {Users}", selectedUsers);

                // Step 3: Crawl & pick for all
                _logger.LogInformation("STEP 3: Crawling shares and picking up to {N} tracks each", RecommendationsPerUser);
                var allPicks = new Dictionary<string, List<string>>();

                foreach (var user in selectedUsers)
                {
                    _logger.LogInformation("Processing user {U}...", user);
                    var seedResp = uniqueUsers.First(r => r.Username == user);
                    var picks = await CrawlAndPickAsync(user, seedResp);
                    allPicks[user] = picks;
                    _logger.LogInformation(" → {Count} picks: {List}", picks.Count, picks);
                }

                _logger.LogInformation("Discovery finished, recommendations ready!");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Discovery error");
            }
            finally
            {
                await DisconnectGracefullyAsync();
            }
        }

        private async Task<List<string>> CrawlAndPickAsync(
            string username,
            SearchResponse seedResp)
        {
            var picks = new List<string>();

            // 1) Browse entire share
            var browse = await _soulseekClient.BrowseAsync(username);
            _logger.LogDebug("Browse for {U}: {D} dirs, {L} locked",
                username, browse.Directories.Count, browse.LockedDirectories.Count);

            // 2) Normalize seed path & artist/title
            var raw = seedResp.Files.First().Filename;
            _logger.LogTrace(" Raw seed: '{Raw}'", raw);

            // Trim leading @@*
            var segs = raw.Split('\\');
            int ti = 0;
            while (ti < segs.Length && segs[ti].StartsWith("@@"))
                ti++;
            var trimmed = segs.Skip(ti).ToArray();
            var normPath = string.Join("\\", trimmed);
            _logger.LogDebug(" Norm path: '{P}'", normPath);

            int idx = normPath.LastIndexOf('\\');
            var seedFolder = idx >= 0 ? normPath[..idx] : "";
            var seedFile   = idx >= 0 ? normPath[(idx + 1)..] : normPath;
            _logger.LogDebug(" Folder='{F}', File='{f}'", seedFolder, seedFile);

            var dirs = browse.Directories;
            var matches = dirs
                .Where(d => d.Name.EndsWith(seedFolder, StringComparison.InvariantCultureIgnoreCase))
                .ToList();
            if (!matches.Any())
            {
                _logger.LogWarning(" No folder matches '{F}'. Available:", seedFolder);
                foreach (var d in dirs) _logger.LogWarning("  - '{N}'", d.Name);
                return picks;
            }

            var seedDir = matches.First();
            var entry   = seedDir.Files
                .FirstOrDefault(f => f.Filename.Equals(seedFile, StringComparison.InvariantCultureIgnoreCase));
            if (entry == null)
            {
                _logger.LogWarning(" Could not find '{f}' in '{D}'", seedFile, seedDir.Name);
                return picks;
            }

            // Pre‐compute normalized tokens
            var seedArtist = Normalize(seedDir.Name[(seedDir.Name.LastIndexOf('\\') + 1)..]);
            var seedTitle  = Normalize(Path.GetFileNameWithoutExtension(seedFile));
            _logger.LogTrace(" SeedArtist='{A}', SeedTitle='{T}'",
                seedArtist, seedTitle);

            // 3) Traverse upward & pick siblings
            var allDirs = browse.Directories.ToList();
            var locked  = new HashSet<string>(browse.LockedDirectories.Select(d => d.Name));
            var current = seedDir.Name;

            while (picks.Count < RecommendationsPerUser && current.Contains("\\"))
            {
                var parent = current[..current.LastIndexOf('\\')];
                _logger.LogTrace(" Ascend to '{P}'", parent);

                var siblings = allDirs.Select(d => d.Name)
                    .Where(p =>
                        p.StartsWith(parent + "\\") &&
                        p.LastIndexOf('\\') == parent.Length)
                    .Except(locked)
                    .Except(new[] { current })
                    .ToList();
                Shuffle(siblings);

                foreach (var sib in siblings)
                {
                    var artistNorm = Normalize(sib[(sib.LastIndexOf('\\') + 1)..]);
                    // Skip exact or fuzzy-similar artist
                    if (artistNorm == seedArtist ||
                        Fuzz.Ratio(artistNorm, seedArtist) >= SimilarityThreshold)
                    {
                        _logger.LogTrace("  Skip artist '{A}' (too similar)", artistNorm);
                        continue;
                    }

                    var dirObj = allDirs.Single(d => d.Name == sib);
                    var candidates = dirObj.Files
                        .Where(f => f.Size > 0)
                        .Select(f => $"{sib}\\{f.Filename}")
                        .Where(path =>
                        {
                            var ext = Path.GetExtension(path)?.ToLowerInvariant();
                            if (ext is not (".mp3" or ".flac" or ".m4a" or ".ogg"))
                                return false;

                            var titleNorm = Normalize(
                                Path.GetFileNameWithoutExtension(path));
                            // Skip exact or fuzzy-similar title
                            return titleNorm != seedTitle &&
                                   Fuzz.Ratio(titleNorm, seedTitle) < SimilarityThreshold;
                        })
                        .ToList();

                    _logger.LogTrace("  {Count} valid candidates in '{S}'", candidates.Count, sib);
                    if (candidates.Any())
                    {
                        var pick = candidates[new Random().Next(candidates.Count)];
                        picks.Add(pick);
                        _logger.LogInformation("  Picked: {Pick}", pick);
                        if (picks.Count == RecommendationsPerUser) break;
                    }
                }

                current = parent;
            }

            return picks;
        }

        private async Task DisconnectGracefullyAsync()
        {
            if (_soulseekClient?.State != SoulseekClientStates.Disconnected)
            {
                _logger.LogInformation("Disconnecting...");
                _soulseekClient.Disconnect();
                _logger.LogInformation("Disconnected.");
            }
        }

        private static string Normalize(string s) =>
            (s ?? "")
            .Replace('_', '-')
            .ToLowerInvariant()
            .Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "";

        private static void Shuffle<T>(IList<T> list)
        {
            var rnd = new Random();
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
