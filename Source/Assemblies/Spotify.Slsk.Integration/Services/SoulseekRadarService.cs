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

namespace Spotify.Slsk.Integration.Services
{
    public class SoulseekRadarService
    {
        private readonly ILogger<SoulseekRadarService> _logger;
        private readonly SoulseekClient _soulseekClient;
        private readonly SoulseekRadarOptions _options;
        private readonly Random _random = new Random();

        private const int MinShareSizeFiles = 50;
        private const int SimilarityThreshold = 50;
        private static readonly HashSet<string> AllowedExtensions = new HashSet<string> { ".mp3", ".flac", ".m4a", ".ogg" };

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
                // Pass the overall CancellationToken here if the static method supports it
                // Assuming ConnectAndLoginAsync from SoulseekService.cs is used and potentially adapted for CT
                await SoulseekService.ConnectAndLoginAsync(_soulseekClient, ssUsername, ssPassword); // Assuming this exists and works
                _logger.LogInformation("Connected and logged in.");

                // --- Step 1: Search seed on the network ---
                // ... (Step 1 logic remains the same) ...
                _logger.LogInformation("STEP 1: Searching for '{Seed}' (timeout {T}s)", seedTrackQuery, _options.SearchTimeoutSeconds);
                IReadOnlyCollection<SearchResponse> responses;
                try
                {
                    var searchOptions = new SearchOptions(
                        searchTimeout: _options.SearchTimeoutSeconds * 1000,
                        stateChanged: e => _logger.LogTrace("Search state: {State}", e.Search.State),
                        responseReceived: e => _logger.LogTrace("Resp from {User}", e.Response.Username)
                    );
                    // Pass overall cancellation token if SearchAsync supports it
                    var result = await _soulseekClient.SearchAsync(
                        SearchQuery.FromText(seedTrackQuery),
                        options: searchOptions,
                        cancellationToken: overallCts.Token // Pass overall cancellation
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
                // ... (Step 2 logic remains the same) ...
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
                        if (stats.FileCount >= MinShareSizeFiles)
                        {
                            shareData.Add((resp.Username, stats.FileCount));
                            _logger.LogTrace(" -> {User} has {Count} files (>= {Min})", resp.Username, stats.FileCount, MinShareSizeFiles);
                        }
                        else
                        {
                            _logger.LogTrace(" -> Skipping {User}, only {Count} files", resp.Username, stats.FileCount);
                        }
                    }
                    catch (OperationCanceledException) when (!overallCts.IsCancellationRequested) { _logger.LogWarning(" -> Timeout fetching stats for {User}", resp.Username); }
                    catch (Exception ex) when (ex is UserOfflineException || ex is TimeoutException || ex is SoulseekClientException) { _logger.LogWarning(" -> Unable to fetch stats for {User}: {Msg}", resp.Username, ex.Message); }
                    catch (Exception ex) { _logger.LogError(ex, " -> Unexpected error fetching stats for {User}", resp.Username); }
                }
                if (overallCts.IsCancellationRequested) { _logger.LogWarning("Operation cancelled during user stats fetching."); await DisconnectGracefullyAsync(); return; }
                if (!shareData.Any()) { _logger.LogWarning("No users passed the share-size filter ({MinFiles} files). Exiting.", MinShareSizeFiles); await DisconnectGracefullyAsync(); return; }

                var selectedUsers = shareData.OrderBy(x => x.FileCount).Take(_options.MaxUsers).Select(x => x.Username).ToList();
                _logger.LogInformation("Selected {Count} users for crawling: {Users}", selectedUsers.Count, string.Join(", ", selectedUsers));
                int perUserQuota = (selectedUsers.Count <= _options.MaxUsers / 2 && _options.MaxUsers > 0) ? _options.MaxPerUserQuotaSmallU : _options.PerUserQuotaLargeU;
                _logger.LogInformation("Calculated per-user quota: {Quota} (based on {SelectedCount} selected users)", perUserQuota, selectedUsers.Count);


