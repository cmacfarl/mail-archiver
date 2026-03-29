using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Requests;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.Core;
using MailArchiver.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailArchiver.Services.Providers
{
    public class GmailEmailService : IProviderEmailService
    {
        private readonly MailArchiverDbContext _context;
        private readonly ILogger<GmailEmailService> _logger;
        private readonly ISyncJobService _syncJobService;
        private readonly BatchOperationOptions _batchOptions;
        private readonly MailSyncOptions _mailSyncOptions;
        private readonly EmailCoreService _coreService;

        private static readonly string[] Scopes = { GmailService.Scope.GmailReadonly };

        // Gmail API hard limit for messages.list
        private const int ListPageSize = 500;

        public GmailEmailService(
            MailArchiverDbContext context,
            ILogger<GmailEmailService> logger,
            ISyncJobService syncJobService,
            IOptions<BatchOperationOptions> batchOptions,
            IOptions<MailSyncOptions> mailSyncOptions,
            EmailCoreService coreService)
        {
            _context = context;
            _logger = logger;
            _syncJobService = syncJobService;
            _batchOptions = batchOptions.Value;
            _mailSyncOptions = mailSyncOptions.Value;
            _coreService = coreService;
        }

        private async Task<GmailService> CreateGmailServiceAsync(MailAccount account, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(account.GmailCredentialsFile))
                throw new InvalidOperationException($"Gmail account '{account.Name}' has no credentials file configured.");

            if (!File.Exists(account.GmailCredentialsFile))
                throw new FileNotFoundException($"Gmail credentials file not found: {account.GmailCredentialsFile}");

            var tokenStoreName = !string.IsNullOrEmpty(account.GmailTokenStoreName)
                ? account.GmailTokenStoreName
                : $"gmail_token_{SanitizeForPath(account.EmailAddress)}";

            using var stream = new FileStream(account.GmailCredentialsFile, FileMode.Open, FileAccess.Read);
            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                GoogleClientSecrets.FromStream(stream).Secrets,
                Scopes,
                "user",
                cancellationToken,
                new FileDataStore(tokenStoreName, true));

            return new GmailService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "MailArchiver"
            });
        }

        private static string SanitizeForPath(string input) =>
            string.Concat(input.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        private CancellationToken GetJobCancellationToken(string? jobId) =>
            jobId != null
                ? (_syncJobService.GetJob(jobId)?.CancellationTokenSource?.Token ?? CancellationToken.None)
                : CancellationToken.None;

        public async Task SyncMailAccountAsync(MailAccount account, string? jobId = null)
        {
            _logger.LogInformation("Starting Gmail sync for account: {AccountName}", account.Name);

            if (account.GmailDownloadAll)
            {
                await SyncAllMailAsync(account, jobId);
                return;
            }

            var senderFilters = await _context.GmailSenderFilters
                .Where(f => f.MailAccountId == account.Id && f.IsEnabled)
                .ToListAsync();

            if (senderFilters.Count == 0)
            {
                _logger.LogInformation("No enabled filters for Gmail account {AccountName}, skipping", account.Name);
                if (jobId != null)
                    _syncJobService.CompleteJob(jobId, true);
                return;
            }

            GmailService gmailService;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_mailSyncOptions.ConnectionTimeoutSeconds));
                gmailService = await CreateGmailServiceAsync(account, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create Gmail service for account {AccountName}. " +
                    "If this is the first run, ensure the application has browser access to complete OAuth authorization.", account.Name);
                if (jobId != null)
                    _syncJobService.CompleteJob(jobId, false, ex.Message);
                return;
            }

            var cancellationToken = GetJobCancellationToken(jobId);
            var totalProcessed = 0;
            var totalNew = 0;
            var totalSkipped = 0;
            var totalFailed = 0;

            foreach (var filter in senderFilters)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var filterLabel = $"{filter.FilterType}:{filter.EmailAddress}";
                _logger.LogInformation("Syncing messages for filter {Filter} on account {AccountName}",
                    filterLabel, account.Name);

                if (jobId != null)
                    _syncJobService.UpdateJobProgress(jobId, job => { job.CurrentFolder = filterLabel; });

                try
                {
                    var filterStartTime = DateTime.UtcNow;

                    var queryPrefix = filter.FilterType == GmailFilterType.Recipient ? "to" : "from";
                    var query = $"{queryPrefix}:{filter.EmailAddress}";
                    if (filter.LastSync.HasValue)
                    {
                        // Treat as UTC explicitly — DB returns timestamp without timezone as DateTimeKind.Unspecified,
                        // and an implicit cast to DateTimeOffset would use local time (currently PDT, UTC-7),
                        // producing a Unix timestamp 7 hours in the future and causing Gmail to skip recent mail.
                        var unixTimestamp = new DateTimeOffset(filter.LastSync.Value, TimeSpan.Zero).ToUnixTimeSeconds();
                        query += $" after:{unixTimestamp}";
                    }

                    string? pageToken = null;
                    do
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        var listRequest = gmailService.Users.Messages.List("me");
                        listRequest.Q = query;
                        listRequest.MaxResults = ListPageSize;
                        if (pageToken != null)
                            listRequest.PageToken = pageToken;

                        ListMessagesResponse listResponse;
                        try
                        {
                            listResponse = await listRequest.ExecuteAsync(cancellationToken);
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to list messages for filter {Filter}", filterLabel);
                            totalFailed++;
                            break;
                        }

                        if (listResponse.Messages == null || listResponse.Messages.Count == 0)
                            break;

                        var (p, n, s, f) = await FetchAndArchiveBatchAsync(
                            gmailService, listResponse.Messages, account, filterLabel, cancellationToken);
                        totalProcessed += p;
                        totalNew += n;
                        totalSkipped += s;
                        totalFailed += f;

                        if (jobId != null)
                        {
                            _syncJobService.UpdateJobProgress(jobId, job =>
                            {
                                job.ProcessedEmails = totalProcessed;
                                job.NewEmails = totalNew;
                                job.FailedEmails = totalFailed;
                            });
                        }

                        pageToken = listResponse.NextPageToken;

                        if (pageToken != null && _batchOptions.PauseBetweenBatchesMs > 0)
                            await Task.Delay(_batchOptions.PauseBetweenBatchesMs);

                    } while (pageToken != null);

                    // Update LastSync to when this sync run started, not the email date
                    filter.LastSync = filterStartTime;
                    await _context.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error syncing filter {Filter} for account {AccountName}",
                        filterLabel, account.Name);
                    totalFailed++;
                }
            }

            if (totalFailed == 0 && !cancellationToken.IsCancellationRequested)
            {
                var freshAccount = await _context.MailAccounts.FindAsync(account.Id);
                if (freshAccount != null)
                {
                    freshAccount.LastSync = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                }
            }

            if (jobId != null)
                _syncJobService.CompleteJob(jobId, totalFailed == 0 && !cancellationToken.IsCancellationRequested);

            _logger.LogInformation(
                "Gmail sync complete for {AccountName}: {Processed} processed, {New} newly archived, {Skipped} skipped (already archived), {Failed} failed",
                account.Name, totalProcessed, totalNew, totalSkipped, totalFailed);
        }

        private async Task SyncAllMailAsync(MailAccount account, string? jobId)
        {
            GmailService gmailService;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_mailSyncOptions.ConnectionTimeoutSeconds));
                gmailService = await CreateGmailServiceAsync(account, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create Gmail service for account {AccountName}. " +
                    "If this is the first run, ensure the application has browser access to complete OAuth authorization.", account.Name);
                if (jobId != null)
                    _syncJobService.CompleteJob(jobId, false, ex.Message);
                return;
            }

            var cancellationToken = GetJobCancellationToken(jobId);
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var syncStartTime = DateTime.UtcNow;
            var totalProcessed = 0;
            var totalNew = 0;
            var totalSkipped = 0;
            var totalFailed = 0;

            // Treat LastSync as UTC explicitly — DB returns timestamp without timezone as DateTimeKind.Unspecified,
            // and an implicit cast to DateTimeOffset uses local time, producing a wrong (future) Unix timestamp.
            var query = account.LastSync > epoch
                ? $"after:{new DateTimeOffset(account.LastSync, TimeSpan.Zero).ToUnixTimeSeconds()}"
                : string.Empty;

            _logger.LogInformation("Download-all sync for {AccountName}, query: '{Query}'", account.Name, query);

            if (jobId != null)
                _syncJobService.UpdateJobProgress(jobId, job => { job.CurrentFolder = "All Mail"; });

            try
            {
                string? pageToken = null;
                do
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var listRequest = gmailService.Users.Messages.List("me");
                    if (!string.IsNullOrEmpty(query))
                        listRequest.Q = query;
                    listRequest.MaxResults = ListPageSize;
                    if (pageToken != null)
                        listRequest.PageToken = pageToken;

                    ListMessagesResponse listResponse;
                    try
                    {
                        listResponse = await listRequest.ExecuteAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to list messages for download-all on account {AccountName}", account.Name);
                        totalFailed++;
                        break;
                    }

                    if (listResponse.Messages == null || listResponse.Messages.Count == 0)
                        break;

                    var (p, n, s, f) = await FetchAndArchiveBatchAsync(
                        gmailService, listResponse.Messages, account, "All Mail", cancellationToken);
                    totalProcessed += p;
                    totalNew += n;
                    totalSkipped += s;
                    totalFailed += f;

                    if (jobId != null)
                    {
                        _syncJobService.UpdateJobProgress(jobId, job =>
                        {
                            job.ProcessedEmails = totalProcessed;
                            job.NewEmails = totalNew;
                            job.FailedEmails = totalFailed;
                        });
                    }

                    _logger.LogInformation(
                        "Download-all progress for {AccountName}: {Processed} processed, {New} newly archived, {Skipped} skipped so far",
                        account.Name, totalProcessed, totalNew, totalSkipped);

                    pageToken = listResponse.NextPageToken;

                    if (pageToken != null && _batchOptions.PauseBetweenBatchesMs > 0)
                        await Task.Delay(_batchOptions.PauseBetweenBatchesMs);

                } while (pageToken != null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error during download-all sync for account {AccountName}", account.Name);
                totalFailed++;
            }

            if (totalFailed == 0 && !cancellationToken.IsCancellationRequested)
            {
                var freshAccount = await _context.MailAccounts.FindAsync(account.Id);
                if (freshAccount != null)
                {
                    freshAccount.LastSync = syncStartTime;
                    await _context.SaveChangesAsync();
                }
            }

            if (jobId != null)
                _syncJobService.CompleteJob(jobId, totalFailed == 0 && !cancellationToken.IsCancellationRequested);

            _logger.LogInformation(
                "Download-all sync complete for {AccountName}: {Processed} processed, {New} newly archived, {Skipped} skipped (already archived), {Failed} failed",
                account.Name, totalProcessed, totalNew, totalSkipped, totalFailed);
        }

        /// <summary>
        /// Two-phase fetch: first retrieves only the Message-ID header (format=METADATA) for all
        /// message refs and bulk-checks the DB to find which are genuinely new; then downloads
        /// format=RAW only for those. This avoids transferring large email payloads for already-
        /// archived messages. Messages that fail in a batch are retried individually.
        /// Returns (processed, newlyArchived, skipped, failed).
        /// </summary>
        private async Task<(int Processed, int New, int Skipped, int Failed)> FetchAndArchiveBatchAsync(
            GmailService gmailService,
            IList<Message> messageRefs,
            MailAccount account,
            string context,
            CancellationToken cancellationToken)
        {
            var totalProcessed = 0;
            var totalNew = 0;
            var totalSkipped = 0;
            var totalFailed = 0;
            var batchSize = Math.Max(1, Math.Min(_batchOptions.GmailBatchSize, 100));

            // ── Phase 1: lightweight METADATA fetch to get Message-ID headers ──────────
            // Maps gmail internal ID → RFC 2822 Message-ID (stripped of angle brackets).
            // IDs that fail metadata are queued for a direct RAW fetch below.
            var metadataMap = new Dictionary<string, string?>(messageRefs.Count);
            var metadataFailedIds = new List<string>();

            for (int i = 0; i < messageRefs.Count; i += batchSize)
            {
                if (cancellationToken.IsCancellationRequested)
                    return (totalProcessed, totalNew, totalSkipped, totalFailed);

                var slice = messageRefs.Skip(i).Take(batchSize).ToList();
                var batch = new BatchRequest(gmailService);
                foreach (var msgRef in slice)
                {
                    var capturedId = msgRef.Id;
                    var req = gmailService.Users.Messages.Get("me", capturedId);
                    req.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;
                    req.MetadataHeaders = new[] { "Message-ID" };
                    batch.Queue<Message>(req, (content, error, index, _) =>
                    {
                        if (error != null || content == null)
                            metadataFailedIds.Add(capturedId);
                        else
                            metadataMap[capturedId] = ExtractMessageId(content);
                    });
                }
                try
                {
                    await batch.ExecuteAsync(cancellationToken);
                }
                catch (OperationCanceledException) { return (totalProcessed, totalNew, totalSkipped, totalFailed); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Metadata batch failed [{Context}], falling back to RAW for {Count} messages", context, slice.Count);
                    metadataFailedIds.AddRange(slice.Select(m => m.Id));
                }
                if (_batchOptions.GmailMetadataBatchPauseMs > 0 && (i + batchSize) < messageRefs.Count)
                    await Task.Delay(_batchOptions.GmailMetadataBatchPauseMs);
            }

            // ── Phase 2: single bulk DB check ────────────────────────────────────────
            var candidateIds = metadataMap.Values.Where(v => v != null).Select(v => v!).ToList();
            // An email counts as "already seen" by this account if it's the primary owner
            // OR if a junction-table association exists for this account
            var existingIds = candidateIds.Count > 0
                ? (await _context.ArchivedEmails
                    .Where(e => candidateIds.Contains(e.MessageId) &&
                           (e.MailAccountId == account.Id ||
                            e.ArchivedEmailAccounts.Any(a => a.MailAccountId == account.Id)))
                    .Select(e => e.MessageId)
                    .ToListAsync(cancellationToken))
                    .ToHashSet()
                : new HashSet<string>();

            // Classify each message ref: skip (already in DB) or fetch (new or metadata failed)
            var toFetchIds = new List<string>();
            foreach (var (gmailId, msgId) in metadataMap)
            {
                if (msgId != null && existingIds.Contains(msgId))
                {
                    totalSkipped++;
                    totalProcessed++;
                    _logger.LogDebug("Skipped already-archived message {MessageId} [{Context}]", gmailId, context);
                }
                else
                {
                    toFetchIds.Add(gmailId);
                }
            }
            toFetchIds.AddRange(metadataFailedIds);

            if (toFetchIds.Count == 0)
                return (totalProcessed, totalNew, totalSkipped, totalFailed);

            // ── Phase 3: RAW fetch only for new messages ──────────────────────────────
            var fetched = new List<Message>(toFetchIds.Count);
            var retryIds = new List<string>();

            for (int i = 0; i < toFetchIds.Count; i += batchSize)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var slice = toFetchIds.Skip(i).Take(batchSize).ToList();
                var batch = new BatchRequest(gmailService);
                foreach (var msgId in slice)
                {
                    var capturedId = msgId;
                    var req = gmailService.Users.Messages.Get("me", capturedId);
                    req.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;
                    batch.Queue<Message>(req, (content, error, index, _) =>
                    {
                        if (error != null)
                        {
                            _logger.LogWarning("RAW batch fetch error for {MessageId} [{Context}]: {Error} — will retry",
                                capturedId, context, error.Message);
                            retryIds.Add(capturedId);
                        }
                        else if (content?.Raw != null)
                            fetched.Add(content);
                        else
                        {
                            _logger.LogWarning("Message {MessageId} [{Context}] returned empty raw — will retry", capturedId, context);
                            retryIds.Add(capturedId);
                        }
                    });
                }
                try
                {
                    await batch.ExecuteAsync(cancellationToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "RAW batch failed [{Context}], retrying {Count} individually", context, slice.Count);
                    retryIds.AddRange(slice.Where(id => !retryIds.Contains(id)));
                }

                if (_batchOptions.PauseBetweenBatchesMs > 0 && (i + batchSize) < toFetchIds.Count)
                    await Task.Delay(_batchOptions.PauseBetweenBatchesMs);
            }

            // Retry individually any RAW fetches that failed in a batch, using exponential backoff
            if (retryIds.Count > 0)
            {
                _logger.LogInformation("Retrying {Count} messages individually [{Context}]", retryIds.Count, context);
                foreach (var msgId in retryIds)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    var result = await FetchRawWithBackoffAsync(gmailService, msgId, context, cancellationToken);
                    if (result?.Raw != null)
                        fetched.Add(result);
                    else if (result == null)
                        totalFailed++;
                    await Task.Delay(_batchOptions.PauseBetweenEmailsMs);
                }
            }

            // ── Phase 4: archive new messages ─────────────────────────────────────────
            foreach (var fullMessage in fetched)
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    var rawBytes = Convert.FromBase64String(
                        fullMessage.Raw.Replace('-', '+').Replace('_', '/'));

                    MimeMessage mimeMessage;
                    using (var ms = new MemoryStream(rawBytes))
                        mimeMessage = await MimeMessage.LoadAsync(ms);

                    var isOutgoing = fullMessage.LabelIds?.Contains("SENT") ?? false;
                    var folderName = ResolveFolderName(fullMessage.LabelIds);

                    var archived = await _coreService.ArchiveEmailAsync(account, mimeMessage, isOutgoing, folderName);
                    totalProcessed++;
                    if (archived)
                    {
                        totalNew++;
                    }
                    else
                    {
                        // ArchiveEmailAsync found a dup we missed (e.g. null Message-ID fallback key)
                        totalSkipped++;
                        _logger.LogDebug("Skipped already-archived message {MessageId} [{Context}]", fullMessage.Id, context);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to archive message {MessageId} [{Context}]", fullMessage.Id, context);
                    totalFailed++;
                }
            }

            return (totalProcessed, totalNew, totalSkipped, totalFailed);
        }

        /// <summary>
        /// Fetches a single message in RAW format with exponential backoff on quota errors.
        /// Returns the Message on success, null after exhausting retries.
        /// </summary>
        private async Task<Message?> FetchRawWithBackoffAsync(
            GmailService gmailService,
            string messageId,
            string context,
            CancellationToken cancellationToken)
        {
            const int maxAttempts = 5;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    var req = gmailService.Users.Messages.Get("me", messageId);
                    req.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;
                    return await req.ExecuteAsync(cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (IsQuotaExceededException(ex) && attempt < maxAttempts - 1)
                {
                    // Back off exponentially: 5s, 10s, 20s, 40s — enough to outlast the per-minute window
                    var delayMs = (int)(5000 * Math.Pow(2, attempt)) + Random.Shared.Next(0, 2000);
                    _logger.LogWarning(
                        "Quota exceeded for {MessageId} [{Context}], attempt {Attempt}/{Max} — waiting {DelayMs}ms",
                        messageId, context, attempt + 1, maxAttempts, delayMs);
                    await Task.Delay(delayMs, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Retry failed for message {MessageId} [{Context}]", messageId, context);
                    return null;
                }
            }
            _logger.LogError("Exhausted {Max} retries for message {MessageId} [{Context}]", maxAttempts, messageId, context);
            return null;
        }

        private static bool IsQuotaExceededException(Exception ex) =>
            ex is Google.GoogleApiException gex &&
            gex.Error?.Errors?.Any(e =>
                e.Reason is "rateLimitExceeded" or "userRateLimitExceeded") == true;

        /// <summary>
        /// Extracts the RFC 2822 Message-ID from a Gmail metadata response,
        /// stripping angle brackets to match how MimeKit stores it in the DB.
        /// </summary>
        private static string? ExtractMessageId(Message gmailMsg)
        {
            var raw = gmailMsg.Payload?.Headers?
                .FirstOrDefault(h => string.Equals(h.Name, "Message-ID", StringComparison.OrdinalIgnoreCase))
                ?.Value;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return raw.Trim().TrimStart('<').TrimEnd('>').Trim();
        }

        private static string ResolveFolderName(IList<string>? labelIds)
        {
            if (labelIds == null || labelIds.Count == 0)
                return "INBOX";

            if (labelIds.Contains("SENT")) return "SENT";
            if (labelIds.Contains("INBOX")) return "INBOX";
            if (labelIds.Contains("DRAFT")) return "DRAFT";
            if (labelIds.Contains("SPAM")) return "SPAM";
            if (labelIds.Contains("TRASH")) return "TRASH";

            return labelIds.FirstOrDefault(l => l.StartsWith("Label_")) ?? labelIds[0];
        }

        public async Task<bool> TestConnectionAsync(MailAccount account)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var gmailService = await CreateGmailServiceAsync(account, cts.Token);
                var profile = await gmailService.Users.GetProfile("me").ExecuteAsync();
                _logger.LogInformation("Gmail connection test successful for {EmailAddress}", profile.EmailAddress);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gmail connection test failed for account {AccountName}", account.Name);
                return false;
            }
        }

        public Task<List<string>> GetMailFoldersAsync(int accountId)
        {
            // Gmail uses labels rather than folders; folder exclusion is not applicable
            return Task.FromResult(new List<string>());
        }

        public Task<bool> RestoreEmailToFolderAsync(int emailId, int targetAccountId, string folderName)
        {
            _logger.LogWarning("Restore is not supported for Gmail accounts (configured read-only)");
            return Task.FromResult(false);
        }

        public Task<(int Successful, int Failed)> RestoreMultipleEmailsWithProgressAsync(
            List<int> emailIds,
            int targetAccountId,
            string folderName,
            Action<int, int, int> progressCallback,
            CancellationToken cancellationToken = default)
        {
            _logger.LogWarning("Restore is not supported for Gmail accounts (configured read-only)");
            return Task.FromResult((0, emailIds.Count));
        }

        public async Task<bool> ResyncAccountAsync(int accountId)
        {
            try
            {
                var filters = await _context.GmailSenderFilters
                    .Where(f => f.MailAccountId == accountId)
                    .ToListAsync();

                foreach (var filter in filters)
                    filter.LastSync = null;

                await _context.SaveChangesAsync();
                _logger.LogInformation("Resync: cleared LastSync for {Count} sender filters on account {AccountId}",
                    filters.Count, accountId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting sync state for Gmail account {AccountId}", accountId);
                return false;
            }
        }

        public async Task<int> GetEmailCountByAccountAsync(int accountId)
        {
            return await _context.ArchivedEmails.CountAsync(e => e.MailAccountId == accountId);
        }
    }
}
