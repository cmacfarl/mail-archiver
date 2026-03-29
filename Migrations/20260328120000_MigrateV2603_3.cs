using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <inheritdoc />
    public partial class MigrateV2603_3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'mail_archiver'
                        AND table_name = 'MailAccounts'
                        AND column_name = 'GmailDownloadAll'
                    ) THEN
                        ALTER TABLE mail_archiver.""MailAccounts""
                            ADD COLUMN ""GmailDownloadAll"" boolean NOT NULL DEFAULT false;
                    END IF;
                END $$;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE mail_archiver.""MailAccounts""
                    DROP COLUMN IF EXISTS ""GmailDownloadAll"";
            ");
        }
    }
}
