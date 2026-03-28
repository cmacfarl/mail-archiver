using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
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

        public async Task SyncMailAccountAsync(MailAccount account, string? jobId = null)
        {
            _logger.LogInformation("Starting Gmail sync for account: {AccountName}", account.Name);

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

            var totalProcessed = 0;
            var totalNew = 0;
            var totalFailed = 0;

            foreach (var filter in senderFilters)
            {
                var filterLabel = $"{filter.FilterType}:{filter.EmailAddress}";
                _logger.LogInformation("Syncing messages for filter {Filter} on account {AccountName}",
                    filterLabel, account.Name);

                if (jobId != null)
                {
                    _syncJobService.UpdateJobProgress(jobId, job =>
                    {
                        job.CurrentFolder = filterLabel;
                    });
                }

                try
                {
                    var filterStartTime = DateTime.UtcNow;

                    var queryPrefix = filter.FilterType == GmailFilterType.Recipient ? "to" : "from";
                    var query = $"{queryPrefix}:{filter.EmailAddress}";
                    if (filter.LastSync.HasValue)
                    {
                        var unixTimestamp = ((DateTimeOffset)filter.LastSync.Value).ToUnixTimeSeconds();
                        query += $" after:{unixTimestamp}";
                    }

                    string? pageToken = null;
                    do
                    {
                        var listRequest = gmailService.Users.Messages.List("me");
                        listRequest.Q = query;
                        listRequest.MaxResults = _batchOptions.BatchSize;
                        if (pageToken != null)
                            listRequest.PageToken = pageToken;

                        ListMessagesResponse listResponse;
                        try
                        {
                            listResponse = await listRequest.ExecuteAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to list messages for filter {Filter}", filterLabel);
                            totalFailed++;
                            break;
                        }

                        if (listResponse.Messages == null || listResponse.Messages.Count == 0)
                            break;

                        foreach (var msgRef in listResponse.Messages)
                        {
                            try
                            {
                                var getRequest = gmailService.Users.Messages.Get("me", msgRef.Id);
                                getRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;
                                var fullMessage = await getRequest.ExecuteAsync();

                                // base64url → bytes → MimeMessage (MimeKit handles all MIME parsing)
                                var rawBytes = Convert.FromBase64String(
                                    fullMessage.Raw.Replace('-', '+').Replace('_', '/'));

                                MimeMessage mimeMessage;
                                using (var ms = new MemoryStream(rawBytes))
                                    mimeMessage = await MimeMessage.LoadAsync(ms);

                                var isOutgoing = fullMessage.LabelIds?.Contains("SENT") ?? false;
                                var folderName = ResolveFolderName(fullMessage.LabelIds);

                                var archived = await _coreService.ArchiveEmailAsync(account, mimeMessage, isOutgoing, folderName);
                                totalProcessed++;
                                if (archived) totalNew++;

                                if (_batchOptions.PauseBetweenEmailsMs > 0)
                                    await Task.Delay(_batchOptions.PauseBetweenEmailsMs);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to archive Gmail message {MessageId} for filter {Filter}",
                                    msgRef.Id, filterLabel);
                                totalFailed++;
                            }
                        }

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

            if (jobId != null)
                _syncJobService.CompleteJob(jobId, totalFailed == 0);

            _logger.LogInformation("Gmail sync complete for {AccountName}: {Processed} processed, {New} new, {Failed} failed",
                account.Name, totalProcessed, totalNew, totalFailed);
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

            // First user-defined label
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