                // --- Step 3: Crawl & pick recommendations ---
                // ... (Step 3 logic remains the same, uses CrawlAndPickAsync below) ...
                 _logger.LogInformation("STEP 3: Crawling shares and picking up to {N} potential tracks per user", perUserQuota);
                var allPotentialDownloads = new List<(string Username, string RemoteFilePath)>();
                foreach (var user in selectedUsers)
                {
                    if (overallCts.IsCancellationRequested) break;
                    _logger.LogInformation("Processing user {User}...", user);
                    var seedResp = uniqueUsers.FirstOrDefault(r => r.Username == user);
                    if (seedResp == null || !seedResp.Files.Any()) { _logger.LogWarning("Could not find original search response or files for user {User}. Skipping crawl.", user); continue; }
                    List<string> picks;
                    try
                    {
                        using var crawlCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
                        crawlCts.CancelAfter(TimeSpan.FromSeconds(_options.CrawlTimeoutSeconds));
                        picks = await CrawlAndPickAsync(user, seedResp, perUserQuota, crawlCts.Token);
                    }
                    catch (OperationCanceledException) when (!overallCts.IsCancellationRequested) { _logger.LogWarning("Timeout during crawl for {User}", user); continue; }
                    catch (Exception ex) { _logger.LogWarning(ex, "Error during crawl for {User}", user); continue; }
                    if (picks.Any()) { allPotentialDownloads.AddRange(picks.Select(p => (user, p))); _logger.LogInformation(" -> Found {Count} potential picks for {User}: {Picks}", picks.Count, user, string.Join("; ", picks.Select(p => Path.GetFileName(p)))); }
                    else { _logger.LogInformation(" -> Found no suitable picks for {User}", user); }
                }
                if (overallCts.IsCancellationRequested) { _logger.LogWarning("Operation cancelled during user share crawling."); await DisconnectGracefullyAsync(); return; }
                _logger.LogInformation("Step 3 complete. Total potential downloads identified: {Total}", allPotentialDownloads.Count);
                if (!allPotentialDownloads.Any()) { _logger.LogWarning("No potential tracks identified across all selected users. Exiting."); await DisconnectGracefullyAsync(); return; }


                // --- Step 4: Parallel Harvesting & Metadata Extraction ---
                _logger.LogInformation("STEP 4: Starting parallel download and metadata extraction...");
                _logger.LogInformation(" -> Global Concurrency Limit: {Limit}", _options.GlobalDownloadConcurrency);
                _logger.LogInformation(" -> Per-User Success Quota (incl. ID3): {Quota}", perUserQuota); // Clarified quota meaning
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

