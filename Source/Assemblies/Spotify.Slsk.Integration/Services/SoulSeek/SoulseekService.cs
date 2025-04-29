using Spotify.Slsk.Integration.Extensions;
using Spotify.Slsk.Integration.Models;
using Soulseek;
using Soulseek.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using Serilog;
using Spotify.Slsk.Integration.Models.Spotify;
using System.Runtime.InteropServices;

namespace Spotify.Slsk.Integration.Services.SoulSeek
{
    public class SoulseekService
    {
        private static int Index = 0;
        private const string Mp3Extension = ".MP3";
        private const string FlacExtension = ".FLAC";

        private static readonly string CURRENT_DIRECTORY = System.IO.Directory.GetParent(System.IO.Directory.GetCurrentDirectory())!.FullName;
        private static string RESULTS_DIRECTORY { get; set; } = default!;
        private static string TRACKS_DIRECTORY { get; set; } = default!;
        private static string SUCCESSFUL_DOWNLOADS_DIRECTORY { get; set; } = default!;
        private static string FAILED_DOWNLOADS_DIRECTORY { get; set; } = default!;

        private static ConcurrentDictionary<(string Username, string Filename, int Token), (TransferStates State, string desiredFileName, int index)> Downloads { get; set; }
            = new ConcurrentDictionary<(string Username, string Filename, int Token), (TransferStates State, string desiredFileName, int index)>();

        private static readonly List<int> ServerPorts = new()
        {
            2416,
            2242,
            2271,
            2300,
            2329,
            2358,
            2387,
            2445,
            2474,
            2503,
            5113,
            5055,
            4939,
            4997
        };

        public SoulseekService()
        {
        }

        public static SoulseekClient GetClient()
        {
            SoulseekClientOptions options = new(
                minimumDiagnosticLevel: DiagnosticLevel.Info,
                peerConnectionOptions: new ConnectionOptions(connectTimeout: 30000, inactivityTimeout: 15000),
                transferConnectionOptions: new ConnectionOptions(connectTimeout: 30000, inactivityTimeout: 15000));
            return new SoulseekClient(options);
        }

        public static async Task ConnectAndLoginAsync(SoulseekClient client, string username, string password)
        {
            if (client.State.Equals(SoulseekClientStates.Disconnected))
            {
                Log.Information("\nConnecting...");
                bool isConnected = false;
                foreach (int port in ServerPorts.Shuffle())
                {
                    try
                    {
                        await client.ConnectAsync("server.slsknet.org", port, username, password, default);
                        isConnected = true;
                        break;
                    }
                    catch (TimeoutException)
                    {
                        Log.Warning($"Time out connecting using port '{port}', trying different one...");
                    }
                }

                if (!isConnected)
                {
                    throw new Exception("Connecting to Soulseek failed, please wait a couple of minutes and try again");
                }
                await client.SetSharedCountsAsync(1, 1);
                Log.Information("\rConnected and logged in.    \n");
            }
        }

