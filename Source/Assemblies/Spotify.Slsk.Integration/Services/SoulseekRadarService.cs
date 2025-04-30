// Source/Assemblies/Spotify.Slsk.Integration/Services/SoulseekRadarService.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Spotify.Slsk.Integration.Models;
using Spotify.Slsk.Integration.Models.Exceptions;
using Spotify.Slsk.Integration.Services.SoulSeek;
using Soulseek;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
//using Soulseek.Exceptions; // Added for specific exceptions

namespace Spotify.Slsk.Integration.Services
{
    public class SoulseekRadarService
    {
        private readonly ILogger<SoulseekRadarService> _logger;
        private readonly SoulseekClient _soulseekClient;
        private readonly SoulseekRadarOptions _options;

        public SoulseekRadarService(ILogger<SoulseekRadarService> logger, SoulseekClient soulseekClient, IConfiguration configuration)
        {
            _logger = logger;
            _soulseekClient = soulseekClient;
            _options = new SoulseekRadarOptions();
            configuration.GetSection("SoulseekRadar").Bind(_options);
            // Log options right after loading
            _logger.LogInformation("SoulseekRadarService initialized with options: {@Options}", _options);
        }

        public async Task DiscoverTracksAsync(string seedTrackQuery, string ssUsername, string ssPassword)
        {
            _logger.LogInformation("Starting Soulseek-Radar discovery for seed: '{SeedTrackQuery}'", seedTrackQuery);

            try
            {
                _logger.LogDebug("Attempting Soulseek connect and login...");
                await SoulseekService.ConnectAndLoginAsync(_soulseekClient, ssUsername, ssPassword);
                _logger.LogInformation("Soulseek connection and login successful."); // Changed from Debug to Info

                _logger.LogInformation("Performing initial Soulseek search for seed track (Timeout: {SearchTimeout}s)...", _options.SearchTimeoutSeconds);

                IReadOnlyCollection<SearchResponse> responses;
                try
                {
                    var searchResult = await _soulseekClient.SearchAsync(
                        SearchQuery.FromText(seedTrackQuery),
                        options: new SearchOptions(
                            searchTimeout: _options.SearchTimeoutSeconds * 1000,
                            stateChanged: (e) => _logger.LogTrace("Search state changed: {State}", e.Search.State), // Changed to Trace
                            responseReceived: (e) => _logger.LogTrace("Received response from {Username}", e.Response.Username) // Changed to Trace
                        )
                    );
                    responses = searchResult.Responses;
                     _logger.LogDebug("SearchAsync completed. State: {State}", searchResult.Search.State); // Log final search state
                }
                catch (TimeoutException ex)
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

                var usersWithFreeSlots = responses
                    .Where(r => r.HasFreeUploadSlot)
                    .OrderByDescending(r => r.UploadSpeed)
                    .ToList();

                _logger.LogInformation("Found {Count} users with free upload slots.", usersWithFreeSlots.Count);

                var initialUserCandidates = usersWithFreeSlots.Take(_options.MaxUsers).ToList();

                if (!initialUserCandidates.Any())
                {
                    _logger.LogWarning("No users found sharing '{SeedTrackQuery}' with free slots within the timeout and limits.", seedTrackQuery);
                    return; // Exit gracefully if no candidates
                }

                _logger.LogInformation("--- Potential User Candidates (Step 1 - Max {MaxUsers}) ---", _options.MaxUsers);
                foreach (var user in initialUserCandidates)
                {
                    _logger.LogInformation("User: {Username} | Files: {FileCount} | Avg Speed: {Speed} kB/s | Slots Free: {SlotsFree}",
                        user.Username,
                        user.FileCount,
                        user.UploadSpeed,
                        user.HasFreeUploadSlot);
                }
                _logger.LogInformation("--- End of User Candidates ---");

                _logger.LogInformation("Step 1 (Seed Search & User Identification) complete. Further steps (Share Size Check, Crawling, etc.) are not yet implemented.");

            }
            catch (LoginRejectedException ex) // Catch specific login errors
            {
                 _logger.LogError(ex, "Soulseek login failed for user '{Username}'. Reason: {Reason}", ssUsername, ex.Message);
                 // Potentially re-throw or handle differently if needed
            }
            catch (ConnectionException ex) // Catch connection errors
            {
                 _logger.LogError(ex, "Soulseek connection failed. Reason: {Reason}", ex.Message);
            }
            catch (Exception ex) // General catch-all
            {
                _logger.LogError(ex, "An unexpected error occurred during Soulseek-Radar discovery for seed '{SeedTrackQuery}'", seedTrackQuery);
            }
        }

        // Placeholder
        private async Task CreateOrUpdateSpotifyPlaylist(string seedTitle, List<string> trackUris)
        {
            _logger.LogInformation("Placeholder: Would create/update Spotify playlist here.");
            await Task.CompletedTask;
        }
    }
}