// Source/Assemblies/Spotify.Slsk.Integration.Cli/Program.cs
using System;
using System.IO;
using System.Threading.Tasks;
using Spotify.Slsk.Integration.Cli.Commands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using McMaster.Extensions.Hosting.CommandLine;
using Spotify.Slsk.Integration.Services.SoulSeek; // For SoulseekService/Client
using Spotify.Slsk.Integration.Services;       // For SoulseekRadarService
using Spotify.Slsk.Integration.Services.Spotify; // *** ADDED: For SpotifyPlaylistService ***
using Soulseek; // For SoulseekClient type

namespace Spotify.Slsk.Integration.Cli
{
    internal class Program
    {

        public async static Task<int> Main(string[] args)
        {
            string basePath = AppContext.BaseDirectory;
            Console.WriteLine($"Base directory for configuration: {basePath}");

            // Build initial configuration for Serilog setup
            IConfigurationRoot configuration = new ConfigurationBuilder()
                .SetBasePath(basePath)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            Log.Logger = new LoggerConfiguration()
               .ReadFrom.Configuration(configuration)
               .Enrich.FromLogContext()
               .CreateLogger();

            try
            {
                Log.Information("Starting application host builder...");

                IHostBuilder builder = Host.CreateDefaultBuilder(args)
                    .ConfigureAppConfiguration((hostingContext, config) =>
                    {
                        config.Sources.Clear();
                        config.SetBasePath(basePath);
                        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                        config.AddEnvironmentVariables();
                    })
                    .ConfigureLogging((context, logging) =>
                    {
                        logging.ClearProviders();
                        logging.AddSerilog(Log.Logger);
                    })
                    .ConfigureServices((hostContext, services) =>
                    {
                        // Add configuration instance for DI
                        services.AddSingleton<IConfiguration>(hostContext.Configuration); // Use hostContext's config

                        // Register SoulseekClient (as Singleton since it maintains connection state)
                        // Consider managing its lifecycle more carefully if needed (e.g., dispose on shutdown)
                        services.AddSingleton<SoulseekClient>(sp => SoulseekService.GetClient());

                        // Register Services
                        services.AddTransient<SpotifyPlaylistService>(); // *** ADDED: Register new Spotify service ***
                        services.AddTransient<SoulseekRadarService>();   // Register Radar service (depends on SpotifyPlaylistService)

                        // Register other services if they were intended to use DI
                        // services.AddTransient<DownloadService>(); // Example if DownloadService used DI
                    });


                 Log.Information("Host built. Running command line application...");
                 // Use the configured host to run the command line app
                 return await builder.RunCommandLineApplicationAsync<SpotseekCommand>(args);

            }
            catch (Exception ex)
            {
                 Log.Fatal(ex, "Application terminated unexpectedly during setup or execution.");
                 Console.WriteLine($"Fatal Error: {ex.Message}");
                 return 1;
            }
            finally
            {
                 Log.CloseAndFlush();
            }
        }
    }
}