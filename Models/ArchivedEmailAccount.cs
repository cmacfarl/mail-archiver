namespace MailArchiver.Models
{
    /// <summary>
    /// Junction table associating an archived email with every mail account that
    /// "owns" it — including the primary account that originally downloaded it and
    /// any secondary accounts whose filters would also have matched it.
    /// </summary>
    public class ArchivedEmailAccount
    {
        public int Id { get; set; }
        public int ArchivedEmailId { get; set; }
        public int MailAccountId { get; set; }

        public virtual ArchivedEmail ArchivedEmail { get; set; } = null!;
        public virtual MailAccount MailAccount { get; set; } = null!;
    }
}
