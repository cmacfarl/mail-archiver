namespace MailArchiver.Models
{
    public class GmailSenderFilter
    {
        public int Id { get; set; }
        public int MailAccountId { get; set; }
        public string EmailAddress { get; set; } = string.Empty;
        public GmailFilterType FilterType { get; set; } = GmailFilterType.Sender;
        public DateTime? LastSync { get; set; }
        public bool IsEnabled { get; set; } = true;

        public virtual MailAccount MailAccount { get; set; } = null!;
    }
}
