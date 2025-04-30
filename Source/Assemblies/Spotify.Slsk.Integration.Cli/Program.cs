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
using McMaster.Extensions.Hosting.CommandLine; // Required for RunCommandLineApplicationAsync

namespace Spotify.Slsk.Integration.Cli
{
    internal class Program
    {

        public async static Task<int> Main(string[] args)
        {
            // Ensure configuration path works correctly when published/run from different dirs
            string basePath = AppContext.BaseDirectory; // Use AppContext.BaseDirectory for reliability
            Console.WriteLine($"Base directory for configuration: {basePath}");


            IConfigurationRoot configuration = new ConfigurationBuilder()
                .SetBasePath(basePath) // Use reliable base path
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true) // Make it non-optional
                .AddEnvironmentVariables()
                .Build();

            // Verify configuration loading
            var radarSection = configuration.GetSection("SoulseekRadar");
            if (!radarSection.Exists())
            {
                 Console.WriteLine("Warning: SoulseekRadar section not found in appsettings.json");
            } else {
                 Console.WriteLine($"SoulseekRadar:MaxUsers from config: {radarSection["MaxUsers"]}");
            }


            Log.Logger = new LoggerConfiguration()
               .ReadFrom.Configuration(configuration)
               .Enrich.FromLogContext()
               // .WriteTo.Console() // Console sink might be duplicated if also added via logging below
               .CreateLogger();

             // Wrap host building in try-finally for proper logger disposal
            try
            {
                Log.Information("Starting application host builder..."); // Initial log before host builds

                IHostBuilder builder = Host.CreateDefaultBuilder(args) // Use CreateDefaultBuilder for standard setup
                    .ConfigureAppConfiguration((hostingContext, config) =>
                    {
                        // Clear default providers if necessary, then add our config
                        config.Sources.Clear();
                        config.SetBasePath(basePath);
                        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                        config.AddEnvironmentVariables();
                        // Add command line args if needed: config.AddCommandLine(args);
                    })
                    .ConfigureLogging((context, logging) => // Use ConfigureLogging for better integration
                    {
                        logging.ClearProviders(); // Clear other providers like default ConsoleLogger
                        logging.AddSerilog(Log.Logger); // Add Serilog
                    })
                    .ConfigureServices((hostContext, services) =>
                    {
                        // Add configuration instance for DI
                        services.AddSingleton<IConfiguration>(configuration);

                        // If we needed to register services:
                        // services.AddSingleton<SoulseekClient>(sp => SoulseekService.GetClient()); // Example
                        // services.AddTransient<SoulseekRadarService>(); // Example
                    });


                 Log.Information("Host built. Running command line application...");
                 return await builder.RunCommandLineApplicationAsync<SpotseekCommand>(args);

            }
            catch (Exception ex)
            {
                 // Log exception during host build or run
                 Log.Fatal(ex, "Application terminated unexpectedly during setup or execution.");
                 Console.WriteLine($"Fatal Error: {ex.Message}"); // Also write to console directly
                 return 1;
            }
            finally
            {
                 Log.CloseAndFlush(); // Ensure logs are written before exit
            }
        }
    }
}