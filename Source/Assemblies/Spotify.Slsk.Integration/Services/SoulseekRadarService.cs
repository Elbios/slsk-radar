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
					var searchResult = await _soulseekClient.SearchAsync(
						SearchQuery.FromText(seedTrackQuery),
						options: new SearchOptions(
							searchTimeout: _options.SearchTimeoutSeconds * 1000,
							stateChanged: e => _logger.LogTrace("Search state: {State}", e.Search.State),
							responseReceived: e => _logger.LogTrace("Received response from {User}", e.Response.Username)
						)
					);
					responses = searchResult.Responses;
					_logger.LogDebug("SearchAsync completed. State: {State}", searchResult.Search.State);
				}
				catch (OperationCanceledException)
				{
					_logger.LogWarning("Search timed out after {T}s for '{Seed}'", _options.SearchTimeoutSeconds, seedTrackQuery);
					responses = Array.Empty<SearchResponse>();
				}

                // Dedupe & free-slot filter
                var uniqueUsers = responses
                    .Where(r => r.HasFreeUploadSlot)
                    .GroupBy(r => r.Username)
                    .Select(g => g.OrderByDescending(r => r.UploadSpeed).First())
                    .ToList();
                _logger.LogInformation("Found {Count} unique users with free slots.", uniqueUsers.Count);
                if (!uniqueUsers.Any())
                {
                    _logger.LogWarning("No candidates; exiting.");
                    await DisconnectGracefullyAsync();
                    return;
                }

                // --- Step 2: Stats & select ---
                _logger.LogInformation("STEP 2: Fetching stats (timeout {T}s)...", _options.PerPeerTimeoutSeconds);
                var shareData = new List<(string User,int Size)>();
                foreach (var u in uniqueUsers.OrderByDescending(u => u.UploadSpeed).Take(_options.MaxUsers * 2))
                {
                    _logger.LogDebug("Stats for {User}", u.Username);
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.PerPeerTimeoutSeconds));
                        var stats = await _soulseekClient.GetUserStatisticsAsync(u.Username, cts.Token);
                        if (stats.FileCount >= MinShareSizeFiles)
                            shareData.Add((u.Username, stats.FileCount));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Skipping {User}", u.Username);
                    }
                }
                if (!shareData.Any())
                {
                    _logger.LogWarning("No share-size qualifiers; exiting.");
                    await DisconnectGracefullyAsync();
                    return;
                }

                var selected = shareData.OrderBy(x => x.Size).Take(_options.MaxUsers).ToList();
                _logger.LogInformation("Selected users: {Users}", selected.Select(x => x.User));

                // --- Step 3: Crawl & pick for first user ---
                var firstUser = selected.First().User;
                _logger.LogInformation("STEP 3: Crawling share of {User}", firstUser);

                var browse = await _soulseekClient.BrowseAsync(firstUser);
                _logger.LogDebug("Browse returned {D} dirs", browse.Directories.Count);

                // raw seed-path from search result
                var rawSeed = uniqueUsers.First(r => r.Username == firstUser).Files.First().Filename;
                _logger.LogTrace("Raw seed path: '{Raw}'", rawSeed);

                // trim @@* segments
                var parts = rawSeed.Split('\\');
                int ti = 0;
                while (ti < parts.Length && parts[ti].StartsWith("@@"))
                {
                    _logger.LogTrace("Trimming '{S}'", parts[ti]);
                    ti++;
                }
                var trimmed = parts.Skip(ti).ToArray();
                var normPath = string.Join("\\", trimmed);
                _logger.LogDebug("Normalized seed path: '{Norm}'", normPath);

                // manual split on last backslash
                int idx = normPath.LastIndexOf('\\');
                string seedFolder = idx >= 0 ? normPath.Substring(0, idx) : "";
                string seedFile   = idx >= 0 ? normPath[(idx + 1)..] : normPath;
                _logger.LogDebug("SeedFolder='{F}', SeedFile='{f}'", seedFolder, seedFile);

                // match any directory whose Name endsWith the seedFolder
                var matches = browse.Directories
                    .Where(d => d.Name.EndsWith(seedFolder, StringComparison.InvariantCultureIgnoreCase))
                    .ToList();
                _logger.LogDebug("Directories ending with '{F}': {C}", seedFolder, matches.Count);

                if (!matches.Any())
                {
                    _logger.LogWarning("No directory matches '{F}'. Listing all:", seedFolder);
                    foreach (var d in browse.Directories)
                        _logger.LogWarning(" - '{Dir}'", d.Name);
                }
                else
                {
                    var dir = matches.First();
                    var fileEntry = dir.Files.FirstOrDefault(f => f.Filename.Equals(seedFile, StringComparison.InvariantCultureIgnoreCase));
                    if (fileEntry == null)
                    {
                        _logger.LogWarning("Could not find file '{f}' in directory '{F}'", seedFile, dir.Name);
                    }
                    else
                    {
                        // now pick sibling tracks
                        var baseArtist = Normalize(dir.Name[(dir.Name.LastIndexOf('\\') + 1)..]);
                        var allDirs = browse.Directories.ToList();
                        var locked  = new HashSet<string>(browse.LockedDirectories.Select(x => x.Name));
                        var picks   = new List<string>();
                        var current = dir.Name;
                        while (picks.Count < RecommendationsPerUser && current.Contains("\\"))
                        {
                            var parent = current[..current.LastIndexOf('\\')];
                            _logger.LogTrace("Up to '{P}'", parent);

                            var siblings = allDirs.Select(d => d.Name)
                                .Where(p =>
                                    p.StartsWith(parent + "\\") &&
                                    p.LastIndexOf('\\') == parent.Length)
                                .Except(locked)
                                .Except(new[] { current })
                                .ToList();
                            Shuffle(siblings);

                            foreach (var s in siblings)
                            {
                                var art = Normalize(s[(s.LastIndexOf('\\') + 1)..]);
                                if (art == baseArtist) continue;

                                var dobj = allDirs.Single(d => d.Name == s);
                                var cands = dobj.Files
                                    .Where(f => f.Size > 0)
                                    .Select(f => $"{s}\\{f.Filename}")
                                    .Where(path =>
                                    {
                                        var ext = Path.GetExtension(path)?.ToLowerInvariant();
                                        return ext is ".mp3" or ".flac" or ".m4a" or ".ogg";
                                    })
                                    .ToList();

                                if (cands.Any())
                                {
                                    var pick = cands[new Random().Next(cands.Count)];
                                    picks.Add(pick);
                                    _logger.LogInformation("Picked '{Pick}'", pick);
                                    if (picks.Count == RecommendationsPerUser) break;
                                }
                            }

                            current = parent;
                        }

                        _logger.LogInformation("STEP 3 picks for {U}: {Count} → {List}",
                            firstUser, picks.Count, picks);
                    }
                }

                _logger.LogInformation("Done.");
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
            .Replace('_','-')
            .ToLowerInvariant()
            .Split(new[]{' ','-'},StringSplitOptions.RemoveEmptyEntries)
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
