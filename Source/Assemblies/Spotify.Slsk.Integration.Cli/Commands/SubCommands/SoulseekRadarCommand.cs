// Source/Assemblies/Spotify.Slsk.Integration.Cli/Commands/SubCommands/SoulseekRadarCommand.cs
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Spotify.Slsk.Integration.Models;
using Spotify.Slsk.Integration.Services;
using Spotify.Slsk.Integration.Services.SoulSeek;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Soulseek;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection; // *** ADDED: For GetRequiredService ***

namespace Spotify.Slsk.Integration.Cli.Commands.SubCommands
{
    [Command("soulseek-radar", Description = "Recommends tracks based on users sharing a seed track on Soulseek, creates Spotify playlist.")]
    class SoulseekRadarCommand : SpotSeekCommandBase
    {
        // Dependencies will be injected by the host
        private readonly ILogger<SoulseekRadarCommand> _radarCmdLogger; // Specific logger for this command
        private readonly IServiceProvider _serviceProvider; // To resolve services

        [Argument(0, Name = "SeedTrack", Description = "The seed track to start discovery (e.g., 'Artist - Title' or Spotify URI)")]
        [Required]
        public string SeedTrack { get; set; } = null!;

        [Option(CommandOptionType.SingleValue, ShortName = "u", LongName = "ssusername", Description = "Soulseek login username", ValueName = "login username", ShowInHelpText = true)]
        public string? SSUsername { get; set; }

        [Option(CommandOptionType.SingleValue, ShortName = "p", LongName = "sspassword", Description = "Soulseek login password", ValueName = "login password", ShowInHelpText = true)]
        public string? SSPassword { get; set; }

        [Option(CommandOptionType.SingleValue, ShortName = "o", LongName = "output-json", Description = "Optional path to a JSON file to output results.", ValueName = "FILEPATH", ShowInHelpText = true)]
        public string? OutputJsonPath { get; set; }

        // Inject ILogger, IConsole, and IServiceProvider
        public SoulseekRadarCommand(ILogger<SoulseekRadarCommand> logger, IConsole console, IServiceProvider serviceProvider)
        {
            // Assign base class logger and console if needed, or use the specific one
            _logger = logger; // Assign to base class logger
            _radarCmdLogger = logger; // Keep specific logger if needed for command-specific logs
            _console = console;
            _serviceProvider = serviceProvider; // Store service provider
        }

        protected override async Task<int> OnExecute(CommandLineApplication app)
        {
            _radarCmdLogger.LogInformation("Executing Soulseek-Radar command...");

            LoadCredentials(); // Load credentials using base class logic + prompting

            if (string.IsNullOrEmpty(SSUsername) || string.IsNullOrEmpty(SSPassword))
            {
                 _radarCmdLogger.LogError("Soulseek username and password are required.");
                 return 1;
            }
            if (string.IsNullOrWhiteSpace(SeedTrack))
            {
                _radarCmdLogger.LogError("Seed track cannot be empty.");
                return 1;
            }


            try
            {
                // Resolve the SoulseekRadarService from the DI container
                // This ensures it gets its dependencies (logger, config, soulseek client, spotify service) injected correctly
                var radarService = _serviceProvider.GetRequiredService<SoulseekRadarService>();

                string seedQuery = SeedTrack; // Use the provided argument directly
                _radarCmdLogger.LogDebug("Using seed query: {Query}", seedQuery);
                if (!string.IsNullOrEmpty(OutputJsonPath))
                {
                    _radarCmdLogger.LogInformation("Outputting results to JSON file: {FilePath}", OutputJsonPath);
                }


                _radarCmdLogger.LogInformation("Attempting to start Soulseek-Radar discovery and Spotify playlist creation...");
                // Pass the OutputJsonPath to the service method
                await radarService.DiscoverTracksAsync(seedQuery, SSUsername, SSPassword, OutputJsonPath);

                _radarCmdLogger.LogInformation("Soulseek-Radar command finished execution successfully.");
                return 0;
            }
            catch (Exception ex)
            {
                _radarCmdLogger.LogError(ex, "An error occurred during the soulseek-radar command execution.");
                _console.Error.WriteLine($"ERROR: {ex.Message}");
                // Consider logging stack trace only at Debug level for production
                _radarCmdLogger.LogDebug(ex.StackTrace);
                return 1;
            }
        }

        // LoadCredentials remains the same as provided in the original codebase
        private void LoadCredentials()
        {
            try
            {
                // Use base class ProfileFolder and Profile properties
                string profilePath = Path.Combine(ProfileFolder, Profile);
                if (!string.IsNullOrEmpty(Profile) && System.IO.File.Exists(profilePath))
                {
                    // Accessing UserProfile property triggers loading and decryption
                    if (UserProfile != null) // UserProfile is defined in SpotSeekCommandBase
                    {
                         SSUsername ??= UserProfile.Username;
                         SSPassword ??= UserProfile.Password; // Assumes UserProfile.Password is decrypted by the getter
                         _radarCmdLogger.LogDebug("Loaded credentials from profile '{ProfileName}'", Profile);
                    } else {
                         _radarCmdLogger.LogWarning("Profile file '{ProfileName}' loaded but failed to deserialize or decrypt.", Profile);
                    }
                }
                else if (!string.IsNullOrEmpty(Profile))
                {
                     _radarCmdLogger.LogDebug("Profile file not found: '{ProfilePath}'. Will prompt if needed.", profilePath);
                }
            }
            catch (Exception ex)
            {
                 _radarCmdLogger.LogWarning(ex, "Could not load profile '{ProfileName}'. Will prompt if needed.", Profile);
            }

            // Prompt if still missing
            if (string.IsNullOrEmpty(SSUsername))
            {
                SSUsername = Prompt.GetString("Soulseek user name:", SSUsername);
            }

            if (string.IsNullOrEmpty(SSPassword))
            {
                // Use base class SecureStringToString and Prompt
                SSPassword = SecureStringToString(Prompt.GetPasswordAsSecureString("Soulseek password:"));
                // Optional: Consider saving back to profile here if desired and implemented
            }
        }
    }
}