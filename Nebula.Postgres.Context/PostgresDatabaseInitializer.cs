using Microsoft.EntityFrameworkCore;

namespace Nebula.Postgres.Context;

public static class PostgresDatabaseInitializer
{
    private const string LegacyDatabaseBaselineSql = """
        DO $$
        DECLARE
            column_mapping record;
        BEGIN
            IF to_regclass('public."__EFMigrationsHistory"') IS NULL
               AND to_regclass('public.requests') IS NOT NULL
               AND to_regclass('public.commands') IS NOT NULL
               AND to_regclass('public.command_verifications') IS NOT NULL
               AND to_regclass('public.conversation_messages') IS NOT NULL
               AND to_regclass('public.conversation_states') IS NOT NULL THEN

                DROP TRIGGER IF EXISTS update_requests_updated_at ON requests;
                DROP TRIGGER IF EXISTS update_commands_updated_at ON commands;
                DROP TRIGGER IF EXISTS update_conversation_states_updated_at
                    ON conversation_states;
                DROP FUNCTION IF EXISTS update_updated_at_column();

                FOR column_mapping IN
                    SELECT *
                    FROM (VALUES
                        ('requests', 'id', 'Id'),
                        ('requests', 'prompt', 'Prompt'),
                        ('requests', 'classification', 'Classification'),
                        ('requests', 'response', 'Response'),
                        ('requests', 'created_at', 'CreatedAt'),
                        ('requests', 'updated_at', 'UpdatedAt'),
                        ('commands', 'id', 'Id'),
                        ('commands', 'request_id', 'RequestId'),
                        ('commands', 'command_id', 'CommandId'),
                        ('commands', 'objective', 'Objective'),
                        ('commands', 'command', 'Command'),
                        ('commands', 'os_type', 'OsType'),
                        ('commands', 'executed', 'Executed'),
                        ('commands', 'execution_result', 'ExecutionResult'),
                        ('commands', 'created_at', 'CreatedAt'),
                        ('commands', 'updated_at', 'UpdatedAt'),
                        ('command_verifications', 'id', 'Id'),
                        ('command_verifications', 'command_id', 'CommandId'),
                        ('command_verifications', 'is_correct', 'IsCorrect'),
                        ('command_verifications', 'is_safe', 'IsSafe'),
                        ('command_verifications', 'verification_notes', 'VerificationNotes'),
                        ('command_verifications', 'created_at', 'CreatedAt'),
                        ('conversation_messages', 'id', 'Id'),
                        ('conversation_messages', 'conversation_id', 'ConversationId'),
                        ('conversation_messages', 'role', 'Role'),
                        ('conversation_messages', 'content', 'Content'),
                        ('conversation_messages', 'created_at', 'CreatedAt'),
                        ('conversation_states', 'conversation_id', 'ConversationId'),
                        ('conversation_states', 'summary', 'Summary'),
                        ('conversation_states', 'current_goal', 'CurrentGoal'),
                        ('conversation_states', 'current_plan', 'CurrentPlan'),
                        ('conversation_states', 'updated_at', 'UpdatedAt')
                    ) AS mappings(table_name, old_name, new_name)
                LOOP
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = column_mapping.table_name
                          AND column_name = column_mapping.old_name
                    ) THEN
                        EXECUTE format(
                            'ALTER TABLE %I RENAME COLUMN %I TO %I',
                            column_mapping.table_name,
                            column_mapping.old_name,
                            column_mapping.new_name);
                    END IF;
                END LOOP;

                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "PK___EFMigrationsHistory"
                        PRIMARY KEY ("MigrationId")
                );

                INSERT INTO "__EFMigrationsHistory" (
                    "MigrationId",
                    "ProductVersion")
                VALUES
                    ('20260401203428_initial-migration', '10.0.5'),
                    ('20260402112927_add_request_response', '10.0.5'),
                    ('20260527235524_add_conversation_memory', '10.0.8');
            END IF;

            ALTER TABLE IF EXISTS requests
                DROP CONSTRAINT IF EXISTS requests_classification_check;
            ALTER TABLE IF EXISTS commands
                DROP CONSTRAINT IF EXISTS commands_os_type_check;
            ALTER TABLE IF EXISTS conversation_messages
                DROP CONSTRAINT IF EXISTS conversation_messages_role_check;
        END
        $$;
        """;

    public static async Task InitializeAsync(
        PostgresContext context,
        CancellationToken cancellationToken = default)
    {
        await context.Database.ExecuteSqlRawAsync(
            LegacyDatabaseBaselineSql,
            cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
    }

    public static void Initialize(PostgresContext context)
    {
        context.Database.ExecuteSqlRaw(LegacyDatabaseBaselineSql);
        context.Database.Migrate();
    }
}
