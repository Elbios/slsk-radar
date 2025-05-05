// Source/Assemblies/Spotify.Slsk.Integration/Services/SoulseekRadarService.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Spotify.Slsk.Integration.Models;
using Spotify.Slsk.Integration.Models.Exceptions;
using Spotify.Slsk.Integration.Services.SoulSeek; // Keep this using
using Soulseek;
using FuzzySharp;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Collections.Concurrent;
using TagLib; // Added for ID3 tags
using System.Diagnostics; // For Stopwatch if needed, though Transfer object has timing
using System.Text.RegularExpressions; // Added for Regex preprocessing

namespace Spotify.Slsk.Integration.Services
{
    public class SoulseekRadarService
    {
        private readonly ILogger<SoulseekRadarService> _logger;
        private readonly SoulseekClient _soulseekClient;
        private readonly SoulseekRadarOptions _options;
        private readonly Random _random = new Random();
		// Define the Soulseek path separator explicitly
		private const char SoulseekSeparator = '\\';
        private const int MinShareSizeFiles = 50;
        // *** CHANGE 2: Define Max Share Size ***
        private const int MaxShareSizeFiles = 700000;
        // Similarity threshold for fuzzy matching (lower means MORE dissimilar items are picked)
        private const int SimilarityThreshold = 53;
        private static readonly HashSet<string> AllowedExtensions = new HashSet<string> { ".mp3", ".flac", ".m4a", ".ogg" };

        // Regex for removing content within parentheses or brackets
        private static readonly Regex ParenthesesContentRegex = new Regex(@"\[.*?\]|\(.*?\)", RegexOptions.Compiled);
        // Removed DigitWordRegex as specific first-word logic is now used in PreprocessForFuzzyMatch

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