                    var task = Task.Run(async () =>
                    {
                        string tempFilePath = string.Empty; // Original .tmp path
                        string renamedTempFilePath = string.Empty; // Path after renaming with correct extension
                        bool semaphoreAcquired = false;
                        Transfer? transferResult = null;
                        Stopwatch downloadStopwatch = new Stopwatch();

                        try
                        {
                            // 1. Acquire Concurrency Slot & Check Quota
                            _logger.LogTrace("Waiting for semaphore slot for {User} - {File}", target.Username, Path.GetFileName(target.RemoteFilePath));
                            await downloadSemaphore.WaitAsync(overallCts.Token);
                            semaphoreAcquired = true;
                            _logger.LogTrace("Semaphore slot acquired for {User} - {File}", target.Username, Path.GetFileName(target.RemoteFilePath));
                            overallCts.Token.ThrowIfCancellationRequested();

                            if (userSuccessCounters.TryGetValue(target.Username, out var currentCount) && currentCount >= perUserQuota)
                            {
                                _logger.LogDebug("Skipping download for {User} - {File}: User quota ({Quota}) already met.", target.Username, Path.GetFileName(target.RemoteFilePath), perUserQuota);
                                return; // Exit task logic
                            }

                            // 2. Prepare for Download
                            tempFilePath = Path.GetTempFileName(); // Gets a path like /tmp/tmpXXXX.tmp
                            _logger.LogDebug("Preparing download: {User} -> '{RemotePath}' to '{LocalTempPath}'", target.Username, target.RemoteFilePath, tempFilePath);

                            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
                            downloadCts.CancelAfter(TimeSpan.FromSeconds(_options.PerPeerTimeoutSeconds));

                            var transferOptions = new TransferOptions(
                                stateChanged: args => { /* Logging as before */
                                    if (!args.Transfer.State.HasFlag(TransferStates.Completed)) { _logger.LogTrace("Download State: {User} - {File} -> {State}", args.Transfer.Username, Path.GetFileName(args.Transfer.Filename), args.Transfer.State); }
                                    else { _logger.LogDebug("Download Final State: {User} - {File} -> {State} (Prev: {PrevState})", args.Transfer.Username, Path.GetFileName(args.Transfer.Filename), args.Transfer.State, args.PreviousState); }
                                },
                                progressUpdated: args => { /* Logging as before */
                                    _logger.LogTrace("Download Progress: {User} - {File} | {Percent:F1}% ({Bytes}/{TotalBytes}) @ {Speed:F1} kB/s", args.Transfer.Username, Path.GetFileName(args.Transfer.Filename), args.Transfer.PercentComplete, args.Transfer.BytesTransferred, args.Transfer.Size, args.Transfer.AverageSpeed / 1024.0);
                                }
                            );

                            // 3. Execute Download
                            _logger.LogInformation("Starting download: {User} - '{File}'", target.Username, Path.GetFileName(target.RemoteFilePath));
                            downloadStopwatch.Start();
                            try
                            {
                                transferResult = await _soulseekClient.DownloadAsync(
                                    username: target.Username,
                                    remoteFilename: target.RemoteFilePath,
                                    localFilename: tempFilePath, // Download to the .tmp path
                                    options: transferOptions,
                                    cancellationToken: downloadCts.Token);
                            }
                            catch (Exception ex) { /* Exception handling as before */
                                downloadStopwatch.Stop();
                                if (ex is OperationCanceledException && !overallCts.IsCancellationRequested) { _logger.LogWarning("Download timed out ({Timeout}s) during call: {User} - '{File}'", _options.PerPeerTimeoutSeconds, target.Username, Path.GetFileName(target.RemoteFilePath)); }
                                else if (ex is OperationCanceledException && overallCts.IsCancellationRequested) { _logger.LogWarning("Download cancelled (overall) during call: {User} - '{File}'", target.Username, Path.GetFileName(target.RemoteFilePath)); }
                                else if (ex is UserOfflineException uoEx) { _logger.LogWarning("Download failed (User Offline) during call: {User} - '{File}'. {Msg}", target.Username, Path.GetFileName(target.RemoteFilePath), uoEx.Message); }
                                else if (ex is TransferRejectedException trEx) { _logger.LogWarning("Download failed (Rejected by Peer) during call: {User} - '{File}'. {Msg}", target.Username, Path.GetFileName(target.RemoteFilePath), trEx.Message); }
                                else if (ex is SoulseekClientException scEx) { _logger.LogWarning(scEx, "Download failed (Soulseek Error) during call: {User} - '{File}'", target.Username, Path.GetFileName(target.RemoteFilePath)); }
                                else if (ex is IOException ioEx) { _logger.LogError(ioEx, "Download failed (IO Error) during call: {User} - '{File}'", target.Username, Path.GetFileName(target.RemoteFilePath)); }
                                else { _logger.LogError(ex, "Download failed (Unexpected Error) during call: {User} - '{File}'", target.Username, Path.GetFileName(target.RemoteFilePath)); }
                            }
                            finally { if (downloadStopwatch.IsRunning) downloadStopwatch.Stop(); }

                            // 4. Process Download Result
                            bool downloadSucceeded = transferResult != null &&
                                                     transferResult.State.HasFlag(TransferStates.Completed) &&
                                                     transferResult.State.HasFlag(TransferStates.Succeeded);

                            if (downloadSucceeded)
                            {
                                _logger.LogInformation("Download Task Completed: SUCCESS - {User} - '{File}' in {Duration:N1}s. Size: {Size} bytes. State: {State}",
                                    target.Username, Path.GetFileName(target.RemoteFilePath), downloadStopwatch.Elapsed.TotalSeconds, transferResult!.Size, transferResult.State);

                                // Verify file exists and has size before ID3 parsing
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

                                // 5. Attempt ID3 Extraction (Only if temp file seems valid)
                                if (tempFileExists && tempFileSize > 0)
                                {
                                    // --- START: Renaming Logic ---
                                    string originalExtension = Path.GetExtension(target.RemoteFilePath); // e.g., ".mp3"
                                    if (string.IsNullOrEmpty(originalExtension) || originalExtension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                                    {
                                        _logger.LogWarning("Could not determine original file extension for '{RemotePath}'. Cannot rename temp file. Skipping ID3.", target.RemoteFilePath);
                                    }
                                    else
                                    {
                                        // Construct new path: /tmp/tmpXXXXX.mp3
                                        renamedTempFilePath = Path.ChangeExtension(tempFilePath, originalExtension);
                                        bool renameSuccess = false;
                                        try
                                        {
                                            _logger.LogDebug("Renaming '{Source}' to '{Dest}' for TagLib.", tempFilePath, renamedTempFilePath);
                                            System.IO.File.Move(tempFilePath, renamedTempFilePath, true); // Overwrite if exists (shouldn't)
                                            renameSuccess = true;
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError(ex, "Failed to rename temporary file from '{Source}' to '{Dest}'. Skipping ID3.", tempFilePath, renamedTempFilePath);
                                        }
                                        // --- END: Renaming Logic ---

                                        if (renameSuccess)
                                        {
                                            try
                                            {
                                                // Use the RENAMED path for TagLib
                                                _logger.LogDebug("Extracting ID3 tags from RENAMED file: {RenamedPath}", renamedTempFilePath);
                                                using var tagFile = TagLib.File.Create(renamedTempFilePath); // Use RENAMED path

                                                string artist = tagFile.Tag.FirstPerformer ?? tagFile.Tag.FirstAlbumArtist ?? string.Empty;
                                                string title = tagFile.Tag.Title ?? Path.GetFileNameWithoutExtension(target.RemoteFilePath);
                                                string album = tagFile.Tag.Album ?? string.Empty;

                                                if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title)) {
                                                    _logger.LogWarning("ID3 Extraction Warning: Could not extract Artist or Title from {RenamedPath}. Skipping harvest.", renamedTempFilePath);
                                                } else {
                                                    var harvestedInfo = new HarvestedFileInfo {
                                                        Username = target.Username, RemoteFilePath = target.RemoteFilePath,
                                                        LocalTempPath = renamedTempFilePath, // Store renamed path now
                                                        Artist = artist.Trim(), Title = title.Trim(), Album = album.Trim(),
                                                        FileSize = transferResult.Size
                                                    };
                                                    _logger.LogInformation("ID3 Extracted Successfully: {Info}", harvestedInfo);
                                                    successfulHarvests.Add(harvestedInfo);

                                                    // 6. Increment User Success Count (ONLY if download AND ID3 succeeded)
                                                    var newCount = userSuccessCounters.AddOrUpdate(target.Username, 1, (key, count) => count + 1);
                                                    _logger.LogDebug("Incremented success count (incl. ID3) for {User} to {Count}/{Quota}", target.Username, newCount, perUserQuota);
                                                }
                                            }
                                            catch (CorruptFileException ex) { _logger.LogWarning(ex, "ID3 Extraction Failed (Corrupt File): {User} - '{File}' from {RenamedPath}", target.Username, Path.GetFileName(target.RemoteFilePath), renamedTempFilePath); }
                                            catch (UnsupportedFormatException ex) { _logger.LogWarning(ex, "ID3 Extraction Failed (Unsupported Format): {User} - '{File}' from {RenamedPath}", target.Username, Path.GetFileName(target.RemoteFilePath), renamedTempFilePath); }
                                            catch (Exception ex) { _logger.LogError(ex, "ID3 Extraction Failed (Unexpected Error): {User} - '{File}' from {RenamedPath}", target.Username, Path.GetFileName(target.RemoteFilePath), renamedTempFilePath); }
                                        } // End if (renameSuccess)
                                    } // End else (valid extension)
                                } // End ID3 Extraction block (if tempFileExists && tempFileSize > 0)
                            }
                            else // Download Failed/Rejected/TimedOut/Errored/etc.
                            {
                                string reason = transferResult?.State.ToString() ?? "Unknown (Transfer object null)";
                                string? exceptionMessage = transferResult?.Exception?.GetBaseException().Message;
                                _logger.LogWarning("Download Task Completed: FAILED - {User} - '{File}'. Final State: {Reason}. Duration: {Duration:N1}s. Exception: {Exception}",
                                    target.Username, Path.GetFileName(target.RemoteFilePath), reason, downloadStopwatch.Elapsed.TotalSeconds, exceptionMessage ?? "N/A");
                            }
                        }
                        catch (OperationCanceledException) { /* Handling as before */
                            if (overallCts.IsCancellationRequested) { _logger.LogWarning("Download task cancelled (overall operation): {User} - '{File}'", target.Username, Path.GetFileName(target.RemoteFilePath)); }
                            else { _logger.LogWarning("Download task cancelled unexpectedly: {User} - '{File}'", target.Username, Path.GetFileName(target.RemoteFilePath)); }
                        }
                        catch (Exception ex) { /* Handling as before */
                            _logger.LogError(ex, "Download task failed (Unexpected Error in Task Runner): {User} - '{File}'", target.Username, Path.GetFileName(target.RemoteFilePath));
                        }
                        finally
                        {
                            // 7. Cleanup Temporary File(s)
                            // Attempt to delete the RENAMED file first if it exists
                            if (!string.IsNullOrEmpty(renamedTempFilePath) && System.IO.File.Exists(renamedTempFilePath))
                            {
                                try { System.IO.File.Delete(renamedTempFilePath); _logger.LogTrace("Deleted renamed temporary file: {RenamedPath}", renamedTempFilePath); }
                                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete renamed temporary file: {RenamedPath}", renamedTempFilePath); }
                            }
                            // Also attempt to delete the ORIGINAL .tmp file (in case rename failed or never happened)
                            if (!string.IsNullOrEmpty(tempFilePath) && System.IO.File.Exists(tempFilePath))
                            {
                                try { System.IO.File.Delete(tempFilePath); _logger.LogTrace("Deleted original temporary file: {TempPath}", tempFilePath); }
                                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete original temporary file: {TempPath}", tempFilePath); }
                            }

                            // 8. Release Semaphore
                            if (semaphoreAcquired) { downloadSemaphore.Release(); _logger.LogTrace("Semaphore slot released for {User} - {File}", target.Username, Path.GetFileName(target.RemoteFilePath)); }
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

        // --- Step 3 Method --- (Keep existing, ensure it uses options correctly)
        private async Task<List<string>> CrawlAndPickAsync(
            string username,
            SearchResponse seedResp,
            int maxPicks,
            CancellationToken cancellationToken)
        {
            var picks = new List<string>();
            const char Sep = '\\';

            _logger.LogDebug("Browsing share for {User} (seeking {MaxPicks} picks, timeout {T}s)", username, maxPicks, _options.PerPeerTimeoutSeconds); // Use PerPeerTimeout for browse
            BrowseResponse browse;
            try
            {
                 // Use PerPeerTimeoutSeconds for the browse operation itself
                 browse = await _soulseekClient.BrowseAsync(username,
                    new BrowseOptions(responseTimeout: _options.PerPeerTimeoutSeconds * 1000),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                 _logger.LogWarning("Timeout browsing share for {User} after {T}s", username, _options.CrawlTimeoutSeconds); // Log CrawlTimeout here
                 return picks;
            }
            catch (Exception ex) when (ex is UserOfflineException || ex is TimeoutException || ex is SoulseekClientException)
            {
                 _logger.LogWarning("Failed to browse share for {User}: {Msg}", username, ex.Message);
                 return picks;
            }

            long fileSizeCapBytes = (long)_options.FileSizeCapMB * 1024 * 1024;

            var allDirs = browse.Directories
                .Where(d => d.Files.Any(f => IsAllowedExtension(f.Filename) && f.Size > 0 && f.Size <= fileSizeCapBytes))
                .ToDictionary(d => d.Name, d => d);

            var locked = new HashSet<string>(browse.LockedDirectories.Select(d => d.Name));

            if (!allDirs.Any()) { _logger.LogDebug("No browseable directories with allowed files found for {User}", username); return picks; }

            var seedFileEntry = seedResp.Files.FirstOrDefault();
            if (seedFileEntry == null) { _logger.LogWarning("Seed file entry missing in search response for {User}. Cannot establish context.", username); return picks; }

            var seedPath = seedFileEntry.Filename;
            int idx = seedPath.LastIndexOf(Sep);
            var seedDir = (idx > 0) ? seedPath.Substring(0, idx) : "";
            var seedFile = seedPath[(idx + 1)..];
            var seedAlbum = Normalize(GetLastPathComponent(seedDir));
            var seedArtist = Normalize(GetLastPathComponent(GetParentPath(seedDir)));
            var seedTitle = Normalize(Path.GetFileNameWithoutExtension(seedFile));
            _logger.LogDebug("Seed context: Artist='{A}', Album='{Al}', Title='{T}', Dir='{D}'", seedArtist, seedAlbum, seedTitle, seedDir);

            var potentialFiles = allDirs
                .SelectMany(kv => kv.Value.Files.Select(f => (DirPath: kv.Key, FileInfo: f)))
                .Where(x => IsAllowedExtension(x.FileInfo.Filename) && x.FileInfo.Size > 0 && x.FileInfo.Size <= fileSizeCapBytes)
                .Select(x => new {
                    FullPath = x.DirPath + Sep + x.FileInfo.Filename,
                    DirPath = x.DirPath,
                    FileInfo = x.FileInfo,
                    DirNameNorm = Normalize(GetLastPathComponent(x.DirPath)),
                    ParentDirNameNorm = Normalize(GetLastPathComponent(GetParentPath(x.DirPath))),
                    FileNameNorm = Normalize(Path.GetFileNameWithoutExtension(x.FileInfo.Filename))
                })
                .Where(x => Fuzz.Ratio(x.FileNameNorm, seedTitle) < SimilarityThreshold)
                .ToList();

            if (!potentialFiles.Any()) { _logger.LogDebug("No files found matching criteria (allowed extension, size, different title) for {User}", username); return picks; }

            var priorityPicks = potentialFiles
                .Where(x => Fuzz.Ratio(x.DirNameNorm, seedAlbum) < SimilarityThreshold &&
                            Fuzz.Ratio(x.ParentDirNameNorm, seedArtist) < SimilarityThreshold)
                .ToList();

            Shuffle(priorityPicks);
            foreach (var file in priorityPicks)
            {
                if (picks.Count >= maxPicks) break;
                picks.Add(file.FullPath);
                _logger.LogTrace("Picked (Priority): {Pick}", file.FullPath);
            }

            if (picks.Count < maxPicks)
            {
                var secondaryPicks = potentialFiles.Except(priorityPicks).ToList();
                Shuffle(secondaryPicks);
                foreach (var file in secondaryPicks)
                {
                    if (picks.Count >= maxPicks) break;
                    picks.Add(file.FullPath);
                    _logger.LogTrace("Picked (Secondary): {Pick}", file.FullPath);
                }
            }

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
                    // Optionally cancel ongoing transfers before disconnecting if needed
                    // var activeDownloads = _soulseekClient.Downloads; // Get current downloads
                    // foreach (var dl in activeDownloads) { _soulseekClient.CancelTransfer(dl.Token); }
                    _soulseekClient.Disconnect();
                    await Task.Delay(250); // Short delay
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

        // --- Helper Methods --- (Keep existing ones)
        private static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var normalized = System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"[\s\(\)\[\]\{\}\.\,\-_]+", " ").Trim();
            return normalized;
        }

        private static bool IsAllowedExtension(string filename)
        {
            var ext = Path.GetExtension(filename)?.ToLowerInvariant();
            return !string.IsNullOrEmpty(ext) && AllowedExtensions.Contains(ext);
        }

        private void Shuffle<T>(IList<T> list)
        {
            int n = list.Count;
            while (n > 1) { n--; int k = _random.Next(n + 1); (list[k], list[n]) = (list[n], list[k]); }
        }

        private static string? GetParentPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int idx = path.LastIndexOf("\\");
            if (idx <= 0) return null;
            return path.Substring(0, idx);
        }

        private static string GetLastPathComponent(string? path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int idx = path.LastIndexOf("\\");
            return path.Substring(idx + 1);
        }
    } // End SoulseekRadarService Class
} // End Namespace