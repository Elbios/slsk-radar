// Source/Assemblies/Spotify.Slsk.Integration/Services/SoulseekRadarService.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Spotify.Slsk.Integration.Models;
using Spotify.Slsk.Integration.Models.Exceptions;
using Spotify.Slsk.Integration.Services.SoulSeek;
using Soulseek;
//using Soulseek.Entities; // Added for UserStatistics type
//using Soulseek.Exceptions;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System;

namespace Spotify.Slsk.Integration.Services
{
    public class SoulseekRadarService
    {
        private readonly ILogger<SoulseekRadarService> _logger;
        private readonly SoulseekClient _soulseekClient;
        private readonly SoulseekRadarOptions _options;

        // Minimum share size threshold
        private const int MinShareSizeFiles = 50;

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
            _logger.LogInformation("Starting Soulseek-Radar discovery for seed: '{SeedTrackQuery}'", seedTrackQuery);

            try
            {
                _logger.LogDebug("Attempting Soulseek connect and login...");
                await SoulseekService.ConnectAndLoginAsync(_soulseekClient, ssUsername, ssPassword);
                _logger.LogInformation("Soulseek connection and login successful.");

                // --- Step 1: Foundation, Configuration & Seed Search ---
                _logger.LogInformation("STEP 1: Performing initial Soulseek search for seed track (Timeout: {SearchTimeout}s)...", _options.SearchTimeoutSeconds);

                IReadOnlyCollection<SearchResponse> responses;
                try
                {
                    var searchResult = await _soulseekClient.SearchAsync(
                        SearchQuery.FromText(seedTrackQuery),
                        options: new SearchOptions(
							searchTimeout: _options.SearchTimeoutSeconds * 1000,
                            stateChanged: (e) => _logger.LogTrace("Search state changed: {State}", e.Search.State),
                            responseReceived: (e) => _logger.LogTrace("Received response from {Username}", e.Response.Username)
                        )
                    );
                    responses = searchResult.Responses;
                    _logger.LogDebug("SearchAsync completed. State: {State}", searchResult.Search.State);
                }
                catch (OperationCanceledException ex) // Catch timeout from CancellationToken
                {
                    _logger.LogWarning(ex, "Soulseek search timed out after {Timeout} seconds for query: {Query}", _options.SearchTimeoutSeconds, seedTrackQuery);
                    responses = new List<SearchResponse>(); // Ensure responses is not null
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred during Soulseek search for query: {Query}", seedTrackQuery);
                    throw; // Re-throw other search exceptions
                }

                _logger.LogInformation("Initial search completed. Found {Count} total responses.", responses.Count);

                // Filter for users with free slots first
                var usersWithFreeSlots = responses
                    .Where(r => r.HasFreeUploadSlot)
                    .ToList();

                _logger.LogInformation("Found {Count} users with free upload slots.", usersWithFreeSlots.Count);

                // Group by username to handle multiple responses from the same user
                var uniqueUserResponses = usersWithFreeSlots
                    .GroupBy(r => r.Username)
                    .Select(g => g.OrderByDescending(r => r.UploadSpeed).First()) // Pick one response per user
                    .ToList();

                _logger.LogInformation("Identified {Count} unique users with free slots from search results.", uniqueUserResponses.Count);

                if (!uniqueUserResponses.Any())
                {
                    _logger.LogWarning("No unique users found sharing '{SeedTrackQuery}' with free slots. Cannot proceed.", seedTrackQuery);
                    await DisconnectGracefullyAsync();
                    return;
                }

                _logger.LogInformation("--- Initial Unique User Candidates (Step 1) ---");
                // Log initial candidates (FileCount here is still from the search response, not total share)
                foreach (var user in uniqueUserResponses)
                {
                    _logger.LogInformation("User: {Username} | Matching Files in Resp: {FileCount} | Avg Speed: {Speed} kB/s | Slots Free: {SlotsFree}",
                        user.Username,
                        user.Files.Count, // Log count of files *in this response* for clarity
                        user.UploadSpeed,
                        user.HasFreeUploadSlot);
                }
                _logger.LogInformation("--- End of Initial Candidates ---");
                _logger.LogInformation("STEP 1 (Seed Search & Initial User Identification) complete.");
                // --- End of Step 1 ---


                // --- Step 2: User Selection (Share Size Bias) ---
                _logger.LogInformation("STEP 2: Fetching user statistics and ranking by share size (Timeout per user: {Timeout}s)...", _options.PerPeerTimeoutSeconds);

                var userShareData = new List<(string Username, int ShareSize)>();

                // Limit the number of users we fetch stats for initially, e.g., top N fastest or just MaxUsers*2
                // Taking MaxUsers*2 gives buffer for filtering
                var candidatesToFetchStats = uniqueUserResponses
                                                .OrderByDescending(u => u.UploadSpeed) // Prioritize faster users if we limit fetching
                                                .Take(_options.MaxUsers * 2)
                                                .ToList();

                 _logger.LogInformation("Will attempt to fetch full statistics for up to {Count} candidates.", candidatesToFetchStats.Count);


                foreach (var candidate in candidatesToFetchStats)
                {
                    _logger.LogDebug("Attempting to fetch statistics for user: {Username}", candidate.Username);
                    try
                    {
                        // Use a CancellationToken for the per-peer timeout for fetching stats
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.PerPeerTimeoutSeconds));
                        UserStatistics userStats = await _soulseekClient.GetUserStatisticsAsync(candidate.Username, cts.Token);

                        // Use the FileCount from the fetched statistics
                        int totalFiles = userStats.FileCount; // Correct property is Files

                        _logger.LogInformation("User: {Username} | Fetched Stats -> Files: {TotalFiles}",
                            candidate.Username, totalFiles);

                        if (totalFiles < MinShareSizeFiles)
                        {
                            _logger.LogInformation("Skipping user {Username} - Fetched share size ({TotalFiles} files) is less than {MinFiles}.",
                                candidate.Username, totalFiles, MinShareSizeFiles);
                            continue; // Skip user if share size is too small
                        }

                        // Add user and their actual share size to the list for ranking
                        userShareData.Add((candidate.Username, totalFiles));
                    }
                    catch (OperationCanceledException) // Catches timeout from CancellationTokenSource
                    {
                        _logger.LogWarning("Timeout fetching statistics for user {Username} after {Timeout} seconds. Skipping.", candidate.Username, _options.PerPeerTimeoutSeconds);
                    }
                    catch (Exception ex) // Catch other unexpected errors
                    {
                        _logger.LogWarning(ex, "Failed to fetch statistics for user {Username}. Skipping.", candidate.Username);
                    }
                }