        public static async Task<SoulseekResult> GetTrackAsync(SoulseekClient client, TrackToDownload trackToDownload, string username, string password,
            SoulseekOptions options, string? folderName = null)
        {
            client.StateChanged += Client_ServerStateChanged!;

            SoulseekResult result = new();

            RemoveQueuedDownloads();

            //create directories
            if (RESULTS_DIRECTORY == default)
            {
                CreateResultsDirectories(folderName);
            }

            if (options.SkipResults)
            {
                if (IsResultFilePresent(trackToDownload.Query!))
                {
                    Log.Information($"track with query '{trackToDownload.Query}' already processed before, skipping...");
                    return result;
                }
            }


            await ConnectAndLoginAsync(client, username, password);
            IEnumerable<SearchResponse> responses = await SearchAsync(client, trackToDownload.Query!, 1, options.SearchTimeout);
            SearchResponse? response = await SelectSearchResponse(client, responses, options);

            if (response == null)
            {
                Log.Error($"Could not find matching response for '{trackToDownload.Query}'.");
                CreateFailedDownloadFile(trackToDownload.Query!, FailedDownloadReason.NoResponse);
                return result;
            }

            Soulseek.File? file = SelectFile(response.Files, options, trackToDownload.Track?.Track!.DurationMs / 1000);
            if (file == null)
            {
                Log.Error($"could not find matching file for '{trackToDownload.Query}'.");
                CreateFailedDownloadFile(trackToDownload.Query!, FailedDownloadReason.NoFile);
                return result;
            }

            
            string desiredFileName = trackToDownload.Track == null ? file.Filename.RemoveSpecialCharacters() : GetDesiredFileName(trackToDownload.Track!.Track!);
            result = await DownloadFileAsync(client, response.Username, file.Filename, desiredFileName);
            bool success = result.Success;

            if (!success)
            {
                Log.Error($"Downloading file '{trackToDownload.Query}' failed.");
                CreateFailedDownloadFile(trackToDownload.Query!, FailedDownloadReason.FailedDownload);
            }
            else
            {
                Log.Information($"Downloading file '{trackToDownload.Query}' succeeded.");
                CreateSuccessfulDownloadFile(trackToDownload.Query!);
            }

            client.StateChanged -= Client_ServerStateChanged!;
            return result;
        }

        private static string GetDesiredFileName(Track track)
        {
            return track.Artists![0].Name + " - " + track.Album!.Name + " - " + track.Name;
        }

        private static async Task<IEnumerable<SearchResponse>> SearchAsync(SoulseekClient client, string searchText, int minimumFileCount = 0, int searchTimeout = 0)
        {
            bool complete = false;
            int totalResponses = 0;
            int totalFiles = 0;
            SearchStates state = SearchStates.None;


            using (System.Timers.Timer? timer = new(1000))
            {

                timer.Elapsed += (e, a) => updateStatus();

                void updateStatus()
                {
                    Log.Information($"\r  {(complete ? $"Search for '{searchText}' complete." : $"Searching for '{searchText}':")} found {totalFiles} files from {totalResponses} users" + (complete ? "\n" : string.Empty));
                }

                timer.Start();

                (Search search, IReadOnlyCollection<SearchResponse> responses) =
                    await client.SearchAsync(SearchQuery.FromText(searchText),
                    options: new SearchOptions(
                        responseLimit: 250,
                        fileLimit: 100,
                        filterResponses: false,
                        minimumResponseFileCount: minimumFileCount,
                        searchTimeout: searchTimeout * 1000,
                        stateChanged: (e) => state = e.Search.State,
                        responseReceived: (e) =>
                        {
                            totalResponses++;
                            totalFiles += e.Response.FileCount;
                        }));

                timer.Stop();
                complete = true;
                updateStatus();
                responses = responses
                        .Where(response => response.HasFreeUploadSlot)
                        .OrderByDescending(r => r.UploadSpeed).ToList();

                return responses;
            }
        }

		// Source/Assemblies/Spotify.Slsk.Integration/Services/SoulSeek/SoulseekService.cs
		private static async Task<SearchResponse?> SelectSearchResponse(SoulseekClient client, IEnumerable<SearchResponse> responses, SoulseekOptions options)
		{
			// Define the file validation predicate based on options
			Func<Soulseek.File, bool> fileValidator;
			if (options.AllowFlac)
			{
				fileValidator = file => IsValidMp3(file) || IsValidFlac(file);
			}
			else
			{
				fileValidator = IsValidMp3;
			}

			// Filter responses that contain at least one valid file according to the validator
			// The fix in IsValidMp3/IsValidFlac should prevent Nullable errors here.
			var responsesWithValidFiles = responses.Where(response => response.Files.Any(fileValidator));


			// Further filter based on user info (queue length, free slots)
			List<SearchResponse> filteredResponses = new();
			foreach (SearchResponse response in responsesWithValidFiles) // Iterate through responses already known to have a potentially valid file
			{
				try
				{
					// UserInfo check might be slow or fail, handle gracefully
					UserInfo userInfo = await client.GetUserInfoAsync(response.Username);
					// Use the user-modified queue length check (500)
					if (userInfo.QueueLength < 500 && userInfo.HasFreeUploadSlot)
					{
						filteredResponses.Add(response);
					}
					else
					{
						 // Optional: Log why a user was skipped
						 // Log.Debug($"Skipping user {response.Username}: QueueLength={userInfo.QueueLength}, HasFreeSlot={userInfo.HasFreeUploadSlot}");
					}
				}
				catch (Exception ex)
				{
					// Log the error but continue checking other users
					Log.Warning($"Could not get user info for {response.Username}: {ex.Message}");
				}
			}

			// Return the first response that meets all criteria (already ordered by speed in SearchAsync)
			// Or potentially order by other criteria here if needed, e.g., average speed again if GetUserInfoAsync provided it.
			// The original code returns the first match from the filtered list.
			return filteredResponses.FirstOrDefault();
		}

