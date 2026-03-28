using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <inheritdoc />
    public partial class MigrateV2603_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rename SenderEmail -> EmailAddress
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'mail_archiver'
                        AND table_name = 'GmailSenderFilters'
                        AND column_name = 'SenderEmail'
                    ) THEN
                        ALTER TABLE mail_archiver.""GmailSenderFilters""
                            RENAME COLUMN ""SenderEmail"" TO ""EmailAddress"";
                    END IF;
                END $$;
            ");

            // Drop old unique index (name may vary)
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS mail_archiver.""IX_GmailSenderFilters_AccountId_SenderEmail"";
            ");

            // Add FilterType column (default 'Sender' preserves all existing rows)
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'mail_archiver'
                        AND table_name = 'GmailSenderFilters'
                        AND column_name = 'FilterType'
                    ) THEN
                        ALTER TABLE mail_archiver.""GmailSenderFilters""
                            ADD COLUMN ""FilterType"" varchar(20) NOT NULL DEFAULT 'Sender';
                    END IF;
                END $$;
            ");

            // Create new unique index on (MailAccountId, EmailAddress, FilterType)
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_GmailSenderFilters_AccountId_Email_Type""
                    ON mail_archiver.""GmailSenderFilters"" (""MailAccountId"", ""EmailAddress"", ""FilterType"");
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS mail_archiver.""IX_GmailSenderFilters_AccountId_Email_Type"";
            ");

            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'mail_archiver'
                        AND table_name = 'GmailSenderFilters'
                        AND column_name = 'FilterType'
                    ) THEN
                        ALTER TABLE mail_archiver.""GmailSenderFilters""
                            DROP COLUMN ""FilterType"";
                    END IF;
                END $$;
            ");

            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'mail_archiver'
                        AND table_name = 'GmailSenderFilters'
                        AND column_name = 'EmailAddress'
                    ) THEN
                        ALTER TABLE mail_archiver.""GmailSenderFilters""
                            RENAME COLUMN ""EmailAddress"" TO ""SenderEmail"";
                    END IF;
                END $$;
            ");

            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_GmailSenderFilters_AccountId_SenderEmail""
                    ON mail_archiver.""GmailSenderFilters"" (""MailAccountId"", ""SenderEmail"");
            ");
        }
    }
}