                if (!userShareData.Any())
                {
                    _logger.LogWarning("No suitable users found after fetching statistics and filtering by share size (minimum {MinFiles} files). Cannot proceed.", MinShareSizeFiles);
                    await DisconnectGracefullyAsync();
                    return;
                }

                // Rank by smallest share size (ascending)
                var rankedUsers = userShareData.OrderBy(u => u.ShareSize).ToList();

                // Select the top MaxUsers according to the ranking
                var selectedUsers = rankedUsers.Take(_options.MaxUsers).ToList();

                _logger.LogInformation("--- Selected Users (Step 2 - Ranked by Share Size <= {MaxUsers}) ---", _options.MaxUsers);
                // Log the final selected list
                int rank = 1;
                foreach (var user in selectedUsers)
                {
                    _logger.LogInformation("#{Rank}. User: {Username} | Share Size (Fetched): {ShareSize} files",
                         rank++, user.Username, user.ShareSize);
                }
                 _logger.LogInformation("--- End of Selected Users ---");
                 _logger.LogInformation("STEP 2 (User Selection by Share Size) complete.");
                // --- End of Step 2 ---


                // Placeholder for future steps
                _logger.LogInformation("Further steps (Crawling, Downloading, Spotify Matching, etc.) are not yet implemented.");
                var finalUsernames = selectedUsers.Select(u => u.Username).ToList();
                // This list `finalUsernames` will be used in Step 3

            }
            catch (LoginRejectedException ex)
            {
                 _logger.LogError(ex, "Soulseek login failed for user '{Username}'. Reason: {Reason}", ssUsername, ex.Message);
            }
            catch (ConnectionException ex)
            {
                 _logger.LogError(ex, "Soulseek connection failed. Reason: {Reason}", ex.Message);
            }
            catch (Exception ex) // General catch-all for the whole process
            {
                _logger.LogError(ex, "An unexpected error occurred during Soulseek-Radar discovery for seed '{SeedTrackQuery}'", seedTrackQuery);
            }
            finally
            {
                 // Ensure disconnection even if errors occur
                 await DisconnectGracefullyAsync();
            }
        }

        // Helper method for graceful disconnection
        private async Task DisconnectGracefullyAsync()
        {
            if (_soulseekClient != null && _soulseekClient.State != SoulseekClientStates.Disconnected)
            {
                _logger.LogInformation("Disconnecting from Soulseek server...");
                try
                {
                    _soulseekClient.Disconnect();
                    _logger.LogInformation("Successfully disconnected from Soulseek.");
                }
                catch (OperationCanceledException)
                {
                     _logger.LogWarning("Soulseek disconnection timed out after 5 seconds.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during Soulseek disconnection.");
                }
            }
        }


        // Placeholder - Will be implemented in later steps
        private async Task CreateOrUpdateSpotifyPlaylist(string seedTitle, List<string> trackUris)
        {
            _logger.LogInformation("Placeholder: Would create/update Spotify playlist here.");
            await Task.CompletedTask;
        }
    }
}