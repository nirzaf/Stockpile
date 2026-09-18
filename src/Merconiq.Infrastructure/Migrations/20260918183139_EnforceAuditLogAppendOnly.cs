using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Merconiq.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnforceAuditLogAppendOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION reject_audit_log_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'Audit logs are append-only.'
                        USING ERRCODE = '55000';
                END;
                $$;

                CREATE TRIGGER TR_AuditLogs_AppendOnly
                    BEFORE UPDATE OR DELETE ON "AuditLogs"
                    FOR EACH ROW
                    EXECUTE FUNCTION reject_audit_log_mutation();

                CREATE TRIGGER TR_AuditLogs_NoTruncate
                    BEFORE TRUNCATE ON "AuditLogs"
                    FOR EACH STATEMENT
                    EXECUTE FUNCTION reject_audit_log_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS TR_AuditLogs_NoTruncate ON "AuditLogs";
                DROP TRIGGER IF EXISTS TR_AuditLogs_AppendOnly ON "AuditLogs";
                DROP FUNCTION IF EXISTS reject_audit_log_mutation();
                """);
        }
    }
}