		private static bool IsValidMp3(Soulseek.File file)
		{
			const int minimalBitRate = 320;

			// Check if file, BitRate, and Length have values before using them
			if (file == null || !file.BitRate.HasValue || !file.Length.HasValue || file.Length.Value <= 0)
			{
				return false; // Cannot validate if essential info is missing or invalid
			}

			// Now it's safe to access .Value
			bool bitrateOk = file.BitRate.Value >= minimalBitRate;
			// Recalculate bitrate using size/length to double-check metadata consistency
			bool recalculatedBitrateOk = (file.Size * 8 / file.Length.Value / 1000) >= minimalBitRate;
			bool extensionOk = file.Filename.ToUpper().EndsWith(Mp3Extension)
							   && (string.IsNullOrEmpty(file.Extension) || file.Extension.ToUpper() == Mp3Extension.TrimStart('.')); // Trim '.' for comparison

			return bitrateOk && recalculatedBitrateOk && extensionOk;
		}


		private static bool IsValidFlac(Soulseek.File file)
		{
			if (file == null) {
				return false;
			}
			// Basic check for FLAC extension
			return file.Filename.ToUpper().EndsWith(FlacExtension)
				   && (string.IsNullOrEmpty(file.Extension) || file.Extension.ToUpper() == FlacExtension.TrimStart('.'));
		}

// Source/Assemblies/Spotify.Slsk.Integration/Services/SoulSeek/SoulseekService.cs

		private static Soulseek.File? SelectFile(IEnumerable<Soulseek.File> files, SoulseekOptions options, int? expectedTrackLength = null)
		{
			IEnumerable<Soulseek.File> validFiles;

			// Pick right file based on options (MP3 or MP3+FLAC)
			if (options.AllowFlac)
			{
				validFiles = files.Where(file => IsValidMp3(file) || IsValidFlac(file));
			}
			else
			{
				validFiles = files.Where(IsValidMp3);
			}

			// Filter for expected length of song based on spotify, if provided
			if (expectedTrackLength != null)
			{
				const int lengthToleranceMs = 5000; // Increased tolerance to 5 seconds
				// IMPORTANT: Check HasValue before comparing
				validFiles = validFiles.Where(file => file.Length.HasValue
												 && Math.Abs(file.Length.Value - expectedTrackLength.Value) < lengthToleranceMs);
			}

			// Sort on size descending so that we get the biggest file (assuming this has the highest quality)
			// For FLAC vs MP3, FLAC usually larger, so prefer FLAC if both exist and are valid.
			// If AllowFlac is true, prioritize FLAC.
			if (options.AllowFlac)
			{
				 return validFiles
						.OrderByDescending(file => file.Filename.ToUpper().EndsWith(FlacExtension)) // Prioritize FLAC
						.ThenByDescending(file => file.Size) // Then by size
						.FirstOrDefault();
			}
			else // Only MP3s considered
			{
				return validFiles
						.OrderByDescending(file => file.Size)
						.FirstOrDefault();
			}
		}

