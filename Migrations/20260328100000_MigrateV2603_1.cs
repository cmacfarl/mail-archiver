using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MailArchiver.Migrations
{
    /// <inheritdoc />
    public partial class MigrateV2603_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add GmailCredentialsFile column to MailAccounts
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'mail_archiver'
                        AND table_name = 'MailAccounts'
                        AND column_name = 'GmailCredentialsFile'
                    ) THEN
                        ALTER TABLE mail_archiver.""MailAccounts"" ADD COLUMN ""GmailCredentialsFile"" text;
                    END IF;
                END $$;
            ");

            // Add GmailTokenStoreName column to MailAccounts
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'mail_archiver'
                        AND table_name = 'MailAccounts'
                        AND column_name = 'GmailTokenStoreName'
                    ) THEN
                        ALTER TABLE mail_archiver.""MailAccounts"" ADD COLUMN ""GmailTokenStoreName"" text;
                    END IF;
                END $$;
            ");

            // Create GmailSenderFilters table
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.tables
                        WHERE table_schema = 'mail_archiver'
                        AND table_name = 'GmailSenderFilters'
                    ) THEN
                        CREATE TABLE mail_archiver.""GmailSenderFilters"" (
                            ""Id"" serial NOT NULL,
                            ""MailAccountId"" integer NOT NULL,
                            ""SenderEmail"" text NOT NULL,
                            ""LastSync"" timestamp without time zone,
                            ""IsEnabled"" boolean NOT NULL DEFAULT true,
                            CONSTRAINT ""PK_GmailSenderFilters"" PRIMARY KEY (""Id""),
                            CONSTRAINT ""FK_GmailSenderFilters_MailAccounts"" FOREIGN KEY (""MailAccountId"")
                                REFERENCES mail_archiver.""MailAccounts"" (""Id"") ON DELETE CASCADE
                        );

                        CREATE UNIQUE INDEX ""IX_GmailSenderFilters_AccountId_SenderEmail""
                            ON mail_archiver.""GmailSenderFilters"" (""MailAccountId"", ""SenderEmail"");
                    END IF;
                END $$;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS mail_archiver.""GmailSenderFilters"";");

            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'mail_archiver'
                        AND table_name = 'MailAccounts'
                        AND column_name = 'GmailTokenStoreName'
                    ) THEN
                        ALTER TABLE mail_archiver.""MailAccounts"" DROP COLUMN ""GmailTokenStoreName"";
                    END IF;
                END $$;
            ");

            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'mail_archiver'
                        AND table_name = 'MailAccounts'
                        AND column_name = 'GmailCredentialsFile'
                    ) THEN
                        ALTER TABLE mail_archiver.""MailAccounts"" DROP COLUMN ""GmailCredentialsFile"";
                    END IF;
                END $$;
            ");
        }
    }
}
