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
        private readonly Random _random = new Random(); // Use a single Random instance

        private const int MinShareSizeFiles = 50;
        private const int RecommendationsPerUser = 2;
        private const int SimilarityThreshold = 80;      // Fuzzy ratio threshold
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
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during initial search for '{Seed}'", seedTrackQuery);
                    responses = Array.Empty<SearchResponse>();
                }


                // Filter & dedupe
                var uniqueUsers = responses
                    .Where(r => r.HasFreeUploadSlot && r.Files.Any()) // Ensure there are files in the response
                    .GroupBy(r => r.Username)
                    .Select(g => g.OrderByDescending(r => r.UploadSpeed).First())
                    .ToList();
                _logger.LogInformation("Found {Count} unique users with free slots and matching files.", uniqueUsers.Count);
                if (!uniqueUsers.Any())
                {
                    _logger.LogWarning("No suitable candidates found from initial search. Exiting.");
                    await DisconnectGracefullyAsync();
                    return;
                }

                // Step 2: Fetch stats & select
                _logger.LogInformation(
                    "STEP 2: Fetching stats for up to {MaxInitial} users (timeout {T}s per user)...",
                     _options.MaxUsers * 2, _options.PerPeerTimeoutSeconds);

                var shareData = new List<(string Username, int Size)>();
                foreach (var u in uniqueUsers
                    .OrderByDescending(u => u.UploadSpeed)
                    .Take(_options.MaxUsers * 2)) // Check more users initially
                {
                    _logger.LogDebug("Checking stats for {User}", u.Username);
                    try
                    {
                        using var cts = new CancellationTokenSource(
                            TimeSpan.FromSeconds(_options.PerPeerTimeoutSeconds));
                        var stats = await _soulseekClient
                            .GetUserStatisticsAsync(u.Username, cts.Token);

                        if (stats.FileCount >= MinShareSizeFiles)
                        {
                            shareData.Add((u.Username, stats.FileCount));
                            _logger.LogTrace(" -> User {User} has {FileCount} files (meets minimum {Min}). Added to potential list.", u.Username, stats.FileCount, MinShareSizeFiles);
                        }
                        else
                        {
                            _logger.LogTrace(" -> Skipping user {User}: only {FileCount} files (less than minimum {Min})", u.Username, stats.FileCount, MinShareSizeFiles);
                        }
                    }
                    catch (UserOfflineException) {
                         _logger.LogWarning(" -> User {User} appears to be offline while fetching stats. Skipping.", u.Username);
                    }
                    catch (TimeoutException) {
                         _logger.LogWarning(" -> Timed out fetching stats for user {User}. Skipping.", u.Username);
                    }
                    catch (OperationCanceledException) {
                         _logger.LogWarning(" -> Operation cancelled while fetching stats for user {User}. Skipping.", u.Username);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, " -> Error fetching stats for user {User}. Skipping.", u.Username);
                    }
                }

                if (!shareData.Any())
                {
                    _logger.LogWarning("No users passed the share-size filter ({Min} files). Exiting.", MinShareSizeFiles);
                    await DisconnectGracefullyAsync();
                    return;
                }

                // Select users based on share size (preferring smaller shares slightly, but taking top N)
                var selectedUsers = shareData
                    .OrderBy(x => x.Size) // Example: prefer smaller shares among the viable ones
                    .Take(_options.MaxUsers)
                    .Select(x => x.Username)
                    .ToList();
                _logger.LogInformation("Selected {Count} users for crawling: {Users}", selectedUsers.Count, string.Join(", ", selectedUsers));

                // Step 3: Crawl & pick for all selected users
                _logger.LogInformation("STEP 3: Crawling shares and picking up to {N} tracks each from selected users", RecommendationsPerUser);
                var allPicks = new Dictionary<string, List<string>>();

                foreach (var user in selectedUsers)
                {
                    _logger.LogInformation("Processing user {User}...", user);
                    // Find the original search response for this user to get the seed file path
                    var seedResp = uniqueUsers.FirstOrDefault(r => r.Username == user);
                    if (seedResp == null || !seedResp.Files.Any())
                    {
                        _logger.LogWarning(" -> Could not find original search response or file for user {User}. Skipping.", user);
                        continue;
                    }

                    List<string> picks = new List<string>();
                    try
                    {
                         // Use a dedicated CancellationTokenSource for the crawl operation per user
                        using var crawlCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.CrawlTimeoutSeconds)); // Add a crawl timeout
                        picks = await CrawlAndPickAsync(user, seedResp, crawlCts.Token);
                        allPicks[user] = picks;
                        _logger.LogInformation(" -> Finished processing {User}. Found {Count} picks: [{Picks}]", user, picks.Count, string.Join(", ", picks));
                    }
                    catch (UserOfflineException) {
                         _logger.LogWarning(" -> User {User} appears to be offline during crawl. Skipping.", user);
                    }
                    catch (TimeoutException) {
                         _logger.LogWarning(" -> Timed out during crawl/browse for user {User}. Skipping.", user);
                    }
                    catch (OperationCanceledException) {
                         _logger.LogWarning(" -> Crawl operation cancelled for user {User} (likely timeout). Skipping.", user);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, " -> Unexpected error crawling user {User}. Skipping.", user);
                        // Optionally add to a failed list or retry logic here
                    }
                }

                _logger.LogInformation("Discovery finished. Total recommendations gathered: {TotalCount}", allPicks.Sum(kvp => kvp.Value.Count));
                // TODO: Do something with allPicks (e.g., display, save, send to another service)

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred during the discovery process.");
            }
            finally
            {
                await DisconnectGracefullyAsync();
            }
        }

        private async Task<List<string>> CrawlAndPickAsync(
            string username,
            SearchResponse seedResp,
            CancellationToken cancellationToken) // Pass CancellationToken
        {
            var picks = new List<string>();
            const char PathSeparator = '\\'; // Soulseek uses backslash

            // --- 1. Browse entire share ---
            _logger.LogDebug(" -> Browsing full share for user {User}...", username);
            BrowseResponse browse;
            try
            {
                 // Apply timeout/cancellation to the BrowseAsync call itself
                browse = await _soulseekClient.BrowseAsync(username,
                    options: new BrowseOptions(responseTimeout: _options.PerPeerTimeoutSeconds * 1000), // Timeout for initial response
                    cancellationToken: cancellationToken); // Overall cancellation for the browse operation
            }
            catch (Exception ex)
            {
                 _logger.LogError(ex, " -> Failed to browse user {User}", username);
                 throw; // Re-throw to be caught by the main loop's error handling
            }

            var allDirs = browse.Directories.ToDictionary(d => d.Name, d => d); // Use Dictionary for faster lookups
            var lockedDirs = new HashSet<string>(browse.LockedDirectories.Select(d => d.Name));
            _logger.LogDebug(" -> Browse complete for {User}: {DirCount} directories, {LockedCount} locked.",
                username, allDirs.Count, lockedDirs.Count);

            if (!allDirs.Any())
            {
                _logger.LogWarning(" -> User {User} has no browsable directories. Cannot proceed.", username);
                return picks;
            }

            // --- 2. Identify and Normalize Seed Info ---
            var seedFileResult = seedResp.Files.First(); // We know this exists from earlier checks
            var seedFullPath = seedFileResult.Filename; // This is the full path from the search result
            _logger.LogDebug(" -> Raw seed path from search: '{Path}'", seedFullPath);

            int lastSeparatorIndex = seedFullPath.LastIndexOf(PathSeparator);
            if (lastSeparatorIndex < 0)
            {
                _logger.LogWarning(" -> Seed path '{Path}' does not contain a separator. Assuming it's in the root. This might be unusual.", seedFullPath);
                // Handle root case if necessary, though less common for music files.
                // For now, we might not be able to traverse effectively.
                return picks; // Exit if we can't determine a parent directory
            }

            var seedDirectoryPath = seedFullPath.Substring(0, lastSeparatorIndex);
            var seedFileName = seedFullPath.Substring(lastSeparatorIndex + 1);

            _logger.LogDebug(" -> Parsed Seed: Directory='{Dir}', FileName='{File}'", seedDirectoryPath, seedFileName);

            // Find the actual Directory object corresponding to the seed's directory path
            if (!allDirs.TryGetValue(seedDirectoryPath, out var seedDirectoryObject))
            {
                _logger.LogWarning(" -> Could not find the exact directory '{Dir}' in the browse results for user {User}. The share structure might have changed or the search result path was inconsistent.", seedDirectoryPath, username);
                // Attempt fuzzy matching for the directory? Maybe too complex/unreliable.
                return picks;
            }

            // Find the File object within that directory
            var seedFileObject = seedDirectoryObject.Files.FirstOrDefault(f => f.Filename.Equals(seedFileName, StringComparison.OrdinalIgnoreCase));
            if (seedFileObject == null)
            {
                 _logger.LogWarning(" -> Could not find the exact file '{File}' within directory '{Dir}' for user {User}, although the directory was found. Skipping.", seedFileName, seedDirectoryPath, username);
                 return picks;
            }

            // Normalize Seed Title
            var seedTitle = Path.GetFileNameWithoutExtension(seedFileName);
            var seedTitleNorm = Normalize(seedTitle);
            _logger.LogDebug(" -> Normalized Seed Title: '{Orig}' -> '{Norm}'", seedTitle, seedTitleNorm);

            // Determine Seed Artist/Context for Comparison (Using the directory containing the seed file)
            var seedDirectoryName = seedDirectoryPath.Contains(PathSeparator)
                ? seedDirectoryPath.Substring(seedDirectoryPath.LastIndexOf(PathSeparator) + 1)
                : seedDirectoryPath; // If it's a root folder
            var seedDirectoryNameNorm = Normalize(seedDirectoryName);
            _logger.LogDebug(" -> Normalized Seed Directory Name (for context comparison): '{Orig}' -> '{Norm}'", seedDirectoryName, seedDirectoryNameNorm);


            // --- 3. Traverse Upward and Pick Siblings ---
            string currentDirectoryPath = seedDirectoryPath;
            var visitedSiblingDirectories = new HashSet<string>(); // Prevent re-processing siblings if traversal goes very high

            while (picks.Count < RecommendationsPerUser && currentDirectoryPath.Contains(PathSeparator))
            {
                cancellationToken.ThrowIfCancellationRequested(); // Check for cancellation periodically

                var parentDirectoryPath = currentDirectoryPath.Substring(0, currentDirectoryPath.LastIndexOf(PathSeparator));
                _logger.LogTrace(" -> Ascending. Current Level: '{Current}', Parent: '{Parent}'", currentDirectoryPath, parentDirectoryPath);

                // Find sibling directories at the parent level
                var siblingPaths = allDirs.Keys
                    .Where(path => path.StartsWith(parentDirectoryPath + PathSeparator) // Must be under the parent
                                && path.IndexOf(PathSeparator, parentDirectoryPath.Length + 1) == -1 // Must be a direct child (no further separators)
                                && path != currentDirectoryPath // Not the directory we just came from
                                && !lockedDirs.Contains(path) // Not locked
                                && !visitedSiblingDirectories.Contains(path)) // Not already processed
                    .ToList();

                Shuffle(siblingPaths); // Randomize the order of siblings
                _logger.LogTrace(" -> Found {Count} potential sibling directories at '{Parent}': [{Siblings}]", siblingPaths.Count, parentDirectoryPath, string.Join(", ", siblingPaths));

                foreach (var siblingPath in siblingPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    visitedSiblingDirectories.Add(siblingPath); // Mark as visited for this crawl

                    if (!allDirs.TryGetValue(siblingPath, out var siblingDirObject))
                    {
                        _logger.LogWarning(" -> Sibling path '{Path}' not found in dictionary? Skipping.", siblingPath);
                        continue; // Should not happen, but safety check
                    }

                    // Extract sibling directory name for fuzzy comparison against seed context
                    var siblingDirName = siblingPath.Substring(siblingPath.LastIndexOf(PathSeparator) + 1);
                    var siblingDirNameNorm = Normalize(siblingDirName);

                    // Fuzzy check: Compare sibling directory name with seed directory name
                    var dirRatio = Fuzz.Ratio(siblingDirNameNorm, seedDirectoryNameNorm);
                    _logger.LogTrace(" -> Checking Sibling Dir: '{SiblingDir}' (Norm: '{SiblingNorm}') vs Seed Dir: '{SeedDir}' (Norm: '{SeedNorm}'). Ratio: {Ratio}",
                        siblingDirName, siblingDirNameNorm, seedDirectoryName, seedDirectoryNameNorm, dirRatio);

                    if (dirRatio >= SimilarityThreshold)
                    {
                        _logger.LogTrace(" -> Skipping sibling directory '{SiblingDir}' due to high similarity ({Ratio} >= {Threshold}) with seed directory '{SeedDir}'.",
                            siblingDirName, dirRatio, SimilarityThreshold, seedDirectoryName);
                        continue;
                    }

                    // Get valid candidate files from this sibling directory
                    var candidates = siblingDirObject.Files
                        .Where(f => f.Size > 0 && IsAllowedExtension(f.Filename))
                        .Select(f => new { FileObject = f, FullPath = siblingPath + PathSeparator + f.Filename }) // Keep File object for easier access
                        .ToList();

                    _logger.LogTrace(" -> Sibling '{SiblingDir}' has {Count} potential media files.", siblingDirName, candidates.Count);

                    // Filter candidates by title similarity
                    var validCandidates = new List<string>();
                    foreach (var candidate in candidates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var candidateTitle = Path.GetFileNameWithoutExtension(candidate.FileObject.Filename);
                        var candidateTitleNorm = Normalize(candidateTitle);

                        var titleRatio = Fuzz.Ratio(candidateTitleNorm, seedTitleNorm);
                        _logger.LogTrace("    -> Checking Candidate File: '{File}' (Norm: '{NormTitle}') vs Seed Title (Norm: '{NormSeed}'). Ratio: {Ratio}",
                            candidate.FileObject.Filename, candidateTitleNorm, seedTitleNorm, titleRatio);

                        if (titleRatio < SimilarityThreshold)
                        {
                            validCandidates.Add(candidate.FullPath);
                            _logger.LogTrace("      -> Candidate '{File}' is valid (Ratio {Ratio} < {Threshold}).", candidate.FileObject.Filename, titleRatio, SimilarityThreshold);
                        }
                        else
                        {
                             _logger.LogTrace("      -> Skipping candidate '{File}' due to high title similarity (Ratio {Ratio} >= {Threshold}).", candidate.FileObject.Filename, titleRatio, SimilarityThreshold);
                        }
                    }

                    _logger.LogTrace(" -> Found {Count} valid candidates (after title filtering) in '{SiblingDir}'.", validCandidates.Count, siblingDirName);

                    // Pick one random valid candidate if any exist
                    if (validCandidates.Any())
                    {
                        var pick = validCandidates[_random.Next(validCandidates.Count)];
                        picks.Add(pick);
                        _logger.LogInformation(" -> Picked recommendation #{Num}: '{Pick}' from directory '{SiblingDir}'", picks.Count, pick, siblingDirName);

                        if (picks.Count >= RecommendationsPerUser)
                        {
                            _logger.LogDebug(" -> Reached target of {N} recommendations. Stopping search for this user.", RecommendationsPerUser);
                            goto EndTraversal; // Exit both loops
                        }
                    }
                } // End foreach sibling

                // If we finished processing siblings at this level and still need more picks, move up
                currentDirectoryPath = parentDirectoryPath;

            } // End while loop (traversing upwards)

            EndTraversal:; // Label to jump to when enough picks are found

            if (picks.Count < RecommendationsPerUser)
            {
                _logger.LogInformation(" -> Traversed up to '{Path}' but only found {Count}/{Target} recommendations.", currentDirectoryPath, picks.Count, RecommendationsPerUser);
            }

            return picks;
        }


        private async Task DisconnectGracefullyAsync()
        {
            // Check state before attempting disconnect
            if (_soulseekClient != null && _soulseekClient.State != SoulseekClientStates.Disconnected)
            {
                try
                {
                    _logger.LogInformation("Disconnecting Soulseek client...");
                    _soulseekClient.Disconnect();
                    // Give a very brief moment for potential background tasks to clean up, though Disconnect should be synchronous.
                    await Task.Delay(100);
                    _logger.LogInformation("Soulseek client disconnected.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during Soulseek client disconnection.");
                }
            }
            else
            {
                 _logger.LogInformation("Soulseek client already disconnected or not initialized.");
            }
        }

        // Updated Normalize function - removes the FirstOrDefault()
        private static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;

            // Replace underscores, lowercase, split, join back.
            // Consider adding more normalization steps if needed (e.g., removing bracketed text, "feat.", etc.)
            var parts = s.Replace('_', '-')
                         .ToLowerInvariant()
                         .Split(new[] { ' ', '-', '(', ')', '[', ']', '{', '}' }, StringSplitOptions.RemoveEmptyEntries);

            // Simple join for now. Could potentially remove common words like 'the', 'a', 'and' if needed.
            return string.Join(" ", parts);
        }

        private static bool IsAllowedExtension(string filename)
        {
            var ext = Path.GetExtension(filename)?.ToLowerInvariant();
            return !string.IsNullOrEmpty(ext) && AllowedExtensions.Contains(ext);
        }


        // Fisher-Yates shuffle
        private void Shuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }

    // Add or update SoulseekRadarOptions class if needed (e.g., add CrawlTimeoutSeconds)
    public class SoulseekRadarOptions
    {
        public int SearchTimeoutSeconds { get; set; } = 30;
        public int PerPeerTimeoutSeconds { get; set; } = 15;
        public int CrawlTimeoutSeconds { get; set; } = 120; // Timeout for the entire crawl operation per user
        public int MaxUsers { get; set; } = 10;
        // Add other options as needed
    }
}