        private static void RemoveQueuedDownloads()
        {
            foreach (KeyValuePair<(string Username, string Filename, int Token), (TransferStates State, string Query, int Index)> download in Downloads)
            {
                if (download.Value.State.HasFlag(TransferStates.Queued))
                {
                    bool isRemoved = Downloads.TryRemove(download.Key, out (TransferStates State, string Query, int Index) removedValue);
                    if (isRemoved)
                    {
                        CreateFailedDownloadFile(download.Value.Query, FailedDownloadReason.Queued);
                    }
                    else
                    {
                        Log.Warning($"Unable to remove download '{removedValue.Query}', this could cause a blocked thread.");
                    }
                }
            }
        }

        private static async Task<SoulseekResult> DownloadFileAsync(SoulseekClient client, string username, string file, string desiredFileName)
        {
            SoulseekResult result = new();
            string prettyFileName;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                prettyFileName = file.Split(@"\")[^1];
            }
            else
            {
                prettyFileName = file.Split(@"/")[^1];
            }
            Log.Information($"\nDownloading '{prettyFileName}' from user '{username}'...\n");

            try
            {
                string? completeFilePath = Path.Combine(TRACKS_DIRECTORY, prettyFileName);

                Transfer? transfer = await client.DownloadAsync(username, file, completeFilePath, startOffset: 0, token: Index++, options: new TransferOptions(stateChanged: (e) =>
                {
                    (string Username, string Filename, int Token) key = (e.Transfer.Username, e.Transfer.Filename, e.Transfer.Token);
                    (TransferStates State, string desiredFileName, int index) progress = Downloads.GetOrAdd(key, (e.Transfer.State, desiredFileName, Index));
                    progress.State = e.Transfer.State;

                    Downloads.AddOrUpdate(key, progress, (k, v) => progress);

                    if (progress.State.HasFlag(TransferStates.Completed))
                    {
                        Log.Debug("Download was already complete????"); // new line
                    }
                }, progressUpdated: (e) =>
                {
                    (string Username, string Filename, int Token) key = (e.Transfer.Username, e.Transfer.Filename, e.Transfer.Token);
                    Downloads.TryGetValue(key, out (TransferStates State, string query, int index) progress);

                    progress.State = e.Transfer.State;

                    string? status = $"{$"{Downloads.Where(d => d.Value.State.HasFlag(TransferStates.Completed)).Count() + 1}".PadLeft(Downloads.Count.ToString().Length)}/{Downloads.Count}"; // [ 1/17]

                    Downloads.AddOrUpdate(key, progress, (k, v) => progress);

                    int longest = Downloads.Max(d => Path.GetFileName(d.Key.Filename.ToLocalOSPath()).Length);
                    string? fn = Path.GetFileName(e.Transfer.Filename.ToLocalOSPath()).PadRight(longest);

                    string? size = $"{e.Transfer.BytesTransferred.ToMB()}/{e.Transfer.Size.ToMB()}".PadLeft(15);
                    string? percent = $"({e.Transfer.PercentComplete,3:N0}%)";

                    string? elapsed = e.Transfer.ElapsedTime.HasValue ? e.Transfer.ElapsedTime.Value.ToString(@"m\:ss") : "--:--";
                    string? remaining = e.Transfer.RemainingTime.HasValue ? e.Transfer.RemainingTime.Value.ToString(@"m\:ss") : "--:--";

                    Console.Write($"\r {fn}  {size}  {percent}  [{status}]  {e.Transfer.AverageSpeed.ToMB()}/s {elapsed} / {remaining}");

                }, disposeInputStreamOnCompletion: true, disposeOutputStreamOnCompletion: true)).ConfigureAwait(false);

                // GetDirectoryName() and GetFileName() only work when the path separator is the same as the current OS' DirectorySeparatorChar.
                // normalize for both Windows and Linux by replacing / and \ with Path.DirectorySeparatorChar.
                file = file.ToLocalOSPath();
                Log.Information($"\nDownload of '{prettyFileName}' complete.");
                string destFileName = $"{completeFilePath.Replace(prettyFileName, desiredFileName)}.mp3";
                System.IO.File.Move(completeFilePath, destFileName);
                result.Success = true;
                result.FilePath = destFileName;
            }
            catch (Exception ex)
            {
                Log.Error($"Error downloading {prettyFileName}: {ex.Message}");
            }

            return result;

        }

