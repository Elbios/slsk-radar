using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration; // Added for configuration
using Spotify.Slsk.Integration.Models;
using Spotify.Slsk.Integration.Services.SoulSeek; // Assuming SoulseekService is here
using Soulseek;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System; // Added for DateTime

namespace Spotify.Slsk.Integration.Services
{
    public class SoulseekRadarService
    {
        private readonly ILogger<SoulseekRadarService> _logger;
        private readonly SoulseekClient _soulseekClient;
        private readonly SoulseekRadarOptions _options; // Store loaded options

        // Constructor updated to accept IConfiguration and ILogger
        public SoulseekRadarService(ILogger<SoulseekRadarService> logger, SoulseekClient soulseekClient, IConfiguration configuration)
        {
            _logger = logger;
            _soulseekClient = soulseekClient;

            // Load options from configuration
            _options = new SoulseekRadarOptions();
            configuration.GetSection("SoulseekRadar").Bind(_options); 
            _logger.LogDebug("SoulseekRadarOptions loaded: {@Options}", _options);
        }

        public async Task DiscoverTracksAsync(string seedTrackQuery, string ssUsername, string ssPassword)
        {
            _logger.LogInformation("Starting Soulseek-Radar discovery for seed: '{SeedTrackQuery}'", seedTrackQuery);
            _logger.LogInformation("Using options: MaxUsers={MaxUsers}, SearchTimeout={SearchTimeout}s", _options.MaxUsers, _options.SearchTimeoutSeconds);

            try
            {
                // Ensure connection and login (reuse existing logic)
                await SoulseekService.ConnectAndLoginAsync(_soulseekClient, ssUsername, ssPassword);

                _logger.LogInformation("Performing initial Soulseek search for seed track...");

                // Reuse existing search logic from SoulseekService if possible, or adapt it
                // Assuming SoulseekService has a SearchAsync method similar to the one used in DownloadService
                // We need to pass the specific timeout from our options.
                var (search, responses) = await _soulseekClient.SearchAsync(
                    SearchQuery.FromText(seedTrackQuery),
                    options: new SearchOptions(
                        searchTimeout: _options.SearchTimeoutSeconds * 1000, // Use configured timeout
                        stateChanged: (e) => _logger.LogDebug("Search state changed: {State}", e.Search.State),
                        responseReceived: (e) => _logger.LogTrace("Received response from {Username}", e.Response.Username)
                    )
                );

                _logger.LogInformation("Initial search completed. Found {Count} total responses.", responses.Count);

                // Filter out users without free slots initially, though we might reconsider later
                var usersWithFreeSlots = responses
                    .Where(r => r.HasFreeUploadSlot)
                    .OrderByDescending(r => r.UploadSpeed) // Keep sorting by speed for now
                    .ToList();

                _logger.LogInformation("Found {Count} users with free upload slots.", usersWithFreeSlots.Count);

                // Limit to MaxUsers
                var initialUserCandidates = usersWithFreeSlots.Take(_options.MaxUsers).ToList();

                if (!initialUserCandidates.Any())
                {
                    _logger.LogWarning("No users found sharing '{SeedTrackQuery}' with free slots within the timeout.", seedTrackQuery);
                    return;
                }

                _logger.LogInformation("--- Potential User Candidates (Step 1 - Max {MaxUsers}) ---", _options.MaxUsers);
                foreach (var user in initialUserCandidates)
                {
                    _logger.LogInformation("User: {Username} | Files: {FileCount} | Avg Speed: {Speed} kB/s | Slots Free: {SlotsFree}",
                        user.Username,
                        user.FileCount,
                        user.UploadSpeed, // Assuming this is in kB/s or similar unit
                        user.HasFreeUploadSlot);
                }
                _logger.LogInformation("--- End of User Candidates ---");

                // --- Steps 2-8 will be added below this line in future iterations ---
                _logger.LogInformation("Step 1 (Seed Search & User Identification) complete. Further steps (Share Size Check, Crawling, etc.) are not yet implemented.");


            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during Soulseek-Radar discovery for seed '{SeedTrackQuery}'", seedTrackQuery);
            }
        }

        // Placeholder for future Spotify interaction
        private async Task CreateOrUpdateSpotifyPlaylist(string seedTitle, List<string> trackUris)
        {
            // TODO in Step 7
            _logger.LogInformation("Placeholder: Would create/update Spotify playlist here.");
            await Task.CompletedTask;
        }
    }
}