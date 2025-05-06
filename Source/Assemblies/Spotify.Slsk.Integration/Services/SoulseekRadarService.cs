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
using Mscc.GenerativeAI; // *** ADDED: For Gemini API ***
using System.Net.Http; // *** ADDED: For HttpRequestException ***
using System.Net; // *** ADDED: For HttpStatusCode ***

namespace Spotify.Slsk.Integration.Services
{
    public class SoulseekRadarService
    {
        private readonly ILogger<SoulseekRadarService> _logger;
        private readonly SoulseekClient _soulseekClient;
        private readonly SoulseekRadarOptions _options;
        private readonly Random _random = new Random();
        private readonly GenerativeModel? _geminiModel; // *** ADDED: Gemini Model Client ***
        private readonly string _geminiPromptTemplate; // *** ADDED: Store prompt template ***

        private const char SoulseekSeparator = '\\';
        private const int MinShareSizeFiles = 50;
        private const int MaxShareSizeFiles = 700000;
        private const int SimilarityThreshold = 53;
        private static readonly HashSet<string> AllowedExtensions = new HashSet<string> { ".mp3", ".flac", ".m4a", ".ogg" };
        private static readonly Regex ParenthesesContentRegex = new Regex(@"\[.*?\]|\(.*?\)", RegexOptions.Compiled);

