// Source/Assemblies/Spotify.Slsk.Integration/Services/SoulseekRadarService.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Spotify.Slsk.Integration.Models;
using Spotify.Slsk.Integration.Models.Exceptions;
using Spotify.Slsk.Integration.Services.SoulSeek;
using Soulseek;
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

        public SoulseekRadarService(ILogger<SoulseekRadarService> logger, SoulseekClient soulseekClient, IConfiguration configuration)
        {
            _logger = logger;
            _soulseekClient = soulseekClient;
            _options = new SoulseekRadarOptions();
            configuration.GetSection("SoulseekRadar").Bind(_options);
            _logger.LogInformation("SoulseekRadarService initialized with options: {@Options}", _options);
        }

        public async Task DiscoverTracksAsync(string seedTrackQuery, string ssUsername, string ssPassword)
        {
            _logger.LogInformation("Starting Soulseek-Radar discovery for seed: '{Seed}'", seedTrackQuery);

            try
            {
                // Connect & login
                await SoulseekService.ConnectAndLoginAsync(_soulseekClient, ssUsername, ssPassword);
                _logger.LogInformation("Connected and logged in.");

                // --- Step 1: Seed Search ---
                _logger.LogInformation("STEP 1: Searching for '{Seed}' (timeout {T}s)", seedTrackQuery, _options.SearchTimeoutSeconds);
                IReadOnlyCollection<SearchResponse> responses;
                try
                {
                    var result = await _soulseekClient.SearchAsync(
                        SearchQuery.FromText(seedTrackQuery),
                        options: new SearchOptions(
                            searchTimeout: _options.SearchTimeoutSeconds * 1000,
                            stateChanged: e => _logger.LogTrace("Search state: {State}", e.Search.State),
                            responseReceived: e => _logger.LogTrace("Received response from {User}", e.Response.Username)
                        )
                    );
                    responses = result.Responses;
                    _logger.LogDebug("SearchAsync completed. State: {State}", result.Search.State);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Search timed out after {T}s for '{Seed}'", _options.SearchTimeoutSeconds, seedTrackQuery);
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
                    _logger.LogWarning("No candidates to proceed with. Exiting.");
                    await DisconnectGracefullyAsync();
                    return;
                }

                // --- Step 2: Stats & select ---
                _logger.LogInformation("STEP 2: Fetching stats (timeout {T}s)...", _options.PerPeerTimeoutSeconds);
                var shareData = new List<(string Username, int Size)>();
                foreach (var u in uniqueUsers
                                    .OrderByDescending(u => u.UploadSpeed)
                                    .Take(_options.MaxUsers * 2))
                {
                    _logger.LogDebug("Fetching stats for {User}", u.Username);
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.PerPeerTimeoutSeconds));
                        var stats = await _soulseekClient.GetUserStatisticsAsync(u.Username, cts.Token);
                        if (stats.FileCount >= MinShareSizeFiles)
                        {
                            shareData.Add((u.Username, stats.FileCount));
                            _logger.LogTrace("  -> {User} has {Count} files", u.Username, stats.FileCount);
                        }
                        else
                        {
                            _logger.LogTrace("  -> Skipping {User}, only {Count} files", u.Username, stats.FileCount);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Skipping stats for {User}", u.Username);
                    }
                }

                if (!shareData.Any())
                {
                    _logger.LogWarning("No users passed share-size filter. Exiting.");
                    await DisconnectGracefullyAsync();
                    return;
                }

                var selected = shareData
                    .OrderBy(x => x.Size)
                    .Take(_options.MaxUsers)
                    .Select(x => x.Username)
                    .ToList();
                _logger.LogInformation("Selected users: {Users}", selected);

                // --- Step 3: Crawl & pick for all selected users ---
                _logger.LogInformation("STEP 3: Crawling shares and selecting up to {N} tracks per user...", RecommendationsPerUser);

                // Pre-fetch entire share for each user and pick
                var allPicks = new Dictionary<string, List<string>>();
                foreach (var user in selected)
                {
                    _logger.LogInformation("Crawling & picking for {User}...", user);
                    var picks = await CrawlAndPickAsync(user, uniqueUsers.First(r => r.Username == user));
                    allPicks[user] = picks;
                    _logger.LogInformation("  {Count} picks: {Tracks}", picks.Count, picks);
                }

                _logger.LogInformation("All picks completed:");
                foreach (var kv in allPicks)
                {
                    _logger.LogInformation(" • {User}: {Tracks}", kv.Key, kv.Value);
                }

                _logger.LogInformation("Discovery finished (picks ready).");
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

        private async Task<List<string>> CrawlAndPickAsync(string username, SearchResponse seedResp)
        {
            var picks = new List<string>();

            // 1) Browse entire share
            var browse = await _soulseekClient.BrowseAsync(username);
            _logger.LogDebug("  BrowseAsync for {User}: {D} dirs, {L} locked", username,
                browse.Directories.Count, browse.LockedDirectories.Count);

            // 2) Normalize seed path
            var rawSeed = seedResp.Files.First().Filename;
            _logger.LogTrace("  Raw seed path: '{Raw}'", rawSeed);

            // Trim any leading @@* segments
            var parts = rawSeed.Split('\\');
            int ti = 0;
            while (ti < parts.Length && parts[ti].StartsWith("@@"))
            {
                _logger.LogTrace("    Trimming '{S}'", parts[ti]);
                ti++;
            }
            var trimmed = parts.Skip(ti).ToArray();
            var normPath = string.Join("\\", trimmed);
            _logger.LogDebug("    Normalized seed path: '{Norm}'", normPath);

            // Split on last backslash
            int idx = normPath.LastIndexOf('\\');
            string seedFolder = idx >= 0 ? normPath.Substring(0, idx) : "";
            string seedFile   = idx >= 0 ? normPath[(idx + 1)..] : normPath;
            _logger.LogDebug("    SeedFolder='{F}', SeedFile='{f}'", seedFolder, seedFile);

            // Find matching directory by ends-with
            var dirs = browse.Directories;
            var matches = dirs
                .Where(d => d.Name.EndsWith(seedFolder, StringComparison.InvariantCultureIgnoreCase))
                .ToList();
            _logger.LogDebug("    Directories ending with '{F}': {C}", seedFolder, matches.Count);

            if (!matches.Any())
            {
                _logger.LogWarning("    No folder matches '{F}'. Listing all:", seedFolder);
                foreach (var d in dirs)
                    _logger.LogWarning("     - '{Dir}'", d.Name);
                return picks;
            }

            var seedDir = matches.First();
            var fileEntry = seedDir.Files
                .FirstOrDefault(f => f.Filename.Equals(seedFile, StringComparison.InvariantCultureIgnoreCase));
            if (fileEntry == null)
            {
                _logger.LogWarning("    Could not find file '{f}' in '{Dir}'", seedFile, seedDir.Name);
                return picks;
            }

            // 3) Traverse up and pick siblings
            var baseArtist = Normalize(seedDir.Name[(seedDir.Name.LastIndexOf('\\') + 1)..]);
            var allDirs = browse.Directories.ToList();
            var locked  = new HashSet<string>(browse.LockedDirectories.Select(d => d.Name));
            var current = seedDir.Name;

            while (picks.Count < RecommendationsPerUser && current.Contains("\\"))
            {
                var parent = current[..current.LastIndexOf('\\')];
                _logger.LogTrace("    Ascend to '{P}'", parent);

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
                    if (artistNorm == baseArtist) continue;

                    var dirObj = allDirs.Single(d => d.Name == sib);
                    var cands = dirObj.Files
                        .Where(f => f.Size > 0)
                        .Select(f => $"{sib}\\{f.Filename}")
                        .Where(fp =>
                        {
                            var ext = Path.GetExtension(fp)?.ToLowerInvariant();
                            return ext is ".mp3" or ".flac" or ".m4a" or ".ogg";
                        })
                        .ToList();

                    if (cands.Any())
                    {
                        var pick = cands[new Random().Next(cands.Count)];
                        picks.Add(pick);
                        _logger.LogInformation("    Picked: {Pick}", pick);
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
                _logger.LogInformation("Disconnecting from Soulseek...");
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