       // Only showing DiscoverTracksAsync and methods it calls that changed
        public async Task DiscoverTracksAsync(
            string seedTrackQuery,
            string ssUsername,
            string ssPassword)
        {
            _logger.LogInformation("Starting Soulseek-Radar discovery for seed: '{Seed}'", seedTrackQuery);
            var overallCts = new CancellationTokenSource(); // Overall cancellation for the entire operation

            try
            {
                // Connect & login (Using SoulseekService static method is fine)
                await SoulseekService.ConnectAndLoginAsync(_soulseekClient, ssUsername, ssPassword); // Assuming this exists and works
                _logger.LogInformation("Connected and logged in.");

                // --- Step 1: Search seed on the network ---
                _logger.LogInformation("STEP 1: Searching for '{Seed}' (timeout {T}s)", seedTrackQuery, _options.SearchTimeoutSeconds);
                IReadOnlyCollection<SearchResponse> responses;
                try
                {
                    var searchOptions = new SearchOptions(
                        searchTimeout: _options.SearchTimeoutSeconds * 1000,
                        stateChanged: e => _logger.LogTrace("Search state: {State}", e.Search.State),
                        responseReceived: e => _logger.LogTrace("Resp from {User}", e.Response.Username)
                    );
                    var result = await _soulseekClient.SearchAsync(
                        SearchQuery.FromText(seedTrackQuery),
                        options: searchOptions,
                        cancellationToken: overallCts.Token
                    );
                    responses = result.Responses;
                    _logger.LogDebug("Search completed. State: {State}", result.Search.State);
                }
                catch (OperationCanceledException) when (!overallCts.IsCancellationRequested)
                {
                    _logger.LogWarning("Search timed out after {T}s for '{Seed}'", _options.SearchTimeoutSeconds, seedTrackQuery);
                    responses = Array.Empty<SearchResponse>();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during initial search for '{Seed}'", seedTrackQuery);
                    responses = Array.Empty<SearchResponse>();
                }

                var uniqueUsers = responses
                    .Where(r => r.HasFreeUploadSlot && r.Files.Any())
                    .GroupBy(r => r.Username)
                    .Select(g => g.OrderByDescending(r => r.UploadSpeed).First())
                    .ToList();
                _logger.LogInformation("Found {Count} unique users with free slots and matching files.", uniqueUsers.Count);
                if (!uniqueUsers.Any())
                {
                    _logger.LogWarning("No suitable candidates found matching seed. Exiting.");
                    await DisconnectGracefullyAsync();
                    return;
                }


                // --- Step 2: Fetch stats & select users ---
                 _logger.LogInformation("STEP 2: Fetching stats for up to {MaxCheck} users (timeout {T}s per user)...", _options.MaxUsers * 2, _options.PerPeerTimeoutSeconds);
                var shareData = new List<(string Username, int FileCount)>();
                var usersToCheck = uniqueUsers.OrderByDescending(u => u.UploadSpeed).Take(Math.Min(uniqueUsers.Count, _options.MaxUsers * 2)).ToList();

                foreach (var resp in usersToCheck)
                {
                    if (overallCts.IsCancellationRequested) break;
                    _logger.LogDebug("Fetching stats for {User}", resp.Username);
                    try
                    {
                        using var statsCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
                        statsCts.CancelAfter(TimeSpan.FromSeconds(_options.PerPeerTimeoutSeconds));
                        var stats = await _soulseekClient.GetUserStatisticsAsync(resp.Username, statsCts.Token);
                        _logger.LogDebug(" -> User {User} has {TotalFiles} total files in share.", resp.Username, stats.FileCount);

                        if (stats.FileCount >= MaxShareSizeFiles)
                        {
                            _logger.LogInformation(" -> Skipping {User}, share size ({Count}) exceeds limit ({Limit}).", resp.Username, stats.FileCount, MaxShareSizeFiles);
                            continue;
                        }

                        if (stats.FileCount >= MinShareSizeFiles)
                        {
                            shareData.Add((resp.Username, stats.FileCount));
                            _logger.LogTrace(" -> {User} meets minimum share size ({Count} >= {Min})", resp.Username, stats.FileCount, MinShareSizeFiles);
                        }
                        else
                        {
                            _logger.LogDebug(" -> Skipping {User}, only {Count} files (less than {Min})", resp.Username, stats.FileCount, MinShareSizeFiles);
                        }
                    }
                    catch (OperationCanceledException) when (!overallCts.IsCancellationRequested) { _logger.LogWarning(" -> Timeout fetching stats for {User}", resp.Username); }
                    catch (Exception ex) when (ex is UserOfflineException || ex is TimeoutException || ex is SoulseekClientException) { _logger.LogWarning(" -> Unable to fetch stats for {User}: {Msg}", resp.Username, ex.Message); }
                    catch (Exception ex) { _logger.LogError(ex, " -> Unexpected error fetching stats for {User}", resp.Username); }
                }
                if (overallCts.IsCancellationRequested) { _logger.LogWarning("Operation cancelled during user stats fetching."); await DisconnectGracefullyAsync(); return; }
                if (!shareData.Any()) { _logger.LogWarning("No users passed the share-size filters (min: {MinFiles}, max: {MaxFiles}). Exiting.", MinShareSizeFiles, MaxShareSizeFiles); await DisconnectGracefullyAsync(); return; }

                var selectedUsers = shareData.OrderBy(x => x.FileCount).Take(_options.MaxUsers).Select(x => x.Username).ToList();
                _logger.LogInformation("Selected {Count} users for crawling: {Users}", selectedUsers.Count, string.Join(", ", selectedUsers));
                int perUserQuota = (selectedUsers.Count <= _options.MaxUsers / 2 && _options.MaxUsers > 0) ? _options.MaxPerUserQuotaSmallU : _options.PerUserQuotaLargeU;
                _logger.LogInformation("Calculated per-user quota: {Quota} (based on {SelectedCount} selected users)", perUserQuota, selectedUsers.Count);


                // --- Step 3: Crawl & pick recommendations ---
				_logger.LogInformation("STEP 3: Crawling shares and picking up to {N} potential tracks per user", perUserQuota);
				var allPotentialDownloads = new List<(string Username, string RemoteFilePath)>();
				foreach (var user in selectedUsers)
				{
					if (overallCts.IsCancellationRequested) break;
					_logger.LogInformation("===> Processing user {User}...", user);
					var seedResp = uniqueUsers.FirstOrDefault(r => r.Username == user);
					if (seedResp == null || !seedResp.Files.Any())
					{
						_logger.LogWarning("Could not find original search response or files for user {User}. Skipping crawl.", user);
						continue;
					}
					var seedFileEntry = seedResp.Files.First();

					List<string> picks;
					using var crawlCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
					try
					{
						crawlCts.CancelAfter(TimeSpan.FromSeconds(_options.CrawlTimeoutSeconds));
						picks = await CrawlAndPickAsync(user, seedFileEntry, perUserQuota, crawlCts.Token);
					}
					catch (OperationCanceledException) when (!overallCts.IsCancellationRequested && crawlCts.IsCancellationRequested)
					{
						_logger.LogWarning("Timeout ({Timeout}s) during crawl for {User}", _options.CrawlTimeoutSeconds, user);
						continue;
					}
					catch (OperationCanceledException) when (overallCts.IsCancellationRequested)
					{
						_logger.LogWarning("Crawl cancelled (overall operation) for {User}", user);
						break;
					}
					catch (Exception ex)
					{
						_logger.LogWarning(ex, "Error during crawl for {User}", user);
						continue;
					}
					if (picks.Any())
					{
                        var validPicks = picks.Where(p => !string.IsNullOrEmpty(p)).ToList();
						allPotentialDownloads.AddRange(validPicks.Select(p => (user, p))); // 'p' should now be the full path
						_logger.LogInformation(" -> Found {Count} potential picks for {User}: {Picks}", validPicks.Count, user, string.Join("; ", validPicks.Select(p => Path.GetFileName(p)))); // Log only filename for brevity
					}
					else
					{
						_logger.LogInformation(" -> Found no suitable picks for {User}", user);
					}
				}
                if (overallCts.IsCancellationRequested) { _logger.LogWarning("Operation cancelled during user share crawling."); await DisconnectGracefullyAsync(); return; }
                _logger.LogInformation("Step 3 complete. Total potential downloads identified: {Total}", allPotentialDownloads.Count);
                if (!allPotentialDownloads.Any()) { _logger.LogWarning("No potential tracks identified across all selected users. Exiting."); await DisconnectGracefullyAsync(); return; }


                // --- Step 4: Parallel Harvesting & Metadata Extraction ---
                _logger.LogInformation("STEP 4: Starting parallel download and metadata extraction...");
                _logger.LogInformation(" -> Global Concurrency Limit: {Limit}", _options.GlobalDownloadConcurrency);
                _logger.LogInformation(" -> Per-User Success Quota (incl. ID3): {Quota}", perUserQuota);
                _logger.LogInformation(" -> Individual Download Timeout: {Timeout}s", _options.PerPeerTimeoutSeconds);
                _logger.LogInformation(" -> Overall Track Limit: {Limit}", _options.OverallTrackLimit);

                var downloadSemaphore = new SemaphoreSlim(_options.GlobalDownloadConcurrency, _options.GlobalDownloadConcurrency);
                var userSuccessCounters = new ConcurrentDictionary<string, int>(selectedUsers.ToDictionary(u => u, u => 0));
                var successfulHarvests = new ConcurrentBag<HarvestedFileInfo>();

                var downloadTasks = new List<Task>();
                Shuffle(allPotentialDownloads);

                foreach (var target in allPotentialDownloads)
                {
                    if (overallCts.IsCancellationRequested) break;
                    if (string.IsNullOrEmpty(target.RemoteFilePath))
                    {
                        _logger.LogWarning("Skipping potential download for user {User} due to null or empty remote path.", target.Username);
                        continue;
                    }

                    var task = Task.Run(async () =>
                    {
                        string tempFilePath = string.Empty;
                        string renamedTempFilePath = string.Empty;
                        bool semaphoreAcquired = false;
                        Transfer? transferResult = null;
                        Stopwatch downloadStopwatch = new Stopwatch();
                        string currentFullRemotePath = target.RemoteFilePath; // Capture for logging in case of failure

                        try
                        {
                            // 1. Acquire Concurrency Slot & Check Quota
                            _logger.LogTrace("Waiting for semaphore slot for {User} - {File}", target.Username, Path.GetFileName(currentFullRemotePath));
                            await downloadSemaphore.WaitAsync(overallCts.Token);
                            semaphoreAcquired = true;
                            _logger.LogTrace("Semaphore slot acquired for {User} - {File}", target.Username, Path.GetFileName(currentFullRemotePath));
                            overallCts.Token.ThrowIfCancellationRequested();

                            if (userSuccessCounters.TryGetValue(target.Username, out var currentCount) && currentCount >= perUserQuota)
                            {
                                _logger.LogDebug("Skipping download for {User} - {File}: User quota ({Quota}) already met.", target.Username, Path.GetFileName(currentFullRemotePath), perUserQuota);
                                return;
                            }

                            // 2. Prepare for Download
                            tempFilePath = Path.GetTempFileName();
                            // Log the FULL path being used
                            _logger.LogDebug("Preparing download: {User} -> FULL REMOTE PATH: '{RemotePath}' to '{LocalTempPath}'", target.Username, currentFullRemotePath, tempFilePath);

                            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
                            downloadCts.CancelAfter(TimeSpan.FromSeconds(_options.PerPeerTimeoutSeconds));

                            var transferOptions = new TransferOptions(
                                stateChanged: args => {
                                    if (args.Transfer == null) {
                                        _logger.LogWarning("Download State Changed: Transfer object is NULL. Previous State: {PrevState}. Target: {User} - {File}", args.PreviousState, target.Username, Path.GetFileName(currentFullRemotePath));
                                        return;
                                    }
                                    if (!args.Transfer.State.HasFlag(TransferStates.Completed)) { _logger.LogTrace("Download State: {User} - {File} -> {State}", args.Transfer.Username, Path.GetFileName(args.Transfer.Filename), args.Transfer.State); }
                                    else { _logger.LogDebug("Download Final State: {User} - {File} -> {State} (Prev: {PrevState})", args.Transfer.Username, Path.GetFileName(args.Transfer.Filename), args.Transfer.State, args.PreviousState); }
                                },
                                progressUpdated: args => {
                                    if (args.Transfer == null) {
                                         _logger.LogWarning("Download Progress Updated: Transfer object is NULL. Target: {User} - {File}", target.Username, Path.GetFileName(currentFullRemotePath));
                                        return;
                                    }
                                    _logger.LogTrace("Download Progress: {User} - {File} | {Percent:F1}% ({Bytes}/{TotalBytes}) @ {Speed:F1} kB/s", args.Transfer.Username, Path.GetFileName(args.Transfer.Filename), args.Transfer.PercentComplete, args.Transfer.BytesTransferred, args.Transfer.Size, args.Transfer.AverageSpeed / 1024.0);
                                }
                            );

                            // 3. Execute Download
                            _logger.LogInformation("Starting download: {User} - '{File}'", target.Username, Path.GetFileName(currentFullRemotePath));
                            downloadStopwatch.Start();
                            try
                            {
                                transferResult = await _soulseekClient.DownloadAsync(
                                    username: target.Username,
                                    remoteFilename: currentFullRemotePath, // Use the full path
                                    localFilename: tempFilePath,
                                    options: transferOptions,
                                    cancellationToken: downloadCts.Token);
                            }
                            catch (Exception ex) {
                                downloadStopwatch.Stop();
                                string logFileName = Path.GetFileName(currentFullRemotePath); // Use captured path for logging
                                if (ex is OperationCanceledException && !overallCts.IsCancellationRequested) { _logger.LogWarning("Download timed out ({Timeout}s) during call: {User} - '{File}'", _options.PerPeerTimeoutSeconds, target.Username, logFileName); }
                                else if (ex is OperationCanceledException && overallCts.IsCancellationRequested) { _logger.LogWarning("Download cancelled (overall) during call: {User} - '{File}'", target.Username, logFileName); }
                                else if (ex is UserOfflineException uoEx) { _logger.LogWarning("Download failed (User Offline) during call: {User} - '{File}'. {Msg}", target.Username, logFileName, uoEx.Message); }
                                else if (ex is TransferRejectedException trEx) { _logger.LogWarning("Download failed (Rejected by Peer) during call: {User} - '{File}'. Reason: '{Msg}'. Full Remote Path Attempted: '{FullPath}'", target.Username, logFileName, trEx.Message, currentFullRemotePath); }
                                else if (ex is SoulseekClientException scEx) { _logger.LogWarning(scEx, "Download failed (Soulseek Error) during call: {User} - '{File}'", target.Username, logFileName); }
                                else if (ex is IOException ioEx) { _logger.LogError(ioEx, "Download failed (IO Error) during call: {User} - '{File}'", target.Username, logFileName); }
                                else { _logger.LogError(ex, "Download failed (Unexpected Error) during call: {User} - '{File}'", target.Username, logFileName); }
                            }
                            finally { if (downloadStopwatch.IsRunning) downloadStopwatch.Stop(); }

                            // 4. Process Download Result
                            bool downloadSucceeded = transferResult != null &&
                                                     transferResult.State.HasFlag(TransferStates.Completed) &&
                                                     transferResult.State.HasFlag(TransferStates.Succeeded);

                            if (downloadSucceeded)
                            {
                                _logger.LogInformation("Download Task Completed: SUCCESS - {User} - '{File}' in {Duration:N1}s. Size: {Size} bytes. State: {State}",
                                    target.Username, Path.GetFileName(currentFullRemotePath), downloadStopwatch.Elapsed.TotalSeconds, transferResult!.Size, transferResult.State);

                                long tempFileSize = 0;
                                bool tempFileExists = false;
                                try {
                                    if (System.IO.File.Exists(tempFilePath)) {
                                        var fileInfo = new FileInfo(tempFilePath);
                                        tempFileSize = fileInfo.Length;
                                        tempFileExists = true;
                                    }
                                } catch (Exception ex) { _logger.LogWarning(ex, "Could not get FileInfo for temporary file {TempPath}", tempFilePath); }

                                if (!tempFileExists || tempFileSize == 0) {
                                     _logger.LogWarning("Download SUCCESS reported by library, but temp file is missing or empty! '{TempPath}'. Skipping ID3.", tempFilePath);
                                } else if (tempFileSize != transferResult.Size) {
                                     _logger.LogWarning("Download SUCCESS reported, but temp file size ({TempSize}) differs from transfer size ({TransferSize})! '{TempPath}'. Proceeding with caution.", tempFileSize, transferResult.Size, tempFilePath);
                                } else {
                                     _logger.LogDebug("Temp file verified: Exists and size ({Size} bytes) matches transfer record. Proceeding to ID3 extraction.", tempFileSize);
                                }

                                // 5. Attempt ID3 Extraction
                                if (tempFileExists && tempFileSize > 0)
                                {
                                    string originalExtension = Path.GetExtension(currentFullRemotePath);
                                    if (string.IsNullOrEmpty(originalExtension) || originalExtension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                                    {
                                        _logger.LogWarning("Could not determine original file extension for '{RemotePath}'. Cannot rename temp file. Skipping ID3.", currentFullRemotePath);
                                    }
                                    else
                                    {
                                        renamedTempFilePath = Path.ChangeExtension(tempFilePath, originalExtension);
                                        bool renameSuccess = false;
                                        try
                                        {
                                            _logger.LogDebug("Renaming '{Source}' to '{Dest}' for TagLib.", tempFilePath, renamedTempFilePath);
                                            System.IO.File.Move(tempFilePath, renamedTempFilePath, true);
                                            renameSuccess = true;
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError(ex, "Failed to rename temporary file from '{Source}' to '{Dest}'. Skipping ID3.", tempFilePath, renamedTempFilePath);
                                        }

                                        if (renameSuccess)
                                        {
                                            try
                                            {
                                                _logger.LogDebug("Extracting ID3 tags from RENAMED file: {RenamedPath}", renamedTempFilePath);
                                                using var tagFile = TagLib.File.Create(renamedTempFilePath);

                                                string artist = tagFile.Tag.FirstPerformer ?? tagFile.Tag.FirstAlbumArtist ?? string.Empty;
                                                string title = tagFile.Tag.Title ?? Path.GetFileNameWithoutExtension(currentFullRemotePath);
                                                string album = tagFile.Tag.Album ?? string.Empty;

                                                if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title)) {
                                                    _logger.LogWarning("ID3 Extraction Warning: Could not extract Artist or Title from {RenamedPath}. Skipping harvest.", renamedTempFilePath);
                                                } else {
                                                    var harvestedInfo = new HarvestedFileInfo {
                                                        Username = target.Username, RemoteFilePath = currentFullRemotePath,
                                                        LocalTempPath = renamedTempFilePath,
                                                        Artist = artist.Trim(), Title = title.Trim(), Album = album.Trim(),
                                                        FileSize = transferResult.Size
                                                    };
                                                    _logger.LogInformation("ID3 Extracted Successfully: {Info}", harvestedInfo);
                                                    successfulHarvests.Add(harvestedInfo);

                                                    // 6. Increment User Success Count
                                                    var newCount = userSuccessCounters.AddOrUpdate(target.Username, 1, (key, count) => count + 1);
                                                    _logger.LogDebug("Incremented success count (incl. ID3) for {User} to {Count}/{Quota}", target.Username, newCount, perUserQuota);
                                                }
                                            }
                                            catch (CorruptFileException ex) { _logger.LogWarning(ex, "ID3 Extraction Failed (Corrupt File): {User} - '{File}' from {RenamedPath}", target.Username, Path.GetFileName(currentFullRemotePath), renamedTempFilePath); }
                                            catch (UnsupportedFormatException ex) { _logger.LogWarning(ex, "ID3 Extraction Failed (Unsupported Format): {User} - '{File}' from {RenamedPath}", target.Username, Path.GetFileName(currentFullRemotePath), renamedTempFilePath); }
                                            catch (Exception ex) { _logger.LogError(ex, "ID3 Extraction Failed (Unexpected Error): {User} - '{File}' from {RenamedPath}", target.Username, Path.GetFileName(currentFullRemotePath), renamedTempFilePath); }
                                        }
                                    }
                                }
                            }
                            else // Download Failed
                            {
                                string reason = transferResult?.State.ToString() ?? "Unknown (Transfer object null)";
                                string? exceptionMessage = null;
                                try { exceptionMessage = transferResult?.Exception?.GetBaseException()?.Message; } catch { /* Ignore */ }

                                _logger.LogWarning("Download Task Completed: FAILED - {User} - '{File}'. Final State: {Reason}. Duration: {Duration:N1}s. Exception: {Exception}. Full Remote Path Attempted: '{FullPath}'",
                                    target.Username, Path.GetFileName(currentFullRemotePath), reason, downloadStopwatch.Elapsed.TotalSeconds, exceptionMessage ?? "N/A", currentFullRemotePath);
                            }
                        }
                        catch (OperationCanceledException) {
                             string logFileName = Path.GetFileName(currentFullRemotePath);
                            if (overallCts.IsCancellationRequested) { _logger.LogWarning("Download task cancelled (overall operation): {User} - '{File}'", target.Username, logFileName); }
                            else { _logger.LogWarning("Download task cancelled unexpectedly: {User} - '{File}'", target.Username, logFileName); }
                        }
                        catch (Exception ex) {
                             string logFileName = Path.GetFileName(currentFullRemotePath);
                            _logger.LogError(ex, "Download task failed (Unexpected Error in Task Runner): {User} - '{File}'", target.Username, logFileName);
                        }
                        finally
                        {
                            // 7. Cleanup Temporary File(s)
                            if (!string.IsNullOrEmpty(renamedTempFilePath) && System.IO.File.Exists(renamedTempFilePath))
                            {
                                try { System.IO.File.Delete(renamedTempFilePath); _logger.LogTrace("Deleted renamed temporary file: {RenamedPath}", renamedTempFilePath); }
                                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete renamed temporary file: {RenamedPath}", renamedTempFilePath); }
                            }
                            if (!string.IsNullOrEmpty(tempFilePath) && System.IO.File.Exists(tempFilePath))
                            {
                                try { System.IO.File.Delete(tempFilePath); _logger.LogTrace("Deleted original temporary file: {TempPath}", tempFilePath); }
                                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete original temporary file: {TempPath}", tempFilePath); }
                            }

                            // 8. Release Semaphore
                            if (semaphoreAcquired) { downloadSemaphore.Release(); _logger.LogTrace("Semaphore slot released for {User} - {File}", target.Username, Path.GetFileName(currentFullRemotePath)); }
                        }
                    }); // End of Task.Run lambda

                    downloadTasks.Add(task);
                } // End of foreach loop creating tasks

                _logger.LogInformation("Waiting for {Count} download tasks to complete...", downloadTasks.Count);
                try
                {
                    await Task.WhenAll(downloadTasks);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error during Task.WhenAll for downloads.");
                }

                _logger.LogInformation("All download tasks finished.");

                // --- Post-Processing ---
                var finalHarvestedTracks = successfulHarvests
                    .OrderBy(h => h.Username)
                    .ThenBy(h => h.RemoteFilePath)
                    .Take(_options.OverallTrackLimit)
                    .ToList();

                _logger.LogInformation("Step 4 complete. Successfully harvested {Count}/{TotalAttempted} tracks (incl. valid ID3, after applying overall limit of {Limit}).",
                    finalHarvestedTracks.Count, allPotentialDownloads.Count, _options.OverallTrackLimit);

                if (!finalHarvestedTracks.Any())
                {
                    _logger.LogWarning("No tracks were successfully downloaded with valid metadata.");
                }
                else
                {
                    _logger.LogInformation("Final Harvested Tracks (with valid ID3):");
                    foreach(var track in finalHarvestedTracks)
                    {
                        _logger.LogInformation("  -> {TrackInfo}", track);
                    }
                }

                // --- NEW Step 5: Spotify Matching & Playlist Preparation (Placeholder) ---
                _logger.LogInformation("STEP 5: Spotify Matching (Not Implemented Yet)");
                // TODO: Implement Spotify matching logic using finalHarvestedTracks

                // --- NEW Step 6: Spotify Playlist Creation/Update (Placeholder) ---
                _logger.LogInformation("STEP 6: Spotify Playlist Creation (Not Implemented Yet)");
                // TODO: Implement Spotify playlist logic using matched Spotify URIs

            }
            catch (OperationCanceledException)
            {
                 _logger.LogWarning("Soulseek-Radar operation was cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in DiscoverTracksAsync");
            }
            finally
            {
                if (!overallCts.IsCancellationRequested)
                {
                    overallCts.Cancel();
                }
                await DisconnectGracefullyAsync();
                overallCts.Dispose();
            }
        } // End DiscoverTracksAsync

