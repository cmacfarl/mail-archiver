using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <inheritdoc />
    public partial class MigrateV2603_4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS mail_archiver.""ArchivedEmailAccounts"" (
                    ""Id""               serial       NOT NULL,
                    ""ArchivedEmailId""  integer      NOT NULL,
                    ""MailAccountId""    integer      NOT NULL,
                    CONSTRAINT ""PK_ArchivedEmailAccounts"" PRIMARY KEY (""Id""),
                    CONSTRAINT ""FK_ArchivedEmailAccounts_ArchivedEmails""
                        FOREIGN KEY (""ArchivedEmailId"")
                        REFERENCES mail_archiver.""ArchivedEmails""(""Id"") ON DELETE CASCADE,
                    CONSTRAINT ""FK_ArchivedEmailAccounts_MailAccounts""
                        FOREIGN KEY (""MailAccountId"")
                        REFERENCES mail_archiver.""MailAccounts""(""Id"") ON DELETE CASCADE
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ArchivedEmailAccounts_EmailId_AccountId""
                    ON mail_archiver.""ArchivedEmailAccounts""(""ArchivedEmailId"", ""MailAccountId"");

                -- Backfill: every existing email gets a primary association row
                INSERT INTO mail_archiver.""ArchivedEmailAccounts""(""ArchivedEmailId"", ""MailAccountId"")
                SELECT ""Id"", ""MailAccountId""
                FROM   mail_archiver.""ArchivedEmails""
                ON CONFLICT DO NOTHING;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TABLE IF EXISTS mail_archiver.""ArchivedEmailAccounts"";
            ");
        }
    }
}
