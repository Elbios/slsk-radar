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
using Soulseek; // Added for SoulseekClient type
using System.ComponentModel.DataAnnotations;

namespace Spotify.Slsk.Integration.Cli.Commands.SubCommands
{
    [Command("soulseek-radar", Description = "Recommends tracks based on users sharing a seed track on Soulseek.")]
    class SoulseekRadarCommand : SpotSeekCommandBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILoggerFactory _loggerFactory; // Added logger factory

        [Argument(0, Name = "SeedTrack", Description = "The seed track to start discovery (e.g., 'Artist - Title' or Spotify URI)")]
        [Required]
        public string SeedTrack { get; set; } = null!;

        [Option(CommandOptionType.SingleValue, ShortName = "u", LongName = "ssusername", Description = "Soulseek login username", ValueName = "login username", ShowInHelpText = true)]
        public string? SSUsername { get; set; }

        [Option(CommandOptionType.SingleValue, ShortName = "p", LongName = "sspassword", Description = "Soulseek login password", ValueName = "login password", ShowInHelpText = true)]
        public string? SSPassword { get; set; }

        // Inject ILogger, IConsole, IConfiguration, and ILoggerFactory
        public SoulseekRadarCommand(ILogger<SoulseekRadarCommand> logger, IConsole console, IConfiguration configuration, ILoggerFactory loggerFactory)
        {
            _logger = logger; // Base class logger (ILogger<SoulseekRadarCommand>)
            _console = console;
            _configuration = configuration;
            _loggerFactory = loggerFactory; // Store the factory
        }

        protected override async Task<int> OnExecute(CommandLineApplication app)
        {
            _logger.LogInformation("Executing Soulseek-Radar command...");

            LoadCredentials();

            if (string.IsNullOrEmpty(SSUsername) || string.IsNullOrEmpty(SSPassword))
            {
                 _logger.LogError("Soulseek username and password are required.");
                 return 1;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(SeedTrack))
                {
                    _logger.LogError("Seed track cannot be empty.");
                    return 1;
                }

                string seedQuery = SeedTrack;
                _logger.LogDebug("Using seed query: {Query}", seedQuery);

                var soulseekClient = SoulseekService.GetClient(); // Existing static method

                // Create the specific logger type using the factory
                var radarServiceLogger = _loggerFactory.CreateLogger<SoulseekRadarService>();

                // Instantiate the service with the correct logger
                var radarService = new SoulseekRadarService(
                    radarServiceLogger, // Pass the correctly created logger
                    soulseekClient,
                    _configuration
                );

                _logger.LogInformation("Attempting to start Soulseek-Radar discovery..."); // Added log
                await radarService.DiscoverTracksAsync(seedQuery, SSUsername, SSPassword);

                _logger.LogInformation("Soulseek-Radar command finished execution.");
                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during the soulseek-radar command execution.");
                // OnException(ex); // Base class handler might be less informative here
                _console.Error.WriteLine($"ERROR: {ex.Message}"); // Direct error output
                _logger.LogDebug(ex.StackTrace); // Log stack trace for debugging
                return 1;
            }
        }

        private void LoadCredentials()
        {
            try
            {
                if (!string.IsNullOrEmpty(Profile) && System.IO.File.Exists($"{ProfileFolder}{Profile}")) // Check file exists
                {
                    // Accessing UserProfile property triggers loading and decryption
                    if (UserProfile != null)
                    {
                         SSUsername ??= UserProfile.Username;
                         SSPassword ??= UserProfile.Password;
                         _logger.LogDebug("Loaded credentials from profile '{ProfileName}'", Profile);
                    } else {
                         _logger.LogWarning("Profile file '{ProfileName}' loaded but failed to deserialize or decrypt.", Profile);
                    }
                }
                else if (!string.IsNullOrEmpty(Profile))
                {
                     _logger.LogDebug("Profile file not found for '{ProfileName}'. Will prompt if needed.", Profile);
                }
            }
            catch (Exception ex)
            {
                 _logger.LogWarning(ex, "Could not load profile '{ProfileName}'. Will prompt if needed.", Profile);
            }

            if (string.IsNullOrEmpty(SSUsername))
            {
                SSUsername = Prompt.GetString("Soulseek user name:", SSUsername);
            }

            if (string.IsNullOrEmpty(SSPassword))
            {
                SSPassword = SecureStringToString(Prompt.GetPasswordAsSecureString("Soulseek password:"));
                // Consider saving back to profile here if desired
            }
        }
    }
}