        // *** ADDED: Constants for Gemini interaction ***
        private const string GeminiModelName = "gemini-2.0-flash"; // Use the appropriate model identifier
        private const int MaxGeminiRetries = 5; // For exponential backoff
        private const int MaxGeminiTotalRetryDelaySeconds = 80;
		// *** ADDED: Regex to extract retryDelay from Gemini error JSON ***
		private static readonly Regex GeminiRetryDelayRegex = new Regex(@"\""retryDelay\"":\s*\""(\d+)s\""", RegexOptions.Compiled | RegexOptions.IgnoreCase);


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

            // *** ADDED: Initialize Gemini Client ***
            var apiKey = Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogWarning("GOOGLE_API_KEY environment variable not found. Artist identification heuristic will be disabled.");
                _geminiModel = null;
            }
            else
            {
                try
                {
                    var googleAI = new GoogleAI(apiKey);
                    // Use the specific model name for Gemini 1.5 Flash
                    _geminiModel = googleAI.GenerativeModel(model: GeminiModelName);
                    _logger.LogInformation("Gemini client initialized successfully for model: {ModelName}", GeminiModelName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize Gemini client. Artist identification heuristic will be disabled.");
                    _geminiModel = null;
                }
            }

            // *** ADDED: Define the prompt template ***
            _geminiPromptTemplate = @"
Analyze the following directory path, which comes from a user's P2P music share (using '\' as the separator). Your goal is to find the *first* folder name in the path that represents *exactly* the artist's name and nothing else.

Look through the folder names from left to right. Skip common folders like 'Music', 'Downloads', genre names (e.g., 'Rock', 'Alternative'), and compilation indicators ('VA', 'Various Artists', 'Soundtracks').

The folder you select must **only** be the artist's name. Do **not** choose folders that also include album titles, years, formats, or other details (e.g., ignore a folder named 'Artist Name - Album Title [FLAC]').

Output the exact folder name if you find one that is purely the artist's name. If no such folder exists in the path, or if you are unsure, you MUST output the literal word `None`.

Example 1:
Input: `music\Alternative\Billy Bragg\Billy Bragg - Life's A Riot With Spy vs Spy (2013) flac`
Output: `Billy Bragg`
*(Reason: 'Billy Bragg' is the first folder that is exactly the artist name, before the folder containing album info.)*

Example 2:
Input: `My Music\Pink Floyd - The Wall [FLAC]\01 - Song.mp3`
Output: `None`
*(Reason: The folder 'Pink Floyd - The Wall [FLAC]' contains more than just the artist name. There's no folder named just 'Pink Floyd'.)*

Example 3:
Input: `Rock\VA - Rock Anthems\Some Song.mp3`
Output: `None`
*(Reason: 'Rock' is a genre, 'VA' indicates a compilation.)*

Now, analyze this path and provide only the required output:
""{0}""
"; // {0} will be replaced with the actual path
        }

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
                int perUserPickQuota = (selectedUsers.Count <= _options.MaxUsers / 2 && _options.MaxUsers > 0) ? _options.MaxPerUserQuotaSmallU : _options.PerUserQuotaLargeU;
                _logger.LogInformation("Calculated per-user PICK quota: {Quota} (based on {SelectedCount} selected users)", perUserPickQuota, selectedUsers.Count);

                // *** ADDED: Calculate Gemini call quota ***
                int maxGeminiCallsPerUser = (_geminiModel == null) ? 0 : (selectedUsers.Count >= 7 ? 1 : 2);
                if (_geminiModel != null)
                {
                    _logger.LogInformation("Calculated per-user GEMINI call quota: {Quota} (based on {SelectedCount} selected users)", maxGeminiCallsPerUser, selectedUsers.Count);
                }
                else
                {
                     _logger.LogInformation("Gemini heuristic disabled (no API key or client init failed).");
                }


                // --- Step 3: Crawl & pick recommendations ---
                _logger.LogInformation("STEP 3: Crawling shares and picking up to {N} potential tracks per user", perUserPickQuota);
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
                        // *** MODIFIED: Pass maxGeminiCallsPerUser to CrawlAndPickAsync ***
                        picks = await CrawlAndPickAsync(user, seedFileEntry, perUserPickQuota, maxGeminiCallsPerUser, crawlCts.Token);
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
                _logger.LogInformation(" -> Per-User Success Quota (incl. ID3): {Quota}", perUserPickQuota); // Note: This is pick quota, not download quota
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

                    // Check overall harvest limit early to avoid unnecessary semaphore waits/downloads
                    if (successfulHarvests.Count >= _options.OverallTrackLimit)
                    {
                        _logger.LogInformation("Overall track limit ({Limit}) reached. Skipping further download attempts.", _options.OverallTrackLimit);
                        break;
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
                            // 0. Check overall limit again inside task
                            if (successfulHarvests.Count >= _options.OverallTrackLimit) return;

                            // 1. Acquire Concurrency Slot & Check User Quota (for SUCCESSFUL harvests)
                            _logger.LogTrace("Waiting for semaphore slot for {User} - {File}", target.Username, Path.GetFileName(currentFullRemotePath));
                            await downloadSemaphore.WaitAsync(overallCts.Token);
                            semaphoreAcquired = true;
                            _logger.LogTrace("Semaphore slot acquired for {User} - {File}", target.Username, Path.GetFileName(currentFullRemotePath));
                            overallCts.Token.ThrowIfCancellationRequested();

                            // Check user QUOTA based on SUCCESSFUL harvests, not just attempts
                            if (userSuccessCounters.TryGetValue(target.Username, out var currentSuccessCount) && currentSuccessCount >= perUserPickQuota)
                            {
                                _logger.LogDebug("Skipping download for {User} - {File}: User SUCCESS quota ({Quota}) already met.", target.Username, Path.GetFileName(currentFullRemotePath), perUserPickQuota);
                                return;
                            }
                            // Check overall limit one more time after acquiring semaphore
                            if (successfulHarvests.Count >= _options.OverallTrackLimit) return;


                            // 2. Prepare for Download
                            tempFilePath = Path.GetTempFileName();
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
                                    // Reduce verbosity of progress logging
                                    // _logger.LogTrace("Download Progress: {User} - {File} | {Percent:F1}% ({Bytes}/{TotalBytes}) @ {Speed:F1} kB/s", args.Transfer.Username, Path.GetFileName(args.Transfer.Filename), args.Transfer.PercentComplete, args.Transfer.BytesTransferred, args.Transfer.Size, args.Transfer.AverageSpeed / 1024.0);
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
                                            System.IO.File.Move(tempFilePath, renamedTempFilePath, true); // Overwrite if exists (unlikely but possible)
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
												// This is the line that can throw various exceptions based on file content
												using var tagFile = TagLib.File.Create(renamedTempFilePath);

												string artist = tagFile.Tag.FirstPerformer ?? tagFile.Tag.FirstAlbumArtist ?? string.Empty;
												string title = tagFile.Tag.Title ?? GetFileNameWithoutExtensionManual(GetFileNameManual(currentFullRemotePath)); // Fallback to filename
												string album = tagFile.Tag.Album ?? string.Empty;

												if (string.IsNullOrWhiteSpace(artist) && string.IsNullOrWhiteSpace(title)) {
													_logger.LogWarning("ID3 Extraction Warning: Could not extract Artist or Title from {RenamedPath}. Skipping harvest.", renamedTempFilePath);
												} else {
													// Check overall limit BEFORE adding to bag and incrementing counter
													if (successfulHarvests.Count < _options.OverallTrackLimit)
													{
														var harvestedInfo = new HarvestedFileInfo {
															Username = target.Username, RemoteFilePath = currentFullRemotePath,
															LocalTempPath = null, // Temp path is deleted, don't store it
															Artist = artist.Trim(), Title = title.Trim(), Album = album.Trim(),
															FileSize = transferResult.Size
														};
														_logger.LogInformation("ID3 Extracted Successfully: {Info}", harvestedInfo);
														successfulHarvests.Add(harvestedInfo);

														// 6. Increment User Success Count only if added
														var newCount = userSuccessCounters.AddOrUpdate(target.Username, 1, (key, count) => count + 1);
														_logger.LogDebug("Incremented success count (incl. ID3) for {User} to {Count}/{Quota}", target.Username, newCount, perUserPickQuota);

														// Check if overall limit reached AFTER adding
														if (successfulHarvests.Count >= _options.OverallTrackLimit)
														{
															_logger.LogInformation("Overall track limit ({Limit}) reached during harvest. Signalling cancellation.", _options.OverallTrackLimit);
															overallCts.Cancel(); // Signal cancellation to stop other tasks
														}
													} else {
														 _logger.LogDebug("Overall track limit reached before adding harvest for {User} - {File}", target.Username, Path.GetFileName(currentFullRemotePath));
													}
												}
											}
											catch (CorruptFileException ex) {
												_logger.LogWarning(ex, "ID3 Extraction Failed (Corrupt File): {User} - '{File}' from {RenamedPath}", target.Username, Path.GetFileName(currentFullRemotePath), renamedTempFilePath);
											}
											catch (UnsupportedFormatException ex) {
												_logger.LogWarning(ex, "ID3 Extraction Failed (Unsupported Format): {User} - '{File}' from {RenamedPath}", target.Username, Path.GetFileName(currentFullRemotePath), renamedTempFilePath);
											}
											catch (ArgumentOutOfRangeException ex) {
												// This often happens with malformed metadata within the file (e.g., invalid picture size/offset)
												_logger.LogWarning(ex, "ID3 Extraction Failed (ArgumentOutOfRangeException - likely malformed internal metadata like embedded picture): {User} - '{File}' from {RenamedPath}", target.Username, Path.GetFileName(currentFullRemotePath), renamedTempFilePath);
											}
											catch (Exception ex) {
												// Keep this for truly unexpected errors during ID3 processing
												_logger.LogError(ex, "ID3 Extraction Failed (Unexpected Error): {User} - '{File}' from {RenamedPath}", target.Username, Path.GetFileName(currentFullRemotePath), renamedTempFilePath);
											}
										} // End if (renameSuccess)
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
                            // Only delete renamed if it exists AND is different from original temp path
                            if (!string.IsNullOrEmpty(renamedTempFilePath) && !renamedTempFilePath.Equals(tempFilePath, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(renamedTempFilePath))
                            {
                                try { System.IO.File.Delete(renamedTempFilePath); _logger.LogTrace("Deleted renamed temporary file: {RenamedPath}", renamedTempFilePath); }
                                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete renamed temporary file: {RenamedPath}", renamedTempFilePath); }
                            }
                            // Always try to delete the original temp path if it exists
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
                    // Wait for tasks, but respect overall cancellation
                    await Task.WhenAll(downloadTasks);
                }
                catch (OperationCanceledException) {
                    _logger.LogWarning("Task.WhenAll cancelled, likely due to overall limit reached.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error during Task.WhenAll for downloads.");
                }

                _logger.LogInformation("All download tasks finished or cancelled.");

                // --- Post-Processing ---
                // Apply overall limit again just in case concurrent additions exceeded it slightly
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
                        // Log harvested info (maybe less verbose?)
                         _logger.LogInformation("  -> U: {User}, A: {Artist}, T: {Title}, Al: {Album} (From: {RemotePath})",
                                              track.Username, track.Artist, track.Title, track.Album, track.RemoteFilePath);
                    }
                }

                // --- Placeholder Steps ---
                _logger.LogInformation("STEP 5: Spotify Matching (Not Implemented Yet)");
                _logger.LogInformation("STEP 6: Spotify Playlist Creation (Not Implemented Yet)");

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
                    overallCts.Cancel(); // Ensure cancellation propagates if exiting early
                }
                await DisconnectGracefullyAsync();
                overallCts.Dispose();
            }
        }

        /// <summary>
        /// Crawls a user's share, picking related but different tracks, potentially using Gemini for artist diversity.
        /// Runs Gemini initially on the seed path and again after the first pick.
        /// </summary>
        private async Task<List<string>> CrawlAndPickAsync(
            string username,
            Soulseek.File seedFileEntry,
            int maxPicks,
            int maxGeminiCallsPerUser,
            CancellationToken cancellationToken)
        {
            var picks = new List<string>();
            int geminiCallsMade = 0;
            bool skipGeminiHeuristic = _geminiModel == null || maxGeminiCallsPerUser <= 0;
            var excludedArtistPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _logger.LogDebug("Browsing share for {User} (seeking {MaxPicks} picks total, 1 per dir, timeout {T}s for browse, threshold {Threshold}%, Gemini Quota: {GeminiQuota})",
                username, maxPicks, _options.PerPeerTimeoutSeconds, SimilarityThreshold, maxGeminiCallsPerUser);
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
            var seedParentDir = GetParentPathManual(seedPath); // This can be null if seed is at root
            var seedFileNameWithoutExtension = GetFileNameWithoutExtensionManual(seedFileNameOnly);
            var preprocessedSeedFileName = PreprocessForFuzzyMatch(seedFileNameWithoutExtension);

            _logger.LogDebug("Raw Seed Context: Path='{P}', Extracted FileName='{FN}', Extracted ParentDir='{PD}'", seedPath, seedFileNameOnly, seedParentDir ?? "<ROOT>");
            _logger.LogDebug(" -> Seed Filename for Matching (Preprocessed): '{PreprocessedSeedFile}'", preprocessedSeedFileName);


            // --- Initial Gemini Call for Seed Artist ---
            string? validatedSeedArtistPath = null; // Store the validated path if found
            if (!skipGeminiHeuristic && geminiCallsMade < maxGeminiCallsPerUser && !string.IsNullOrEmpty(seedParentDir))
            {
                _logger.LogInformation("Gemini Trigger: Initial check for seed artist in path: '{Path}' (Call {CallNum}/{MaxCalls})",
                    seedParentDir, geminiCallsMade + 1, maxGeminiCallsPerUser);

                string? seedArtistFolderName = await GetArtistFolderFromPathAsync(seedParentDir, cancellationToken);
                geminiCallsMade++;

                if (seedArtistFolderName != null)
                {
                    _logger.LogInformation("Gemini Initial Response for '{Path}': Identified '{ArtistFolder}' as potential seed artist folder.", seedParentDir, seedArtistFolderName);
                    // *** MODIFIED: Store the validated path ***
                    validatedSeedArtistPath = ValidateAndGetFullPathForArtistFolder(seedParentDir, seedArtistFolderName);

                    if (validatedSeedArtistPath != null)
                    {
                        _logger.LogInformation("Gemini Initial Action: Validated seed artist folder '{ArtistFolder}' found at path '{ArtistPath}'. Adding to excluded paths.",
                            seedArtistFolderName, validatedSeedArtistPath);
                        excludedArtistPaths.Add(validatedSeedArtistPath);
                    }
                    else
                    {
                        _logger.LogWarning("Gemini Initial Validation Failed: Folder '{ArtistFolder}' returned by Gemini not found as a component in seed path '{Path}'.",
                            seedArtistFolderName, seedParentDir);
                    }
                }
                else
                {
                    _logger.LogInformation("Gemini Initial Response for '{Path}': No specific seed artist folder identified or error occurred.", seedParentDir);
                }
            }
            else if (string.IsNullOrEmpty(seedParentDir))
            {
                _logger.LogDebug("Skipping initial Gemini seed artist check because seed parent directory is null or root.");
            }
            else if (skipGeminiHeuristic || geminiCallsMade >= maxGeminiCallsPerUser)
            {
                 _logger.LogDebug("Skipping initial Gemini seed artist check (Heuristic disabled or quota met).");
            }
            // --- END Initial Gemini Call ---


            // Handle case where seed is in root or path parsing failed (Fallback Logic)
            if (string.IsNullOrEmpty(seedParentDir))
            {
                 _logger.LogWarning("Seed track '{SeedFile}' appears to be in the root directory (or path parsing failed). Cannot perform relative traversal for {User}. Performing fallback pick.", seedFileNameOnly, username);
                 // ... (Fallback logic remains unchanged, including the check against excludedArtistPaths) ...
                 var fallbackPicks = browseDirLookup.Values
                    .Where(dir => dir.Files != null)
                    .SelectMany(dir => dir.Files.Select(f => new { DirectoryName = dir.Name, FileEntry = f }))
                    .Where(df => df.FileEntry != null && !string.IsNullOrEmpty(df.FileEntry.Filename)
                             && IsAllowedExtension(df.FileEntry.Filename)
                             && df.FileEntry.Size > 0 && df.FileEntry.Size <= fileSizeCapBytes
                             && (string.IsNullOrEmpty(df.DirectoryName) || !excludedArtistPaths.Any(excluded => df.DirectoryName.StartsWith(excluded, StringComparison.OrdinalIgnoreCase)))
                             )
                    .Select(df => {
                         string potentialRelativeFileName = df.FileEntry.Filename;
                         string reconstructedFullPath = potentialRelativeFileName.Contains(SoulseekSeparator)
                            ? potentialRelativeFileName
                            : df.DirectoryName + SoulseekSeparator + potentialRelativeFileName;
                         return new {
                            FullPath = reconstructedFullPath,
                            PreprocessedName = PreprocessForFuzzyMatch(GetFileNameWithoutExtensionManual(GetFileNameManual(reconstructedFullPath)))
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
                    .Select(f => f.FullPath)
                    .ToList();

                 if (fallbackPicks.Any()) {
                     _logger.LogDebug("Falling back to picking {Count} files from browse results (overall limit {Limit}, similarity < {Threshold}%, excluding {ExcludedCount} artist paths):",
                        fallbackPicks.Count, maxPicks, SimilarityThreshold, excludedArtistPaths.Count);
                     foreach(var pick in fallbackPicks) {
                         _logger.LogDebug(" -> Fallback Pick: {FullPickPath}", pick);
                         picks.Add(pick);
                     }
                 } else {
                     _logger.LogDebug("Fallback picking yielded no results (similarity < {Threshold}%, excluded {ExcludedCount} artist paths).", SimilarityThreshold, excludedArtistPaths.Count);
                 }
                 return picks;
            }

            // --- Traversal Setup ---
            var traversalStack = new Stack<string>();
            var visitedDirs = new HashSet<string>(StringComparer.Ordinal); // Tracks visited dirs for traversal path
            var pickedFromFileInDir = new HashSet<string>(StringComparer.Ordinal); // Tracks dirs we picked a file from

            // *** MODIFIED: Determine the ACTUAL starting point for traversal ***

            // First, mark essential paths as visited *before* deciding the start point
            visitedDirs.Add(seedParentDir); // Always mark seed parent visited initially
            foreach (var excludedPath in excludedArtistPaths)
            {
                if (visitedDirs.Add(excludedPath)) // Add returns true if it was added
                {
                    _logger.LogDebug(" -> Preemptively marked initially identified seed artist path '{Path}' as visited.", excludedPath);
                }
            }

            string? initialStackPushDir = null; // The directory to actually push onto the stack first
            bool seedParentIsUnderExcludedArtistPath = validatedSeedArtistPath != null &&
                                                      (seedParentDir.Equals(validatedSeedArtistPath, StringComparison.OrdinalIgnoreCase) ||
                                                       seedParentDir.StartsWith(validatedSeedArtistPath + SoulseekSeparator, StringComparison.OrdinalIgnoreCase));

            if (seedParentIsUnderExcludedArtistPath)
            {
                // The normal starting point is inside the excluded zone. Try to start from the parent of the excluded zone.
                _logger.LogInformation("Seed parent '{SeedParent}' is within the excluded artist path '{ArtistPath}'. Attempting to start traversal from artist path's parent.", seedParentDir, validatedSeedArtistPath);
                string? artistParent = GetParentPathManual(validatedSeedArtistPath!); // Use ! as validatedSeedArtistPath is non-null here

                if (!string.IsNullOrEmpty(artistParent))
                {
                    // Check if this calculated parent exists and hasn't already been visited (e.g., if it was somehow also excluded)
                    if ((browseDirLookup.ContainsKey(artistParent) || lockedDirs.Contains(artistParent)) && !visitedDirs.Contains(artistParent))
                    {
                        initialStackPushDir = artistParent;
                        _logger.LogDebug(" -> Will start traversal from artist parent: '{ArtistParent}'", artistParent);
                    }
                    else if (visitedDirs.Contains(artistParent))
                    {
                         _logger.LogWarning(" -> Artist parent '{ArtistParent}' is already visited/excluded. Cannot start traversal from there.", artistParent);
                    }
                    else
                    {
                         _logger.LogWarning(" -> Artist parent '{ArtistParent}' not found in browse/locked directories. Cannot start traversal from there.", artistParent);
                    }
                }
                else
                {
                     _logger.LogWarning(" -> Excluded artist path '{ArtistPath}' is at root or has no parent. Cannot automatically go up to start traversal.", validatedSeedArtistPath);
                }
            }
            else
            {
                // Seed parent is NOT excluded, start there as usual, provided it exists.
                if (browseDirLookup.ContainsKey(seedParentDir) || lockedDirs.Contains(seedParentDir))
                {
                     // visitedDirs already contains seedParentDir, no need to check again
                     initialStackPushDir = seedParentDir;
                     _logger.LogDebug("Starting traversal from seed parent directory: '{SeedParentDir}'", seedParentDir);
                }
                else
                {
                     // This case should be rare if the initial search found the file, but handle defensively.
                     _logger.LogWarning("Seed parent directory '{SeedParentDir}' not found in browse results or locked directories for {User}, despite not being excluded. Cannot start traversal.", seedParentDir, username);
                     return picks; // Cannot start if seed parent doesn't exist in browse data
                }
            }

            // Push the determined starting directory (if any)
            if (initialStackPushDir != null)
            {
                traversalStack.Push(initialStackPushDir);
                // Ensure the actual starting point is marked visited *if it wasn't already*
                // (It would be if it was the seedParentDir or an excluded path, but might not be if it's the artistParent)
                if (visitedDirs.Add(initialStackPushDir))
                {
                     _logger.LogDebug(" -> Marked actual starting directory '{InitialDir}' as visited.", initialStackPushDir);
                }
                 _logger.LogDebug(" -> Pushed initial directory '{InitialDir}' onto stack.", initialStackPushDir);
            }
            else
            {
                 _logger.LogWarning("Could not determine a valid starting directory for traversal for user {User}. No picks possible.", username);
                 return picks; // Cannot start traversal
            }
            // --- *** END Traversal Setup Modification *** ---


            while (traversalStack.Count > 0 && picks.Count < maxPicks && !cancellationToken.IsCancellationRequested)
            {
                var currentDir = traversalStack.Pop();
                _logger.LogDebug("Traversal: Popped '{CurrentDir}' from stack. Stack size: {StackSize}", currentDir, traversalStack.Count);

                // Check if currentDir is within an excluded artist path (still needed for general traversal)
                bool isExcluded = excludedArtistPaths.Any(excluded => currentDir.Equals(excluded, StringComparison.OrdinalIgnoreCase) || currentDir.StartsWith(excluded + SoulseekSeparator, StringComparison.OrdinalIgnoreCase));
                if (isExcluded)
                {
                    _logger.LogDebug("Skipping processing of '{CurrentDir}' because it is within an excluded artist path.", currentDir);
                    visitedDirs.Add(currentDir); // Ensure visited
                    continue; // Skip file and child processing
                }

                // *** NOTE: The check for isSeedParentDirectory for skipping file processing is potentially less relevant now,
                //     as if the seed parent was excluded, we started higher up. If it wasn't excluded,
                //     processing its files might be desired if it's not the artist folder itself.
                //     However, keeping it prevents picking siblings of the seed track in the exact same folder.
                bool isSeedParentDirectory = currentDir.Equals(seedParentDir, StringComparison.Ordinal);
                if (isSeedParentDirectory)
                {
                    _logger.LogDebug("Skipping file processing in '{CurrentDir}' because it is the seed track's parent directory.", currentDir);
                }

                // --- 1. Process Files (Only if NOT the seed parent dir, not already picked from, not locked, and not excluded) ---
                bool pickedThisIteration = false;
                if (!isSeedParentDirectory && !pickedFromFileInDir.Contains(currentDir) && !lockedDirs.Contains(currentDir)) // isExcluded check happened above
                {
                     if (browseDirLookup.TryGetValue(currentDir, out var currentDirEntry) && currentDirEntry.Files != null)
                    {
                        // ... (File processing logic remains the same) ...
                        _logger.LogDebug("Processing files in '{CurrentDir}'. Found {FileCount} files in browse data.", currentDir, currentDirEntry.Files.Count);
                        var filesInDir = currentDirEntry.Files
                                            .Where(f => f != null && !string.IsNullOrEmpty(f.Filename)
                                                        && IsAllowedExtension(f.Filename)
                                                        && f.Size > 0 && f.Size <= fileSizeCapBytes)
                                            .ToList();
                        _logger.LogDebug(" -> {Count} files meet extension/size criteria in '{CurrentDir}'.", filesInDir.Count, currentDir);

                        Shuffle(filesInDir);

                        foreach (var file in filesInDir)
                        {
                            // ... (Path reconstruction, fuzzy matching logic remains the same) ...
                            if (string.IsNullOrEmpty(file.Filename)) {
                                _logger.LogWarning("Skipping file entry with null/empty filename in directory '{CurrentDir}'", currentDir);
                                continue;
                            }
                            string potentialRelativeFileName = file.Filename;
                            string reconstructedFullPath;
                            if (potentialRelativeFileName.Contains(SoulseekSeparator)) { reconstructedFullPath = potentialRelativeFileName; }
                            else { reconstructedFullPath = currentDir + SoulseekSeparator + potentialRelativeFileName; }

                            var currentFileNameOnly = GetFileNameManual(reconstructedFullPath);
                            var currentFileNameWithoutExtension = GetFileNameWithoutExtensionManual(currentFileNameOnly);
                            var preprocessedCurrentFileName = PreprocessForFuzzyMatch(currentFileNameWithoutExtension);
                            int fileSimilarity = (string.IsNullOrEmpty(preprocessedSeedFileName) || string.IsNullOrEmpty(preprocessedCurrentFileName))
                                                    ? 0 : Fuzz.TokenSetRatio(preprocessedSeedFileName, preprocessedCurrentFileName);

                            _logger.LogDebug(" -> Checking file: '{FileName}' (Preprocessed: '{PreprocessedFile}') vs Seed (Preprocessed: '{PreprocessedSeed}'). TokenSetRatio: {Score}%",
                                currentFileNameOnly, preprocessedCurrentFileName, preprocessedSeedFileName, fileSimilarity);

                            if (fileSimilarity < SimilarityThreshold)
                            {
                                picks.Add(reconstructedFullPath);
                                pickedFromFileInDir.Add(currentDir);
                                pickedThisIteration = true;
                                _logger.LogDebug("Picked file: {FileName} (Full Path: '{FullPath}', TokenSetRatio to seed '{SeedPreprocessed}': {Score}% < {Threshold}%)",
                                    currentFileNameOnly, reconstructedFullPath, preprocessedSeedFileName, fileSimilarity, SimilarityThreshold);
                                _logger.LogDebug(" -> Marked '{CurrentDir}' as having a file picked.", currentDir);

                                // --- Gemini Artist Heuristic Trigger (For Picked Artist) ---
                                if (!skipGeminiHeuristic && geminiCallsMade < maxGeminiCallsPerUser)
                                {
                                    _logger.LogInformation("Gemini Trigger: First pick from user {User}. Calling Gemini to identify artist in picked path: '{Path}' (Call {CallNum}/{MaxCalls})",
                                        username, currentDir, geminiCallsMade + 1, maxGeminiCallsPerUser);

                                    string? pickedArtistFolderName = await GetArtistFolderFromPathAsync(currentDir, cancellationToken);
                                    geminiCallsMade++;

                                    if (pickedArtistFolderName != null)
                                    {
                                        _logger.LogInformation("Gemini Response for picked path '{Path}': Identified '{ArtistFolder}' as potential artist folder.", currentDir, pickedArtistFolderName);
                                        string? validatedPickedArtistPath = ValidateAndGetFullPathForArtistFolder(currentDir, pickedArtistFolderName);

                                        if (validatedPickedArtistPath != null)
                                        {
                                            _logger.LogInformation("Gemini Action: Validated picked artist folder '{ArtistFolder}' found at path '{ArtistPath}'. Adding to excluded paths and redirecting traversal.",
                                                pickedArtistFolderName, validatedPickedArtistPath);

                                            excludedArtistPaths.Add(validatedPickedArtistPath);
                                            visitedDirs.Add(validatedPickedArtistPath); // Ensure visited

                                            string? artistParentPath = GetParentPathManual(validatedPickedArtistPath);

                                            traversalStack.Clear();
                                            _logger.LogDebug(" -> Cleared traversal stack.");

                                            var newVisitedDirs = new HashSet<string>(StringComparer.Ordinal);
                                            if (!string.IsNullOrEmpty(seedParentDir)) { newVisitedDirs.Add(seedParentDir); } // Keep original seed parent visited
                                            newVisitedDirs.Add(currentDir); // Keep picked dir visited
                                            foreach(var excluded in excludedArtistPaths) { newVisitedDirs.Add(excluded); } // Keep ALL excluded paths visited

                                            visitedDirs = newVisitedDirs;
                                            _logger.LogDebug(" -> Reset visitedDirs set, preserving essential paths and all {Count} excluded artist paths. New size: {NewCount}", excludedArtistPaths.Count, visitedDirs.Count);

                                            if (!string.IsNullOrEmpty(artistParentPath) && (browseDirLookup.ContainsKey(artistParentPath) || lockedDirs.Contains(artistParentPath)))
                                            {
                                                if (!visitedDirs.Contains(artistParentPath))
                                                {
                                                    traversalStack.Push(artistParentPath);
                                                    visitedDirs.Add(artistParentPath);
                                                    _logger.LogInformation(" -> Pushing parent '{ParentPath}' onto stack to continue search above artist level. Added to visited.", artistParentPath);
                                                }
                                                else { _logger.LogDebug(" -> Parent '{ParentPath}' was already in the essential/excluded preserved set. Not pushing again.", artistParentPath); }
                                            }
                                            else { _logger.LogInformation(" -> Picked artist folder '{ArtistFolder}' has no valid parent or is at root. Traversal will stop unless stack had prior entries.", pickedArtistFolderName); }

                                            goto EndFileProcessing; // Break file loop
                                        }
                                        else { _logger.LogWarning("Gemini Validation Failed: Folder '{ArtistFolder}' returned by Gemini not found as a component in picked path '{Path}'.", pickedArtistFolderName, currentDir); }
                                    }
                                    else { _logger.LogInformation("Gemini Response for picked path '{Path}': No specific artist folder identified or error occurred. Continuing standard traversal.", currentDir); }
                                }
                                // --- END Gemini Section for Picked Artist ---
                                break; // Stop after one pick per directory
                            }
                            else { _logger.LogDebug(" -> Skipping file: {FileName} (TokenSetRatio: {Score}% >= {Threshold}%)", currentFileNameOnly, fileSimilarity, SimilarityThreshold); }
                        } // End foreach file
                        EndFileProcessing:;
                        _logger.LogDebug(" -> Finished processing files in '{CurrentDir}'. Picked a file this iteration? {PickedStatus}", currentDir, pickedThisIteration);
                    }
                    else { _logger.LogTrace("Directory '{Dir}' not found in browse results or has null Files collection. Skipping file processing.", currentDir); }
                } // --- End File Processing Check ---

                if (picks.Count >= maxPicks || cancellationToken.IsCancellationRequested)
                {
                     _logger.LogDebug("Global pick limit ({Limit}) reached or cancellation requested. Stopping traversal.", maxPicks);
                     break;
                }

                if (pickedThisIteration && !skipGeminiHeuristic && traversalStack.Count > 0 && !cancellationToken.IsCancellationRequested) {
                     _logger.LogDebug("Gemini heuristic potentially modified stack after pick. Continuing to next iteration of while loop.");
                     continue; // Skip child processing for currentDir, process the parent pushed by Gemini
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
                    // Check visited AND excluded before processing
                    if (visitedDirs.Contains(childDir)) { _logger.LogTrace("Child '{ChildDir}' already visited. Skipping.", childDir); continue; }
                    bool isChildExcluded = excludedArtistPaths.Any(excluded => childDir.Equals(excluded, StringComparison.OrdinalIgnoreCase) || childDir.StartsWith(excluded + SoulseekSeparator, StringComparison.OrdinalIgnoreCase));
                    if (isChildExcluded) { _logger.LogDebug("Child directory '{ChildDir}' is within an excluded artist path. Marking visited, skipping descent.", childDir); visitedDirs.Add(childDir); continue; }

                    // ... (Similarity check logic remains the same) ...
                    var childDirNameOnly = GetLastPathComponentManual(childDir);
                    var preprocessedChildDirName = PreprocessForFuzzyMatch(childDirNameOnly);
                    int similarity = (string.IsNullOrEmpty(preprocessedSeedFileName) || string.IsNullOrEmpty(preprocessedChildDirName))
                                        ? 0 : Fuzz.TokenSetRatio(preprocessedSeedFileName, preprocessedChildDirName);

                    _logger.LogTrace("Checking child dir '{ChildName}' (Preprocessed: '{PreprocessedChild}') against seed (Preprocessed: '{PreprocessedSeed}'). TokenSetRatio: {Similarity}%",
                        childDirNameOnly, preprocessedChildDirName, preprocessedSeedFileName, similarity);

                    if (similarity >= SimilarityThreshold)
                    {
                        _logger.LogDebug("Child directory '{ChildName}' (preprocessed:{preprocessedChildDirName}) is SIMILAR (TokenSetRatio: {Similarity}% >= {Threshold}%) to seed. Marking visited, skipping descent.",
                             childDirNameOnly, preprocessedChildDirName, similarity, SimilarityThreshold);
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
                    foreach (var dissimilarChild in dissimilarChildrenToPush)
                    {
                        // Double-check not visited/excluded before pushing
                        if (!visitedDirs.Contains(dissimilarChild) && !excludedArtistPaths.Any(excluded => dissimilarChild.Equals(excluded, StringComparison.OrdinalIgnoreCase) || dissimilarChild.StartsWith(excluded + SoulseekSeparator, StringComparison.OrdinalIgnoreCase)))
                        {
                            traversalStack.Push(dissimilarChild);
                            visitedDirs.Add(dissimilarChild);
                             _logger.LogTrace(" -> Pushed dissimilar child: {ChildDir}", dissimilarChild);
                        } else { _logger.LogTrace(" -> Skipping push of dissimilar child {ChildDir} as it became visited or is excluded.", dissimilarChild); }
                    }
                    _logger.LogDebug(" -> Stack size after pushing dissimilar children: {StackSize}", traversalStack.Count);
                }
                else // No dissimilar children to explore downwards
                {
                    _logger.LogDebug("Decision: No unvisited dissimilar children found for '{CurrentDir}'. Attempting to go up.", currentDir);
                    if (canGoUp && parentDir != null)
                    {
                        // Check not visited/excluded before pushing parent
                        bool isParentExcluded = excludedArtistPaths.Any(excluded => parentDir.Equals(excluded, StringComparison.OrdinalIgnoreCase) || parentDir.StartsWith(excluded + SoulseekSeparator, StringComparison.OrdinalIgnoreCase));
                        if (!visitedDirs.Contains(parentDir) && !isParentExcluded)
                        {
                            _logger.LogDebug(" -> Can go up. Pushing parent '{ParentDir}' onto stack. Stack size: {StackSize}", parentDir, traversalStack.Count + 1);
                            traversalStack.Push(parentDir);
                            visitedDirs.Add(parentDir);
                        } else { _logger.LogTrace(" -> Parent '{ParentDir}' already visited or is excluded, not pushing again.", parentDir); }
                    }
                    else { _logger.LogDebug(" -> Cannot go up from '{CurrentDir}'. No parent, parent not in browse/locked results, or parent already visited/excluded. Ending branch exploration.", currentDir); }
                }
            } // --- End while loop ---

            if (cancellationToken.IsCancellationRequested)
            {
                 _logger.LogWarning("Crawl for {User} was cancelled during traversal.", username);
            }

            _logger.LogDebug("Traversal finished for {User}. Found {Count} picks (Quota: {Quota}). Gemini calls made: {GeminiCalls}/{MaxGeminiCalls}. Excluded artist paths: {ExcludedCount}",
                username, picks.Count, maxPicks, geminiCallsMade, maxGeminiCallsPerUser, excludedArtistPaths.Count);
            return picks;
        }



        // *** ADDED: Helper to validate Gemini response and get full path ***
        /// <summary>
        /// Validates if the artist folder name returned by Gemini exists as a component in the original path
        /// and returns the full path to that component if found.
        /// </summary>
        /// <param name="originalPath">The full directory path sent to Gemini.</param>
        /// <param name="artistFolderName">The folder name returned by Gemini.</param>
        /// <returns>The full path ending with the validated artist folder, or null if not found/validated.</returns>
        private string? ValidateAndGetFullPathForArtistFolder(string originalPath, string artistFolderName)
        {
            if (string.IsNullOrEmpty(originalPath) || string.IsNullOrEmpty(artistFolderName))
            {
                return null;
            }

            string[] pathComponents = originalPath.Split(SoulseekSeparator);
            int artistIndex = -1;
            for (int i = 0; i < pathComponents.Length; i++)
            {
                // Use OrdinalIgnoreCase for robustness against casing variations from Gemini or user shares
                if (pathComponents[i].Equals(artistFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    artistIndex = i;
                    break;
                }
            }

            if (artistIndex != -1)
            {
                // Reconstruct the path using the original components up to the found index
                string validatedArtistFolderPath = string.Join(SoulseekSeparator.ToString(), pathComponents.Take(artistIndex + 1));
                return validatedArtistFolderPath;
            }

            return null; // Folder name not found as a component
        }

    // *** ADDED: Method to call Gemini API with retry logic ***
    /// <summary>
    /// Calls the Gemini API to identify the artist folder in a given path.
    /// Includes retry logic for rate limiting (429) errors using exponential backoff,
    /// prioritizing the 'retryDelay' field from the API response if available.
    /// </summary>
    /// <param name="directoryPath">The directory path to analyze.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identified artist folder name (sanitized), or null if none found, error occurred, or heuristic disabled.</returns>
    private async Task<string?> GetArtistFolderFromPathAsync(string directoryPath, CancellationToken cancellationToken)
    {
        if (_geminiModel == null || string.IsNullOrEmpty(directoryPath))
        {
            return null;
        }

        string prompt = string.Format(_geminiPromptTemplate, directoryPath);
        int attempt = 0;
        int totalDelayMs = 0;
        int exponentialBackoffDelayMs = 1000; // Start with 1 second for fallback

        while (attempt <= MaxGeminiRetries && totalDelayMs < MaxGeminiTotalRetryDelaySeconds * 1000 && !cancellationToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                _logger.LogDebug("Calling Gemini API (Attempt {Attempt}/{MaxAttempts}) for path: {Path}", attempt, MaxGeminiRetries + 1, directoryPath);
                var response = await _geminiModel.GenerateContent(prompt, cancellationToken: cancellationToken);

                if (response == null || string.IsNullOrWhiteSpace(response.Text))
                {
                    _logger.LogWarning("Gemini API returned null or empty response for path: {Path}", directoryPath);
                    return null; // Treat empty response as failure/None
                }

                var rawResponseText = response.Text;
                 _logger.LogDebug("Gemini Raw Response: '{Response}'", rawResponseText);

                // Sanitize output
                var sanitizedResponse = rawResponseText.Trim().Trim('`', '"', '\'', '(', ')', '.');

                if (sanitizedResponse.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Gemini identified no specific artist folder for path: {Path}", directoryPath);
                    return null; // Explicit "None" means no artist found
                }

                if (string.IsNullOrWhiteSpace(sanitizedResponse))
                {
                     _logger.LogWarning("Gemini response was empty after sanitization for path: {Path}. Raw: '{Raw}'", directoryPath, rawResponseText);
                     return null;
                }

                // Return the sanitized potential artist name
                return sanitizedResponse;

            }
            catch (HttpRequestException httpEx)
            {
                // *** CHANGE POINT: Check status code *before* deciding how to log ***
                if (httpEx.StatusCode == HttpStatusCode.TooManyRequests) // 429 Rate Limit
                {
                    // *** Log WITHOUT the exception object for expected rate limits ***
                    _logger.LogWarning("HTTP Error calling Gemini API (Attempt {Attempt}). Status Code: {StatusCode}. Path: {Path}. Message: {ErrorMessage}",
                        attempt, httpEx.StatusCode, directoryPath, httpEx.Message); // Log message instead of full exception

                    if (attempt > MaxGeminiRetries || totalDelayMs >= MaxGeminiTotalRetryDelaySeconds * 1000)
                    {
                        _logger.LogError("Gemini rate limit exceeded and max retries ({MaxRetries}) or max delay ({MaxDelay}s) reached. Giving up for this path: {Path}", MaxGeminiRetries, MaxGeminiTotalRetryDelaySeconds, directoryPath);
                        return null;
                    }

                    // --- Attempt to parse retryDelay from API response ---
                    int delayMs = exponentialBackoffDelayMs; // Default to exponential backoff
                    string delaySource = "exponential backoff";
                    TimeSpan? delayFromApi = null;

                    try
                    {
                        // The exception message often contains the JSON body
                        Match match = GeminiRetryDelayRegex.Match(httpEx.Message);
                        if (match.Success && match.Groups.Count > 1 && int.TryParse(match.Groups[1].Value, out int seconds))
                        {
                            delayFromApi = TimeSpan.FromSeconds(seconds);
                            // Use API delay + small buffer (e.g., 500ms) to avoid hitting limit exactly on reset
                            delayMs = (int)delayFromApi.Value.TotalMilliseconds + 500;
                            delaySource = "API suggestion";
                             _logger.LogDebug("Parsed retryDelay: {Seconds}s from Gemini error response.", seconds);
                        }
                        else
                        {
                             _logger.LogDebug("Could not parse retryDelay from Gemini error response. Falling back to exponential backoff.");
                        }
                    }
                    catch (Exception parseEx)
                    {
                        _logger.LogWarning(parseEx, "Error parsing retryDelay from Gemini error response message. Falling back to exponential backoff.");
                    }
                    // --- End parsing retryDelay ---


                    // Ensure delay isn't excessively long, respecting our overall timeout somewhat
                    // (though API delay should generally be trusted)
                    delayMs = Math.Min(delayMs, 60000); // Cap individual delay at 60s regardless of source
                    delayMs = Math.Max(delayMs, 500); // Ensure minimum delay

                    // *** Log the wait message at Information level ***
                    _logger.LogInformation("Gemini rate limit hit. Waiting {DelayMs}ms (using {DelaySource}) before retry {RetryNum}/{MaxRetries}. Total delay so far: {TotalDelayMs}ms. Path: {Path}",
                        delayMs, delaySource, attempt, MaxGeminiRetries, totalDelayMs, directoryPath);

                    try
                    {
                        await Task.Delay(delayMs, cancellationToken);
                    }
                    catch (OperationCanceledException) { throw; } // Propagate cancellation

                    totalDelayMs += delayMs;
                    // Still update exponential backoff value in case the *next* error doesn't provide a delay
                    exponentialBackoffDelayMs = Math.Min(exponentialBackoffDelayMs * 2, 60000);
                }
                else // Handle other non-429 HTTP errors
                {
                    // *** Log WITH the exception object for unexpected HTTP errors ***
                    _logger.LogError(httpEx, "Non-retryable HTTP error calling Gemini API (Attempt {Attempt}). Status Code: {StatusCode}. Path: {Path}", attempt, httpEx.StatusCode, directoryPath);
                    return null; // Treat as failure
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Gemini API call cancelled for path: {Path}", directoryPath);
                throw; // Re-throw cancellation
            }
            catch (ArgumentNullException argEx)
            {
                 _logger.LogError(argEx, "Argument Null Exception calling Gemini API (likely prompt issue). Path: {Path}", directoryPath);
                 return null; // Don't retry argument issues
            }
            catch (Exception ex)
            {
                // Log WITH exception for unexpected errors
                _logger.LogError(ex, "Unexpected error calling Gemini API (Attempt {Attempt}). Path: {Path}", attempt, directoryPath);
                return null;
            }
        } // End while loop

        if (cancellationToken.IsCancellationRequested)
        {
             _logger.LogWarning("Gemini API call cancelled after retries for path: {Path}", directoryPath);
        }
        else
        {
             _logger.LogError("Gemini API call failed after {Attempts} attempts (due to retries/total delay). Giving up for path: {Path}", attempt, directoryPath);
        }

        return null; // Failed after retries or cancelled
    }


        // --- Disconnect ---
        private async Task DisconnectGracefullyAsync()
        {
            if (_soulseekClient != null && _soulseekClient.State != SoulseekClientStates.Disconnected)
            {
                _logger.LogInformation("Disconnecting Soulseek client...");
                try
                {
                    // No async disconnect method in Soulseek.NET client library?
                    _soulseekClient.Disconnect();
                    await Task.Delay(250); // Give some time for cleanup?
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

    /// <summary>
    /// Preprocesses a string (filename or directory name, without extension) for fuzzy matching.
    /// </summary>
    private static string PreprocessForFuzzyMatch(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";

        string processed = s.ToLowerInvariant(); // Lowercase first
        processed = ParenthesesContentRegex.Replace(processed, ""); // Remove content within () and []
        processed = processed.Replace("-", ""); // Remove hyphens
        processed = processed.Replace(".", ""); // Remove dots
        processed = Regex.Replace(processed, @"\s+", " ").Trim(); // Normalize whitespace

        if (!string.IsNullOrEmpty(processed))
        {
            int firstSpaceIndex = processed.IndexOf(' ');
            string firstWord = (firstSpaceIndex == -1) ? processed : processed.Substring(0, firstSpaceIndex);

            if (firstWord.Length > 0 && firstWord.All(char.IsDigit))
            {
                processed = (firstSpaceIndex == -1) ? "" : processed.Substring(firstSpaceIndex + 1).TrimStart();
                processed = processed.Trim(); // Trim again after potential removal
            }
        }
        // Normalize whitespace again in case removing first word left consecutive spaces
        processed = Regex.Replace(processed, @"\s+", " ").Trim();

        return processed;
    }

    /// <summary>
    /// Checks if a filename has an allowed audio extension.
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

    private static string GetFileNameManual(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        int lastSeparatorIndex = path.LastIndexOf(SoulseekSeparator);
        return (lastSeparatorIndex == -1) ? path : path.Substring(lastSeparatorIndex + 1);
    }

    private static string GetFileNameWithoutExtensionManual(string? filename)
    {
        if (string.IsNullOrEmpty(filename)) return "";
        int lastDotIndex = filename.LastIndexOf('.');
        return (lastDotIndex <= 0) ? filename : filename.Substring(0, lastDotIndex);
    }

    private static string? GetParentPathManual(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string tempPath = path.TrimEnd(SoulseekSeparator); // Handle trailing separator before finding parent
        if (string.IsNullOrEmpty(tempPath)) return null;
        int lastSeparatorIndex = tempPath.LastIndexOf(SoulseekSeparator);
        if (lastSeparatorIndex < 0) return null; // No separator found, it's a root element
        if (lastSeparatorIndex == 0) return null; // Parent is root, e.g., "\Folder" -> parent is root (null)
        return tempPath.Substring(0, lastSeparatorIndex);
    }

    private static string GetLastPathComponentManual(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        string trimmedPath = path.TrimEnd(SoulseekSeparator);
        if (string.IsNullOrEmpty(trimmedPath)) return "";
        int lastSeparatorIndex = trimmedPath.LastIndexOf(SoulseekSeparator);
        return (lastSeparatorIndex == -1) ? trimmedPath : trimmedPath.Substring(lastSeparatorIndex + 1);
    }

    private static bool IsDirectChildManual(string parentPath, string childPath)
    {
        if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(childPath)) return false;

        // Normalize by removing trailing separators for comparison logic
        string normParent = parentPath.TrimEnd(SoulseekSeparator);
        string normChild = childPath.TrimEnd(SoulseekSeparator);

        // Child must be longer than parent
        if (normChild.Length <= normParent.Length) return false;

        // Child must start with parent + separator
        if (!normChild.StartsWith(normParent + SoulseekSeparator, StringComparison.Ordinal)) return false;

        // Ensure no more separators after the parent part in the child path
        // Start search *after* the expected separator following the parent path
        int nextSeparatorIndex = normChild.IndexOf(SoulseekSeparator, normParent.Length + 1);
        return nextSeparatorIndex == -1;
    }

    } // End SoulseekRadarService Class
} // End Namespace