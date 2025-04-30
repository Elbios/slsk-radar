using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration; // Added for configuration
using Spotify.Slsk.Integration.Models; // For UserProfile
using Spotify.Slsk.Integration.Services; // For SoulseekRadarService
using Spotify.Slsk.Integration.Services.SoulSeek; // For SoulseekService.GetClient()
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;

namespace Spotify.Slsk.Integration.Cli.Commands.SubCommands
{
    [Command("soulseek-radar", Description = "Recommends tracks based on users sharing a seed track on Soulseek.")]
    class SoulseekRadarCommand : SpotSeekCommandBase
    {
        private readonly IConfiguration _configuration; // Inject configuration

        [Argument(0, Name = "SeedTrack", Description = "The seed track to start discovery (e.g., 'Artist - Title' or Spotify URI)")]
        [Required]
        public string SeedTrack { get; set; } = null!;

        [Option(CommandOptionType.SingleValue, ShortName = "u", LongName = "ssusername", Description = "Soulseek login username", ValueName = "login username", ShowInHelpText = true)]
        public string? SSUsername { get; set; } // Made nullable for profile loading

        [Option(CommandOptionType.SingleValue, ShortName = "p", LongName = "sspassword", Description = "Soulseek login password", ValueName = "login password", ShowInHelpText = true)]
        public string? SSPassword { get; set; } // Made nullable for profile loading

        // We can add specific overrides for config values later if needed, e.g.:
        // [Option("--max-users", Description = "Override MaxUsers from config")]
        // public int? MaxUsersOverride { get; set; }

        // Inject ILogger, IConsole, and IConfiguration
        public SoulseekRadarCommand(ILogger<SoulseekRadarCommand> logger, IConsole console, IConfiguration configuration)
        {
            _logger = logger;
            _console = console;
            _configuration = configuration; // Store configuration
        }

        protected override async Task<int> OnExecute(CommandLineApplication app)
        {
            _logger.LogInformation("Executing Soulseek-Radar command...");

            // Load profile or prompt for credentials
            LoadCredentials(); // Refactored credential handling

            if (string.IsNullOrEmpty(SSUsername) || string.IsNullOrEmpty(SSPassword))
            {
                 _logger.LogError("Soulseek username and password are required.");
                 return 1;
            }

            try
            {
                // Basic validation for seed track (can be enhanced later)
                if (string.IsNullOrWhiteSpace(SeedTrack))
                {
                    _logger.LogError("Seed track cannot be empty.");
                    return 1;
                }

                // Resolve Spotify URI later if needed. For now, use the input as search query.
                string seedQuery = SeedTrack;
                // TODO: Add logic here if SeedTrack is a Spotify URI to fetch Artist/Title

                _logger.LogDebug("Using seed query: {Query}", seedQuery);

                // Get the Soulseek client (reuse existing static method)
                var soulseekClient = SoulseekService.GetClient();

                // Instantiate the new service, passing dependencies
                var radarService = new SoulseekRadarService(
                    _logger as ILogger<SoulseekRadarService> ?? new LoggerFactory().CreateLogger<SoulseekRadarService>(), // Handle potential type mismatch
                    soulseekClient,
                    _configuration // Pass configuration
                );

                // Run the discovery process
                await radarService.DiscoverTracksAsync(seedQuery, SSUsername, SSPassword);

                _logger.LogInformation("Soulseek-Radar command finished execution.");
                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during the soulseek-radar command execution.");
                OnException(ex); // Use base class exception handler
                return 1;
            }
        }

        private void LoadCredentials()
        {
            // Try loading from profile first
            try
            {
                if (!string.IsNullOrEmpty(Profile) && UserProfile != null)
                {
                     SSUsername ??= UserProfile.Username;
                     SSPassword ??= UserProfile.Password; // Decryption happens in UserProfile getter
                     _logger.LogDebug("Loaded credentials from profile '{ProfileName}'", Profile);
                }
            }
            catch(FileNotFoundException)
            {
                 _logger.LogDebug("Profile file not found for '{ProfileName}'. Will prompt if needed.", Profile);
            }
            catch (Exception ex)
            {
                 _logger.LogWarning(ex, "Could not load profile '{ProfileName}'. Will prompt if needed.", Profile);
            }


            // Prompt if still missing
            if (string.IsNullOrEmpty(SSUsername))
            {
                SSUsername = Prompt.GetString("Soulseek user name:", SSUsername);
            }

            if (string.IsNullOrEmpty(SSPassword))
            {
                SSPassword = SecureStringToString(Prompt.GetPasswordAsSecureString("Soulseek password:"));
                // Optionally save back to profile if prompted? For now, just use for this run.
            }
        }
    }
}