    /// <summary>
    /// Crawls a user's share starting from a seed track's location, picking related but different tracks.
    /// Picks at most ONE track per directory.
    /// Uses fuzzy matching (TokenSetRatio) between directory/file names and the seed track's filename
    /// (without extension or path) to guide exploration and avoid duplicates.
    /// Relies on manual path parsing using backslash as separator.
    /// It explores dissimilar child directories first. If a directory only contains similar children (or already visited ones),
    /// or has no children, it attempts to move up the hierarchy.
    /// </summary>
    /// <param name="username">The user whose share to crawl.</param>
    /// <param name="seedFileEntry">The specific file entry from the initial search result representing the seed track.</param>
    /// <param name="maxPicks">The maximum number of track paths to pick (overall).</param>
    /// <param name="cancellationToken">Cancellation token for the crawl operation.</param>
    /// <returns>A list of full remote file paths for potential download.</returns>
    private async Task<List<string>> CrawlAndPickAsync(
        string username,
        Soulseek.File seedFileEntry, // Specific File entry
        int maxPicks,       // Overall quota
        CancellationToken cancellationToken)
    {
        var picks = new List<string>();
        const char Sep = SoulseekSeparator;

        _logger.LogDebug("Browsing share for {User} (seeking {MaxPicks} picks total, 1 per dir, timeout {T}s for browse, threshold {Threshold}%)",
            username, maxPicks, _options.PerPeerTimeoutSeconds, SimilarityThreshold);
        BrowseResponse browse;
        try
        {
            browse = await _soulseekClient.BrowseAsync(username,
               new BrowseOptions(responseTimeout: _options.PerPeerTimeoutSeconds * 1000),
               cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
             _logger.LogWarning("Browse operation cancelled or timed out for {User}. IsCancellationRequested: {IsCancelled}", username, cancellationToken.IsCancellationRequested);
             return picks;
        }
        catch (Exception ex) when (ex is UserOfflineException || ex is TimeoutException || ex is SoulseekClientException)
        {
            _logger.LogWarning("Failed to browse share for {User}: {Msg}", username, ex.Message);
            return picks;
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Unexpected error browsing share for {User}", username);
             return picks;
        }

        long fileSizeCapBytes = (long)_options.FileSizeCapMB * 1024 * 1024;

        var browseDirLookup = browse.Directories?
            .Where(d => d != null && !string.IsNullOrEmpty(d.Name))
            .ToDictionary(d => d.Name, d => d, StringComparer.Ordinal);

        if (browseDirLookup == null || !browseDirLookup.Any())
        {
             _logger.LogDebug("No directories returned from browse operation for {User}.", username);
             return picks;
        }

        var lockedDirs = new HashSet<string>(browse.LockedDirectories?.Select(d => d.Name) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

        // --- Seed Context ---
        if (seedFileEntry == null || string.IsNullOrEmpty(seedFileEntry.Filename)) {
            _logger.LogError("Seed file entry or its filename is null/empty for user {User}. Cannot proceed with crawl.", username);
            return picks;
        }
        var seedPath = seedFileEntry.Filename;
        var seedFileNameOnly = GetFileNameManual(seedPath);
        var seedParentDir = GetParentPathManual(seedPath);
        var seedFileNameWithoutExtension = GetFileNameWithoutExtensionManual(seedFileNameOnly);
        var preprocessedSeedFileName = PreprocessForFuzzyMatch(seedFileNameWithoutExtension);

        _logger.LogDebug("Raw Seed Context: Path='{P}', Extracted FileName='{FN}', Extracted ParentDir='{PD}'", seedPath, seedFileNameOnly, seedParentDir ?? "<ROOT>");
        _logger.LogDebug(" -> Seed Filename for Matching (Preprocessed): '{PreprocessedSeedFile}'", preprocessedSeedFileName);

        // Handle case where seed is in root or path parsing failed
        if (string.IsNullOrEmpty(seedParentDir))
        {
            // --- Root Directory Fallback ---
            _logger.LogWarning("Seed track '{SeedFile}' appears to be in the root directory (or path parsing failed). Cannot perform relative traversal for {User}.", seedFileNameOnly, username);
             var fallbackPicks = browseDirLookup.Values
                .Where(dir => dir.Files != null)
                .SelectMany(dir => dir.Files.Select(f => new { DirectoryName = dir.Name, FileEntry = f })) // Keep directory context
                .Where(df => df.FileEntry != null && !string.IsNullOrEmpty(df.FileEntry.Filename)
                         && IsAllowedExtension(df.FileEntry.Filename)
                         && df.FileEntry.Size > 0 && df.FileEntry.Size <= fileSizeCapBytes)
                .Select(df => {
                     // Reconstruct path here as well for fallback
                     string potentialRelativeFileName = df.FileEntry.Filename;
                     string reconstructedFullPath = potentialRelativeFileName.Contains(SoulseekSeparator)
                        ? potentialRelativeFileName
                        : df.DirectoryName + SoulseekSeparator + potentialRelativeFileName;
                     return new {
                        FullPath = reconstructedFullPath, // Use reconstructed path
                        PreprocessedName = PreprocessForFuzzyMatch(GetFileNameWithoutExtensionManual(GetFileNameManual(reconstructedFullPath))) // Preprocess based on reconstructed path
                     };
                 })
                .Select(f => new {
                    f.FullPath,
                    Similarity = (string.IsNullOrEmpty(preprocessedSeedFileName) || string.IsNullOrEmpty(f.PreprocessedName))
                                    ? 0
                                    : Fuzz.TokenSetRatio(preprocessedSeedFileName, f.PreprocessedName)
                })
                .Where(f => f.Similarity < SimilarityThreshold)
                .OrderBy(f => f.Similarity)
                .Take(maxPicks)
                .Select(f => f.FullPath) // Select the full path
                .ToList();

             if (fallbackPicks.Any()) {
                 _logger.LogDebug("Falling back to picking {Count} files from browse results (overall limit {Limit}, similarity < {Threshold}% using TokenSetRatio with preprocessing):",
                    fallbackPicks.Count, maxPicks, SimilarityThreshold);
                 foreach(var pick in fallbackPicks) {
                     _logger.LogDebug(" -> Fallback Pick: {FullPickPath}", pick); // Log the full path
                     picks.Add(pick);
                 }
             } else {
                 _logger.LogDebug("Fallback picking yielded no results (similarity < {Threshold}% using TokenSetRatio with preprocessing).", SimilarityThreshold);
             }
             return picks;
        }

        // --- Traversal Logic ---
        var traversalStack = new Stack<string>();
        var visitedDirs = new HashSet<string>(StringComparer.Ordinal);
        var pickedFromFileInDir = new HashSet<string>(StringComparer.Ordinal);

        if (browseDirLookup.ContainsKey(seedParentDir) || lockedDirs.Contains(seedParentDir))
        {
             _logger.LogDebug("Starting traversal from seed parent directory: '{SeedParentDir}'", seedParentDir);
             traversalStack.Push(seedParentDir);
             visitedDirs.Add(seedParentDir);
        }
        else
        {
             _logger.LogWarning("Seed parent directory '{SeedParentDir}' not found in browse results or locked directories for {User}. Cannot start traversal.", seedParentDir, username);
             return picks;
        }

        while (traversalStack.Count > 0 && picks.Count < maxPicks && !cancellationToken.IsCancellationRequested)
        {
            var currentDir = traversalStack.Pop();
            _logger.LogDebug("Traversal: Popped '{CurrentDir}' from stack. Stack size: {StackSize}", currentDir, traversalStack.Count);

            bool isSeedParentDirectory = currentDir.Equals(seedParentDir, StringComparison.Ordinal);
            if (isSeedParentDirectory)
            {
                _logger.LogDebug("Skipping file processing in '{CurrentDir}' because it is the seed track's parent directory.", currentDir);
            }

            // --- 1. Process Files (Only if NOT the seed parent dir and not already picked from) ---
            if (!isSeedParentDirectory)
            {
                if (pickedFromFileInDir.Contains(currentDir))
                {
                    _logger.LogTrace("Skipping file processing in directory '{CurrentDir}' as a file was already picked from it.", currentDir);
                }
                else if (lockedDirs.Contains(currentDir))
                {
                    _logger.LogTrace("Skipping file processing in locked directory: {LockedDir}", currentDir);
                }
                else if (browseDirLookup.TryGetValue(currentDir, out var currentDirEntry) && currentDirEntry.Files != null)
                {
                    _logger.LogDebug("Processing files in '{CurrentDir}'. Found {FileCount} files in browse data.", currentDir, currentDirEntry.Files.Count);
                    var filesInDir = currentDirEntry.Files
                                        .Where(f => f != null && !string.IsNullOrEmpty(f.Filename)
                                                    && IsAllowedExtension(f.Filename)
                                                    && f.Size > 0 && f.Size <= fileSizeCapBytes)
                                        .ToList();
                    _logger.LogDebug(" -> {Count} files meet extension/size criteria in '{CurrentDir}'.", filesInDir.Count, currentDir);

                    Shuffle(filesInDir);

                    bool pickedThisDir = false;
                    foreach (var file in filesInDir)
                    {
                        if (string.IsNullOrEmpty(file.Filename)) {
                            _logger.LogWarning("Skipping file entry with null/empty filename in directory '{CurrentDir}'", currentDir);
                            continue;
                        }

                        // *** FIX: Construct the full path ***
                        string potentialRelativeFileName = file.Filename;
                        string reconstructedFullPath;
                        if (potentialRelativeFileName.Contains(SoulseekSeparator))
                        {
                            // If filename contains separator, assume it's already absolute (or malformed relative)
                            reconstructedFullPath = potentialRelativeFileName;
                            _logger.LogTrace(" -> File entry '{FileName}' in dir '{CurrentDir}' seems to contain a separator; using as-is.", potentialRelativeFileName, currentDir);
                        }
                        else
                        {
                            // Assume relative, combine with current directory path
                            reconstructedFullPath = currentDir + SoulseekSeparator + potentialRelativeFileName;
                            _logger.LogTrace(" -> Reconstructing full path: '{CurrentDir}' + '{FileName}' = '{FullPath}'", currentDir, potentialRelativeFileName, reconstructedFullPath);
                        }

                        var currentFileNameOnly = GetFileNameManual(reconstructedFullPath);
                        var currentFileNameWithoutExtension = GetFileNameWithoutExtensionManual(currentFileNameOnly);
                        var preprocessedCurrentFileName = PreprocessForFuzzyMatch(currentFileNameWithoutExtension);

                        int fileSimilarity;
                        if (string.IsNullOrEmpty(preprocessedSeedFileName) || string.IsNullOrEmpty(preprocessedCurrentFileName))
                        {
                            fileSimilarity = 0;
                            _logger.LogTrace(" -> Preprocessing resulted in empty string for file '{FileName}' or seed. Forcing dissimilarity.", currentFileNameOnly);
                        }
                        else
                        {
                            fileSimilarity = Fuzz.TokenSetRatio(preprocessedSeedFileName, preprocessedCurrentFileName);
                        }

                        _logger.LogDebug(" -> Checking file: '{FileName}' (Preprocessed: '{PreprocessedFile}') vs Seed (Preprocessed: '{PreprocessedSeed}'). TokenSetRatio: {Score}%",
                            currentFileNameOnly, preprocessedCurrentFileName, preprocessedSeedFileName, fileSimilarity);

                        if (fileSimilarity < SimilarityThreshold)
                        {
                            // *** FIX: Add the RECONSTRUCTED full path ***
                            picks.Add(reconstructedFullPath);
                            pickedFromFileInDir.Add(currentDir);
                            pickedThisDir = true;
                            _logger.LogDebug("Picked file: {FileName} (Full Path: '{FullPath}', TokenSetRatio to seed '{SeedPreprocessed}': {Score}% < {Threshold}%)",
                                currentFileNameOnly, reconstructedFullPath, preprocessedSeedFileName, fileSimilarity, SimilarityThreshold);
                            _logger.LogDebug(" -> Marked '{CurrentDir}' as having a file picked.", currentDir);
                            break; // Stop after one pick per directory
                        }
                        else
                        {
                            _logger.LogDebug(" -> Skipping file: {FileName} (TokenSetRatio: {Score}% >= {Threshold}%)",
                                currentFileNameOnly, fileSimilarity, SimilarityThreshold);
                        }
                    }
                    _logger.LogDebug(" -> Finished processing files in '{CurrentDir}'. Picked a file? {PickedStatus}", currentDir, pickedThisDir);

                    if (picks.Count >= maxPicks || cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogDebug("Global pick limit ({Limit}) reached or cancellation requested. Stopping traversal.", maxPicks);
                        break;
                    }
                }
                else
                {
                    _logger.LogTrace("Directory '{Dir}' not found in browse results or has null Files collection. Skipping file processing.", currentDir);
                }
            } // --- End File Processing ---

            if (picks.Count >= maxPicks || cancellationToken.IsCancellationRequested)
            {
                 _logger.LogDebug("Global pick limit ({Limit}) reached or cancellation requested after file processing check. Stopping traversal.", maxPicks);
                 break;
            }

            // --- 2. Process Child Directories ---
            _logger.LogDebug("Processing children of '{CurrentDir}'", currentDir);
            var potentialChildren = browseDirLookup.Keys
                .Concat(lockedDirs)
                .Where(path => IsDirectChildManual(currentDir, path))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            _logger.LogDebug(" -> Found {Count} potential child directories for '{CurrentDir}'.", potentialChildren.Count, currentDir);

            Shuffle(potentialChildren);

            var dissimilarChildrenToPush = new List<string>();

            foreach (var childDir in potentialChildren)
            {
                if (visitedDirs.Contains(childDir))
                {
                    _logger.LogTrace("Child '{ChildDir}' already visited. Skipping.", childDir);
                    continue;
                }

                var childDirNameOnly = GetLastPathComponentManual(childDir);
                var preprocessedChildDirName = PreprocessForFuzzyMatch(childDirNameOnly);

                int similarity;
                if (string.IsNullOrEmpty(preprocessedSeedFileName) || string.IsNullOrEmpty(preprocessedChildDirName))
                {
                    similarity = 0;
                    _logger.LogTrace(" -> Preprocessing resulted in empty string for dir '{DirName}' or seed. Forcing dissimilarity.", childDirNameOnly);
                }
                else
                {
                    similarity = Fuzz.TokenSetRatio(preprocessedSeedFileName, preprocessedChildDirName);
                }

                _logger.LogTrace("Checking child dir '{ChildName}' (Preprocessed: '{PreprocessedChild}') against seed (Preprocessed: '{PreprocessedSeed}'). TokenSetRatio: {Similarity}%",
                    childDirNameOnly, preprocessedChildDirName, preprocessedSeedFileName, similarity);

                if (similarity >= SimilarityThreshold)
                {
                    _logger.LogDebug("Child directory '{ChildName}' (preprocessed:{preprocessedChildDirName}) is SIMILAR (TokenSetRatio: {Similarity}% >= {Threshold}%) to seed (Preprocessed: '{PreprocessedSeed}'). Marking visited, skipping descent.",
                         childDirNameOnly, preprocessedChildDirName, similarity, SimilarityThreshold, preprocessedSeedFileName);
                    visitedDirs.Add(childDir);
                }
                else
                {
                     _logger.LogTrace("Child directory '{ChildName}' is DISSIMILAR (TokenSetRatio: {Similarity}% < {Threshold}%). Adding to potential exploration list.", childDirNameOnly, similarity, SimilarityThreshold);
                    dissimilarChildrenToPush.Add(childDir);
                }
            } // --- End foreach childDir ---

            // --- 3. Decide Traversal Action ---
            var parentDir = GetParentPathManual(currentDir);
            bool canGoUp = !string.IsNullOrEmpty(parentDir)
                        && (browseDirLookup.ContainsKey(parentDir) || lockedDirs.Contains(parentDir));

            if (dissimilarChildrenToPush.Any())
            {
                _logger.LogDebug("Decision: Found {Count} dissimilar children for '{CurrentDir}'. Pushing them onto stack. Stack size before push: {StackSize}",
                    dissimilarChildrenToPush.Count, currentDir, traversalStack.Count);
                foreach (var dissimilarChild in dissimilarChildrenToPush.OrderBy(d => d, StringComparer.Ordinal))
                {
                    if (!visitedDirs.Contains(dissimilarChild))
                    {
                        traversalStack.Push(dissimilarChild);
                        visitedDirs.Add(dissimilarChild);
                         _logger.LogTrace(" -> Pushed dissimilar child: {ChildDir}", dissimilarChild);
                    } else {
                         _logger.LogTrace(" -> Skipping push of dissimilar child {ChildDir} as it became visited.", dissimilarChild);
                    }
                }
                _logger.LogDebug(" -> Stack size after pushing dissimilar children: {StackSize}", traversalStack.Count);
            }
            else
            {
                _logger.LogDebug("Decision: No unvisited dissimilar children found for '{CurrentDir}'. Attempting to go up.", currentDir);
                if (canGoUp && parentDir != null)
                {
                    if (!visitedDirs.Contains(parentDir))
                    {
                        _logger.LogDebug(" -> Can go up. Pushing parent '{ParentDir}' onto stack. Stack size: {StackSize}", parentDir, traversalStack.Count + 1);
                        traversalStack.Push(parentDir);
                        visitedDirs.Add(parentDir);
                    } else {
                       _logger.LogTrace(" -> Parent '{ParentDir}' already visited, not pushing again to prevent potential loops.", parentDir);
                    }
                }
                else
                {
                    _logger.LogDebug(" -> Cannot go up from '{CurrentDir}'. No parent, parent not in browse/locked results, or parent already visited. Ending branch exploration.", currentDir);
                }
            }
        } // --- End while loop ---

        if (cancellationToken.IsCancellationRequested)
        {
             _logger.LogWarning("Crawl for {User} was cancelled during traversal.", username);
        }

        _logger.LogDebug("Traversal finished for {User}. Found {Count} picks (Quota: {Quota}).", username, picks.Count, maxPicks);
        return picks;
    }

        // --- Disconnect ---
        private async Task DisconnectGracefullyAsync()
        {
            if (_soulseekClient != null && _soulseekClient.State != SoulseekClientStates.Disconnected)
            {
                _logger.LogInformation("Disconnecting Soulseek client...");
                try
                {
                    _soulseekClient.Disconnect();
                    await Task.Delay(250);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Exception during Soulseek disconnect.");
                }
                finally
                {
                     _logger.LogInformation("Soulseek client state after disconnect attempt: {State}", _soulseekClient.State);
                }
            }
        }

    // *** CHANGE 1: Updated PreprocessForFuzzyMatch method ***
    /// <summary>
    /// Preprocesses a string (filename or directory name, without extension) for fuzzy matching:
    /// - Converts to lowercase.
    /// - Removes content within parentheses () or square brackets [].
    /// - Removes hyphens (-).
    /// - Removes dots (.).
    /// - Normalizes whitespace (collapses multiple spaces to single, trims).
    /// - Removes the first word if it consists entirely of digits (e.g., track numbers).
    /// </summary>
    /// <param name="s">The input string (expected to be without extension).</param>
    /// <returns>The preprocessed string, or an empty string if the input was null/whitespace.</returns>
    private static string PreprocessForFuzzyMatch(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";

        string processed = s.ToLowerInvariant(); // Lowercase first

        // Remove content within () and []
        processed = ParenthesesContentRegex.Replace(processed, "");

        // Remove hyphens
        processed = processed.Replace("-", "");

        // Remove dots
        processed = processed.Replace(".", "");

        // Normalize whitespace (important before splitting and for general matching)
        processed = Regex.Replace(processed, @"\s+", " ").Trim();

        // Check and remove the first word if it's all digits
        if (!string.IsNullOrEmpty(processed))
        {
            int firstSpaceIndex = processed.IndexOf(' ');
            string firstWord = (firstSpaceIndex == -1) ? processed : processed.Substring(0, firstSpaceIndex);

            if (firstWord.Length > 0 && firstWord.All(char.IsDigit))
            {
                processed = (firstSpaceIndex == -1) ? "" : processed.Substring(firstSpaceIndex + 1).TrimStart();
                processed = processed.Trim();
            }
        }

        processed = Regex.Replace(processed, @"\s+", " ").Trim();

        return processed;
    }

    /// <summary>
    /// Checks if a filename has an allowed audio extension.
    /// Uses Path.GetExtension which is generally safe for this purpose.
    /// </summary>
    private static bool IsAllowedExtension(string filename)
    {
        if (string.IsNullOrEmpty(filename)) return false;
        var ext = Path.GetExtension(filename)?.ToLowerInvariant();
        return !string.IsNullOrEmpty(ext) && AllowedExtensions.Contains(ext);
    }

    /// <summary>
    /// Shuffles a list in place.
    /// </summary>
    private void Shuffle<T>(IList<T> list)
    {
        int n = list.Count;
        while (n > 1) { n--; int k = _random.Next(n + 1); (list[k], list[n]) = (list[n], list[k]); }
    }

    // --- Manual Path Parsing Helpers (using SoulseekSeparator) ---

    /// <summary>
    /// Manually extracts the filename part (after the last backslash) from a Soulseek path.
    /// </summary>
    /// <param name="path">The full Soulseek path.</param>
    /// <returns>The filename, or the original path if no backslash is found, or empty string if path is null/empty.</returns>
    private static string GetFileNameManual(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        int lastSeparatorIndex = path.LastIndexOf(SoulseekSeparator);
        if (lastSeparatorIndex == -1)
        {
            return path;
        }
        if (lastSeparatorIndex >= path.Length - 1)
        {
             return "";
        }
        return path.Substring(lastSeparatorIndex + 1);
    }

    /// <summary>
    /// Manually extracts the filename without the extension from a filename string.
    /// Assumes 'filename' is just the file part, not the full path.
    /// </summary>
    /// <param name="filename">The filename (e.g., from GetFileNameManual).</param>
    /// <returns>The filename without the last extension, or the original filename if no dot is found, or empty string if filename is null/empty.</returns>
    private static string GetFileNameWithoutExtensionManual(string? filename)
    {
        if (string.IsNullOrEmpty(filename)) return "";
        int lastDotIndex = filename.LastIndexOf('.');
        if (lastDotIndex <= 0)
        {
            return filename;
        }
        return filename.Substring(0, lastDotIndex);
    }

    /// <summary>
    /// Manually extracts the parent directory path (before the last backslash) from a Soulseek path.
    /// </summary>
    /// <param name="path">The full Soulseek path.</param>
    /// <returns>The parent path, or null if no parent exists (root or single component).</returns>
    private static string? GetParentPathManual(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        int lastSeparatorIndex = path.LastIndexOf(SoulseekSeparator);
        if (lastSeparatorIndex <= 0)
        {
            return null;
        }
        if (lastSeparatorIndex == path.Length - 1) {
            lastSeparatorIndex = path.LastIndexOf(SoulseekSeparator, lastSeparatorIndex - 1);
            if (lastSeparatorIndex < 0) return null;
        }

        return path.Substring(0, lastSeparatorIndex);
    }

    /// <summary>
    /// Manually extracts the last component of a Soulseek path (directory or filename).
    /// Handles trailing slashes.
    /// </summary>
    /// <param name="path">The full Soulseek path.</param>
    /// <returns>The last component of the path, or empty string if path is null/empty or just separators.</returns>
    private static string GetLastPathComponentManual(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        string trimmedPath = path.TrimEnd(SoulseekSeparator);
        if (string.IsNullOrEmpty(trimmedPath)) return "";
        int lastSeparatorIndex = trimmedPath.LastIndexOf(SoulseekSeparator);
        if (lastSeparatorIndex == -1)
        {
            return trimmedPath;
        }
        return trimmedPath.Substring(lastSeparatorIndex + 1);
    }


    /// <summary>
    /// Manually checks if 'childPath' is a direct child of 'parentPath' using SoulseekSeparator.
    /// Uses Ordinal comparison (case-sensitive). Handles paths that might end with separators.
    /// </summary>
    private static bool IsDirectChildManual(string parentPath, string childPath)
    {
        if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(childPath)) return false;

        string normParent = parentPath.TrimEnd(SoulseekSeparator);
        string normChild = childPath.TrimEnd(SoulseekSeparator);

        if (normChild.Length <= normParent.Length) return false;

        // Handle root case: If normParent is empty, child must not contain separator
        if (string.IsNullOrEmpty(normParent)) {
            return !normChild.Contains(SoulseekSeparator);
        }

        // Normal case: Child must start with parent + separator
        if (!normChild.StartsWith(normParent + SoulseekSeparator, StringComparison.Ordinal)) return false;

        // Ensure no more separators after the parent part
        int nextSeparatorIndex = normChild.IndexOf(SoulseekSeparator, normParent.Length + 1);
        return nextSeparatorIndex == -1;
    }

    } // End SoulseekRadarService Class
} // End Namespace