        private static void CreateResultsDirectories(string? folderName = null)
        {
            RESULTS_DIRECTORY = Path.Combine(CURRENT_DIRECTORY, "Results");

            if (folderName != null)
            {
                RESULTS_DIRECTORY = Path.Combine(RESULTS_DIRECTORY, folderName);
            }
            
            TRACKS_DIRECTORY = Path.Combine(RESULTS_DIRECTORY, "Tracks");
            SUCCESSFUL_DOWNLOADS_DIRECTORY = Path.Combine(RESULTS_DIRECTORY, "Success");
            FAILED_DOWNLOADS_DIRECTORY = Path.Combine(RESULTS_DIRECTORY, "Failed");

            if (!System.IO.Directory.Exists(TRACKS_DIRECTORY))
            {
                System.IO.Directory.CreateDirectory(TRACKS_DIRECTORY);
            }

            if (!System.IO.Directory.Exists(SUCCESSFUL_DOWNLOADS_DIRECTORY))
            {
                System.IO.Directory.CreateDirectory(SUCCESSFUL_DOWNLOADS_DIRECTORY);
            }

            if (!System.IO.Directory.Exists(FAILED_DOWNLOADS_DIRECTORY))
            {
                System.IO.Directory.CreateDirectory(FAILED_DOWNLOADS_DIRECTORY);
            }

            CreateFailedDownloadReasonDirectories();
        }

        private static void CreateFailedDownloadReasonDirectories()
        {
            foreach (FailedDownloadReason reason in Enum.GetValues<FailedDownloadReason>())
            {
                string path = Path.Combine(FAILED_DOWNLOADS_DIRECTORY, reason.ToString());
                if (!System.IO.Directory.Exists(path))
                {
                    System.IO.Directory.CreateDirectory(path);
                }
            }
        }

        private static void Client_ServerStateChanged(object sender, SoulseekClientStateChangedEventArgs e)
        {
            if (e.State == SoulseekClientStates.Disconnected)
            {
                Log.Error("\n×  Disconnected from server" + (!string.IsNullOrEmpty(e.Message) ? $": {e.Message}" : "."));
            }
        }

        private static void CreateSuccessfulDownloadFile(string query)
        {
            System.IO.File.Create(GetSuccessfulDownloadPath(query));
        }


        private static void CreateFailedDownloadFile(string query, FailedDownloadReason failedDownloadReason)
        {
            System.IO.File.Create(GetFailedDownloadPath(query!, failedDownloadReason));
        }

        private static string GetSuccessfulDownloadPath(string query)
        {
            return Path.Combine(SUCCESSFUL_DOWNLOADS_DIRECTORY, $"{query}.success");
        }

        private static string GetFailedDownloadPath(string query, FailedDownloadReason failedDownloadReason)
        {
            return Path.Combine(FAILED_DOWNLOADS_DIRECTORY, $"{failedDownloadReason.ToString()}", $"{query}.failed");
        }

        private static bool IsResultFilePresent(string query)
        {
            return IsFailedDownloadFilePresent(query) || IsSuccessfulDownloadFilePresent(query);
        }

        private static bool IsSuccessfulDownloadFilePresent(string query)
        {
            return System.IO.File.Exists(GetSuccessfulDownloadPath(query));
        }

        private static bool IsFailedDownloadFilePresent(string query)
        {
            foreach (FailedDownloadReason reason in Enum.GetValues<FailedDownloadReason>())
            {
                if (System.IO.File.Exists(GetFailedDownloadPath(query, reason)))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
