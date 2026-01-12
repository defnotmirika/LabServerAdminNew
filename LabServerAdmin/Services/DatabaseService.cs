using Microsoft.Extensions.Configuration;
using Npgsql;
using LabServerAdmin.Models;
using System;
using System.Data;
using System.Text.RegularExpressions;

namespace LabServerAdmin.Services
{
    public class DatabaseService
    {
        private readonly string _connectionString;
        private readonly IConfiguration _configuration;

        public DatabaseService(IConfiguration configuration)
        {
            _configuration = configuration;

            var supabaseConnection = Environment.GetEnvironmentVariable("SUPABASE_DB_CONNECTION")
                ?? _configuration["Supabase:ConnectionString"];

            var defaultConnection = _configuration.GetConnectionString("DefaultConnection")
                ?? "Host=localhost;Database=labserver;Username=postgres;Password=abc123";

            _connectionString = !string.IsNullOrWhiteSpace(supabaseConnection)
                ? supabaseConnection
                : defaultConnection;
        }

        public async Task InitializeDatabaseAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Note: Tables should already exist from database_setup.sql
            // This method now only ensures default users exist and creates connected_clients if needed
            
            // Create connected_clients table if it doesn't exist (for backward compatibility)
            var createConnectedClients = @"
                CREATE TABLE IF NOT EXISTS connected_clients (
                    id SERIAL PRIMARY KEY,
                    name VARCHAR(100) NOT NULL UNIQUE,
                    ip_address VARCHAR(45) NOT NULL,
                    last_response TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    is_connected BOOLEAN DEFAULT TRUE,
                    status VARCHAR(50) DEFAULT 'Online'
                );
            ";

            using var command = new NpgsqlCommand(createConnectedClients, connection);
            await command.ExecuteNonQueryAsync();

            // Ensure UNIQUE constraint exists on connected_clients.name
            var ensureUniqueConstraint = @"
                DO $$ 
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint 
                        WHERE conname = 'connected_clients_name_key'
                    ) THEN
                        ALTER TABLE connected_clients ADD CONSTRAINT connected_clients_name_key UNIQUE (name);
                    END IF;
                END $$;
            ";
            using var constraintCommand = new NpgsqlCommand(ensureUniqueConstraint, connection);
            await constraintCommand.ExecuteNonQueryAsync();

            // Migrate attendance_logs table if it has old schema
            await MigrateAttendanceLogsTableAsync(connection);

            // Migrate system_logs table if it has old schema
            await MigrateSystemLogsTableAsync(connection);

            // Create default admin user if none exists
            await CreateDefaultAdminAsync(connection);
            
            // Create default student/client user if none exists
            await CreateDefaultStudentAsync(connection);
        }

        private async Task CreateDefaultAdminAsync(NpgsqlConnection connection)
        {
            var getAdmin = "SELECT password FROM users WHERE username = 'admin' AND role = 'Admin' LIMIT 1";
            using var getCommand = new NpgsqlCommand(getAdmin, connection);
            var existingHashObj = await getCommand.ExecuteScalarAsync();

            if (existingHashObj == null)
            {
                await InsertDefaultAdminAsync(connection);
                return;
            }

            var existingHash = existingHashObj?.ToString();
            if (!IsValidBcryptHash(existingHash))
            {
                await UpdateAdminPasswordAsync(connection);
            }
        }

        private async Task CreateDefaultStudentAsync(NpgsqlConnection connection)
        {
            var getStudent = "SELECT password FROM users WHERE username = 'student' AND role = 'Student' LIMIT 1";
            using var getCommand = new NpgsqlCommand(getStudent, connection);
            var existingHashObj = await getCommand.ExecuteScalarAsync();

            if (existingHashObj == null)
            {
                await InsertDefaultStudentAsync(connection);
                return;
            }

            var existingHash = existingHashObj?.ToString();
            if (!IsValidBcryptHash(existingHash))
            {
                await UpdateStudentPasswordAsync(connection);
            }
        }

        private static async Task InsertDefaultAdminAsync(NpgsqlConnection connection)
        {
            var insertAdmin = @"
                INSERT INTO users (username, password, role, full_name, email) 
                VALUES ('admin', @passwordHash, 'Admin', 'System Administrator', 'admin@labserver.local')
                ON CONFLICT (username) DO NOTHING
            ";

            var hashedPassword = BCrypt.Net.BCrypt.HashPassword("admin123");

            using var insertCommand = new NpgsqlCommand(insertAdmin, connection);
            insertCommand.Parameters.AddWithValue("@passwordHash", hashedPassword);
            await insertCommand.ExecuteNonQueryAsync();
        }

        private static async Task UpdateAdminPasswordAsync(NpgsqlConnection connection)
        {
            var updateAdmin = @"
                UPDATE users 
                SET password = @passwordHash 
                WHERE username = 'admin' AND role = 'Admin'
            ";

            var hashedPassword = BCrypt.Net.BCrypt.HashPassword("admin123");

            using var updateCommand = new NpgsqlCommand(updateAdmin, connection);
            updateCommand.Parameters.AddWithValue("@passwordHash", hashedPassword);
            await updateCommand.ExecuteNonQueryAsync();
        }

        private static async Task InsertDefaultStudentAsync(NpgsqlConnection connection)
        {
            var insertStudent = @"
                INSERT INTO users (username, password, role, full_name, email) 
                VALUES ('student', @passwordHash, 'Student', 'Default Student', 'student@labserver.local')
                ON CONFLICT (username) DO NOTHING
            ";

            var hashedPassword = BCrypt.Net.BCrypt.HashPassword("student123");

            using var insertCommand = new NpgsqlCommand(insertStudent, connection);
            insertCommand.Parameters.AddWithValue("@passwordHash", hashedPassword);
            await insertCommand.ExecuteNonQueryAsync();
        }

        private static async Task UpdateStudentPasswordAsync(NpgsqlConnection connection)
        {
            var updateStudent = @"
                UPDATE users 
                SET password = @passwordHash 
                WHERE username = 'student' AND role = 'Student'
            ";

            var hashedPassword = BCrypt.Net.BCrypt.HashPassword("student123");

            using var updateCommand = new NpgsqlCommand(updateStudent, connection);
            updateCommand.Parameters.AddWithValue("@passwordHash", hashedPassword);
            await updateCommand.ExecuteNonQueryAsync();
        }

        private static bool IsValidBcryptHash(string? hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
            {
                return false;
            }

            return Regex.IsMatch(hash, @"^\$2[aby]\$\d{2}\$[./A-Za-z0-9]{53}$");
        }

        private async Task MigrateAttendanceLogsTableAsync(NpgsqlConnection connection)
        {
            // Check if attendance_logs table exists
            var checkTableExists = @"
                SELECT EXISTS (
                    SELECT FROM information_schema.tables 
                    WHERE table_schema = 'public' 
                    AND table_name = 'attendance_logs'
                );
            ";
            using var checkTableCmd = new NpgsqlCommand(checkTableExists, connection);
            var tableExists = (bool)(await checkTableCmd.ExecuteScalarAsync() ?? false);

            if (!tableExists)
            {
                // Create table with new schema
                var createTable = @"
                    CREATE TABLE attendance_logs (
                        id SERIAL PRIMARY KEY,
                        user_id INTEGER REFERENCES users(id),
                        computer_id INTEGER REFERENCES computers(id),
                        login_time TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                        logout_time TIMESTAMP,
                        session_duration INTERVAL,
                        status VARCHAR(20) DEFAULT 'Active'
                    );
                ";
                using var createCmd = new NpgsqlCommand(createTable, connection);
                await createCmd.ExecuteNonQueryAsync();
                return;
            }

            // Check if user_id column exists
            var checkColumnExists = @"
                SELECT EXISTS (
                    SELECT FROM information_schema.columns 
                    WHERE table_schema = 'public' 
                    AND table_name = 'attendance_logs' 
                    AND column_name = 'user_id'
                );
            ";
            using var checkColumnCmd = new NpgsqlCommand(checkColumnExists, connection);
            var columnExists = (bool)(await checkColumnCmd.ExecuteScalarAsync() ?? false);

            if (!columnExists)
            {
                // Check if old columns exist
                var checkOldColumns = @"
                    SELECT EXISTS (
                        SELECT FROM information_schema.columns 
                        WHERE table_schema = 'public' 
                        AND table_name = 'attendance_logs' 
                        AND column_name = 'student_name'
                    );
                ";
                using var checkOldCmd = new NpgsqlCommand(checkOldColumns, connection);
                var hasOldColumns = (bool)(await checkOldCmd.ExecuteScalarAsync() ?? false);

                // Add new columns
                var addColumns = @"
                    ALTER TABLE attendance_logs
                    ADD COLUMN IF NOT EXISTS user_id INTEGER REFERENCES users(id),
                    ADD COLUMN IF NOT EXISTS computer_id INTEGER REFERENCES computers(id),
                    ADD COLUMN IF NOT EXISTS login_time TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    ADD COLUMN IF NOT EXISTS logout_time TIMESTAMP,
                    ADD COLUMN IF NOT EXISTS session_duration INTERVAL,
                    ADD COLUMN IF NOT EXISTS status VARCHAR(20) DEFAULT 'Active';
                ";
                using var addColumnsCmd = new NpgsqlCommand(addColumns, connection);
                await addColumnsCmd.ExecuteNonQueryAsync();

                // If old columns exist, try to migrate data
                if (hasOldColumns)
                {
                    // Check if time_in column exists before trying to use it
                    var checkTimeIn = @"
                        SELECT EXISTS (
                            SELECT FROM information_schema.columns 
                            WHERE table_schema = 'public' 
                            AND table_name = 'attendance_logs' 
                            AND column_name = 'time_in'
                        );
                    ";
                    using var checkTimeInCmd = new NpgsqlCommand(checkTimeIn, connection);
                    var hasTimeIn = (bool)(await checkTimeInCmd.ExecuteScalarAsync() ?? false);

                    if (hasTimeIn)
                    {
                        var migrateData = @"
                            UPDATE attendance_logs
                            SET 
                                login_time = COALESCE(time_in, CURRENT_TIMESTAMP),
                                logout_time = time_out,
                                status = CASE 
                                    WHEN is_active = TRUE THEN 'Active' 
                                    ELSE 'Completed' 
                                END
                            WHERE login_time IS NULL OR (time_in IS NOT NULL AND login_time = CURRENT_TIMESTAMP);
                        ";
                        using var migrateCmd = new NpgsqlCommand(migrateData, connection);
                        await migrateCmd.ExecuteNonQueryAsync();
                    }

                    // Drop old columns after migration (safe with IF EXISTS)
                    var dropOldColumns = @"
                        ALTER TABLE attendance_logs
                        DROP COLUMN IF EXISTS student_name,
                        DROP COLUMN IF EXISTS pc_name,
                        DROP COLUMN IF EXISTS time_in,
                        DROP COLUMN IF EXISTS time_out,
                        DROP COLUMN IF EXISTS is_active;
                    ";
                    using var dropCmd = new NpgsqlCommand(dropOldColumns, connection);
                    await dropCmd.ExecuteNonQueryAsync();
                }
            }

            // Ensure indexes exist
            var createIndexes = @"
                CREATE INDEX IF NOT EXISTS idx_attendance_logs_user ON attendance_logs(user_id);
                CREATE INDEX IF NOT EXISTS idx_attendance_logs_time ON attendance_logs(login_time);
            ";
            using var indexCmd = new NpgsqlCommand(createIndexes, connection);
            await indexCmd.ExecuteNonQueryAsync();
        }

        private async Task MigrateSystemLogsTableAsync(NpgsqlConnection connection)
        {
            // Check if system_logs table exists
            var checkTableExists = @"
                SELECT EXISTS (
                    SELECT FROM information_schema.tables 
                    WHERE table_schema = 'public' 
                    AND table_name = 'system_logs'
                );
            ";
            using var checkTableCmd = new NpgsqlCommand(checkTableExists, connection);
            var tableExists = (bool)(await checkTableCmd.ExecuteScalarAsync() ?? false);

            if (!tableExists)
            {
                // Create table with new schema
                var createTable = @"
                    CREATE TABLE system_logs (
                        id SERIAL PRIMARY KEY,
                        log_level VARCHAR(10) NOT NULL,
                        log_message TEXT NOT NULL,
                        log_time TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                        computer_id INTEGER REFERENCES computers(id),
                        user_id INTEGER REFERENCES users(id),
                        action_type VARCHAR(50)
                    );
                ";
                using var createCmd = new NpgsqlCommand(createTable, connection);
                await createCmd.ExecuteNonQueryAsync();
                return;
            }

            // Check if log_level column exists
            var checkColumnExists = @"
                SELECT EXISTS (
                    SELECT FROM information_schema.columns 
                    WHERE table_schema = 'public' 
                    AND table_name = 'system_logs' 
                    AND column_name = 'log_level'
                );
            ";
            using var checkColumnCmd = new NpgsqlCommand(checkColumnExists, connection);
            var columnExists = (bool)(await checkColumnCmd.ExecuteScalarAsync() ?? false);

            if (!columnExists)
            {
                // Check if old columns exist
                var checkOldColumns = @"
                    SELECT EXISTS (
                        SELECT FROM information_schema.columns 
                        WHERE table_schema = 'public' 
                        AND table_name = 'system_logs' 
                        AND column_name = 'timestamp'
                    );
                ";
                using var checkOldCmd = new NpgsqlCommand(checkOldColumns, connection);
                var hasOldColumns = (bool)(await checkOldCmd.ExecuteScalarAsync() ?? false);

                // Add new columns
                var addColumns = @"
                    ALTER TABLE system_logs
                    ADD COLUMN IF NOT EXISTS log_level VARCHAR(10) DEFAULT 'INFO',
                    ADD COLUMN IF NOT EXISTS log_message TEXT,
                    ADD COLUMN IF NOT EXISTS log_time TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    ADD COLUMN IF NOT EXISTS computer_id INTEGER REFERENCES computers(id),
                    ADD COLUMN IF NOT EXISTS user_id INTEGER REFERENCES users(id),
                    ADD COLUMN IF NOT EXISTS action_type VARCHAR(50);
                ";
                using var addColumnsCmd = new NpgsqlCommand(addColumns, connection);
                await addColumnsCmd.ExecuteNonQueryAsync();

                // If old columns exist, try to migrate data
                if (hasOldColumns)
                {
                    // Check if timestamp column exists before trying to use it
                    var checkTimestamp = @"
                        SELECT EXISTS (
                            SELECT FROM information_schema.columns 
                            WHERE table_schema = 'public' 
                            AND table_name = 'system_logs' 
                            AND column_name = 'timestamp'
                        );
                    ";
                    using var checkTimestampCmd = new NpgsqlCommand(checkTimestamp, connection);
                    var hasTimestamp = (bool)(await checkTimestampCmd.ExecuteScalarAsync() ?? false);

                    if (hasTimestamp)
                    {
                        var migrateData = @"
                            UPDATE system_logs
                            SET 
                                log_time = COALESCE(timestamp, CURRENT_TIMESTAMP),
                                log_message = COALESCE(details, action, ''),
                                action_type = action,
                                log_level = CASE 
                                    WHEN UPPER(status) = 'ERROR' OR UPPER(status) = 'FAILED' THEN 'ERROR'
                                    WHEN UPPER(status) = 'WARNING' THEN 'WARNING'
                                    WHEN UPPER(status) = 'DEBUG' THEN 'DEBUG'
                                    ELSE 'INFO'
                                END
                            WHERE log_time IS NULL OR (timestamp IS NOT NULL AND log_time = CURRENT_TIMESTAMP);
                        ";
                        using var migrateCmd = new NpgsqlCommand(migrateData, connection);
                        await migrateCmd.ExecuteNonQueryAsync();

                        // Try to map client_name to computer_id if possible
                        var checkClientName = @"
                            SELECT EXISTS (
                                SELECT FROM information_schema.columns 
                                WHERE table_schema = 'public' 
                                AND table_name = 'system_logs' 
                                AND column_name = 'client_name'
                            );
                        ";
                        using var checkClientNameCmd = new NpgsqlCommand(checkClientName, connection);
                        var hasClientName = (bool)(await checkClientNameCmd.ExecuteScalarAsync() ?? false);

                        if (hasClientName)
                        {
                            var mapClientName = @"
                                UPDATE system_logs sl
                                SET computer_id = c.id
                                FROM computers c
                                WHERE sl.computer_id IS NULL 
                                AND c.client_name = sl.client_name;
                            ";
                            using var mapCmd = new NpgsqlCommand(mapClientName, connection);
                            await mapCmd.ExecuteNonQueryAsync();
                        }
                    }

                    // Drop old columns after migration (safe with IF EXISTS)
                    var dropOldColumns = @"
                        ALTER TABLE system_logs
                        DROP COLUMN IF EXISTS timestamp,
                        DROP COLUMN IF EXISTS action,
                        DROP COLUMN IF EXISTS client_name,
                        DROP COLUMN IF EXISTS status,
                        DROP COLUMN IF EXISTS details;
                    ";
                    using var dropCmd = new NpgsqlCommand(dropOldColumns, connection);
                    await dropCmd.ExecuteNonQueryAsync();
                }

                // Set NOT NULL constraint on log_level and log_message after migration
                var setNotNull = @"
                    ALTER TABLE system_logs
                    ALTER COLUMN log_level SET NOT NULL,
                    ALTER COLUMN log_message SET NOT NULL;
                ";
                try
                {
                    using var notNullCmd = new NpgsqlCommand(setNotNull, connection);
                    await notNullCmd.ExecuteNonQueryAsync();
                }
                catch
                {
                    // If there are NULL values, set defaults first
                    var setDefaults = @"
                        UPDATE system_logs SET log_level = 'INFO' WHERE log_level IS NULL;
                        UPDATE system_logs SET log_message = '' WHERE log_message IS NULL;
                    ";
                    using var defaultsCmd = new NpgsqlCommand(setDefaults, connection);
                    await defaultsCmd.ExecuteNonQueryAsync();
                    
                    using var notNullCmd = new NpgsqlCommand(setNotNull, connection);
                    await notNullCmd.ExecuteNonQueryAsync();
                }
            }

            // Ensure indexes exist
            var createIndexes = @"
                CREATE INDEX IF NOT EXISTS idx_system_logs_time ON system_logs(log_time);
            ";
            using var indexCmd = new NpgsqlCommand(createIndexes, connection);
            await indexCmd.ExecuteNonQueryAsync();
        }

        public async Task<bool> ValidateAdminAsync(string username, string password)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = "SELECT password FROM users WHERE username = @username AND role = 'Admin' AND is_active = TRUE";
            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@username", username);

            var result = await command.ExecuteScalarAsync();
            var passwordHash = result?.ToString();

            if (string.IsNullOrWhiteSpace(passwordHash))
            {
                return false;
            }

            // Check if it's a valid bcrypt hash
            if (IsValidBcryptHash(passwordHash))
            {
                try
                {
                    return BCrypt.Net.BCrypt.Verify(password, passwordHash);
                }
                catch (BCrypt.Net.SaltParseException)
                {
                    return false;
                }
            }

            // Fallback: plain text comparison (for migration)
            if (passwordHash == password)
            {
                // Update to hashed password
                await UpdateAdminPasswordAsync(connection);
                return true;
            }
            return false;
        }

        public async Task<bool> ValidateClientAsync(string username, string password)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Check for Student or Teacher role
            var query = "SELECT password FROM users WHERE username = @username AND (role = 'Student' OR role = 'Teacher') AND is_active = TRUE";
            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@username", username);

            var result = await command.ExecuteScalarAsync();
            var passwordHash = result?.ToString();

            if (string.IsNullOrWhiteSpace(passwordHash))
            {
                return false;
            }

            // Check if it's a valid bcrypt hash
            if (IsValidBcryptHash(passwordHash))
            {
                try
                {
                    return BCrypt.Net.BCrypt.Verify(password, passwordHash);
                }
                catch (BCrypt.Net.SaltParseException)
                {
                    return false;
                }
            }

            // Fallback: plain text comparison (for migration)
            return passwordHash == password;
        }

        public async Task LogSystemActionAsync(string action, string clientName, string status, string? details = null)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Get computer_id if clientName exists
            int? computerId = null;
            if (!string.IsNullOrWhiteSpace(clientName))
            {
                var getComputerQuery = "SELECT id FROM computers WHERE client_name = @clientName LIMIT 1";
                using var getComputerCmd = new NpgsqlCommand(getComputerQuery, connection);
                getComputerCmd.Parameters.AddWithValue("@clientName", clientName);
                var computerIdObj = await getComputerCmd.ExecuteScalarAsync();
                if (computerIdObj != null)
                {
                    computerId = Convert.ToInt32(computerIdObj);
                }
            }

            var logLevel = status.ToUpper() switch
            {
                "ERROR" or "FAILED" => "ERROR",
                "WARNING" => "WARNING",
                "DEBUG" => "DEBUG",
                _ => "INFO"
            };

            var logMessage = details ?? action;
            var actionType = action;

            var query = @"
                INSERT INTO system_logs (log_level, log_message, log_time, computer_id, action_type) 
                VALUES (@logLevel, @logMessage, CURRENT_TIMESTAMP, @computerId, @actionType)
            ";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@logLevel", logLevel);
            command.Parameters.AddWithValue("@logMessage", logMessage);
            command.Parameters.AddWithValue("@computerId", computerId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@actionType", actionType);

            await command.ExecuteNonQueryAsync();
        }

        public async Task<List<SystemLog>> GetSystemLogsAsync(int limit = 100)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT 
                    sl.id, 
                    sl.log_level, 
                    sl.log_message, 
                    sl.log_time, 
                    sl.computer_id, 
                    sl.user_id, 
                    sl.action_type,
                    COALESCE(c.client_name, '') as client_name
                FROM system_logs sl
                LEFT JOIN computers c ON sl.computer_id = c.id
                ORDER BY sl.log_time DESC 
                LIMIT @limit
            ";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@limit", limit);

            var logs = new List<SystemLog>();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                logs.Add(new SystemLog
                {
                    Id = reader.GetInt32("id"),
                    LogLevel = reader.GetString("log_level"),
                    LogMessage = reader.GetString("log_message"),
                    LogTime = reader.GetDateTime("log_time"),
                    ComputerId = reader.IsDBNull("computer_id") ? null : reader.GetInt32("computer_id"),
                    UserId = reader.IsDBNull("user_id") ? null : reader.GetInt32("user_id"),
                    ActionType = reader.IsDBNull("action_type") ? null : reader.GetString("action_type"),
                    ClientName = reader.IsDBNull("client_name") ? "" : reader.GetString("client_name")
                });
            }

            return logs;
        }

        public async Task<List<AttendanceLog>> GetAttendanceLogsAsync(DateTime? startDate = null, DateTime? endDate = null)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT 
                    al.id, 
                    al.user_id,
                    al.computer_id, 
                    al.login_time, 
                    al.logout_time, 
                    al.session_duration, 
                    al.status,
                    COALESCE(u.full_name, u.username, '') as student_name,
                    COALESCE(c.client_name, '') as pc_name
                FROM attendance_logs al
                LEFT JOIN users u ON al.user_id = u.id
                LEFT JOIN computers c ON al.computer_id = c.id
                WHERE 1=1
            ";

            // Add date filtering if provided
            if (startDate.HasValue)
            {
                query += " AND DATE(al.login_time) >= @startDate";
            }

            if (endDate.HasValue)
            {
                query += " AND DATE(al.login_time) <= @endDate";
            }

            query += " ORDER BY al.login_time DESC";

            using var command = new NpgsqlCommand(query, connection);

            // Add parameters if dates are provided
            if (startDate.HasValue)
            {
                command.Parameters.AddWithValue("@startDate", startDate.Value.Date);
            }

            if (endDate.HasValue)
            {
                command.Parameters.AddWithValue("@endDate", endDate.Value.Date);
            }

            var logs = new List<AttendanceLog>();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var log = new AttendanceLog
                {
                    Id = reader.GetInt32("id"),
                    UserId = reader.IsDBNull("user_id") ? null : reader.GetInt32("user_id"),
                    ComputerId = reader.IsDBNull("computer_id") ? null : reader.GetInt32("computer_id"),
                    LoginTime = reader.GetDateTime("login_time"),
                    LogoutTime = reader.IsDBNull("logout_time") ? null : reader.GetDateTime("logout_time"),
                    SessionDuration = reader.IsDBNull("session_duration") ? null : reader.GetTimeSpan(reader.GetOrdinal("session_duration")),
                    Status = reader.IsDBNull("status") ? "Active" : reader.GetString("status"),
                    StudentName = reader.IsDBNull("student_name") ? "" : reader.GetString("student_name"),
                    PcName = reader.IsDBNull("pc_name") ? "" : reader.GetString("pc_name")
                };

                logs.Add(log);
            }

            return logs;
        }

        public async Task UpdateClientStatusAsync(string clientName, string ipAddress, bool isConnected, string status = "Online")
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                // Update computers table
                var updateComputerQuery = @"
                    INSERT INTO computers (client_name, ip_address, status, is_online, is_locked, last_seen)
                    VALUES (@clientName, @ipAddress, @status, @isOnline, FALSE, CURRENT_TIMESTAMP)
                    ON CONFLICT (client_name)
                    DO UPDATE SET
                        ip_address = EXCLUDED.ip_address,
                        status = EXCLUDED.status,
                        is_online = EXCLUDED.is_online,
                        last_seen = CURRENT_TIMESTAMP,
                        updated_at = CURRENT_TIMESTAMP
                ";

                using var computerCommand = new NpgsqlCommand(updateComputerQuery, connection);
                computerCommand.Parameters.AddWithValue("@clientName", clientName);
                computerCommand.Parameters.AddWithValue("@ipAddress", ipAddress);
                computerCommand.Parameters.AddWithValue("@status", status);
                computerCommand.Parameters.AddWithValue("@isOnline", isConnected);
                await computerCommand.ExecuteNonQueryAsync();

                // Also update connected_clients for backward compatibility
                var ensureConstraint = @"
                    DO $$ 
                    BEGIN
                        IF NOT EXISTS (
                            SELECT 1 FROM pg_constraint 
                            WHERE conname = 'connected_clients_name_key'
                        ) THEN
                            ALTER TABLE connected_clients ADD CONSTRAINT connected_clients_name_key UNIQUE (name);
                        END IF;
                    END $$;
                ";
                using var constraintCommand = new NpgsqlCommand(ensureConstraint, connection);
                await constraintCommand.ExecuteNonQueryAsync();

                var query = @"
                    INSERT INTO connected_clients (name, ip_address, is_connected, status, last_response) 
                    VALUES (@name, @ipAddress, @isConnected, @status, @lastResponse)
                    ON CONFLICT (name) 
                    DO UPDATE SET 
                        ip_address = EXCLUDED.ip_address,
                        is_connected = EXCLUDED.is_connected,
                        status = EXCLUDED.status,
                        last_response = EXCLUDED.last_response
                ";

                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@name", clientName);
                command.Parameters.AddWithValue("@ipAddress", ipAddress);
                command.Parameters.AddWithValue("@isConnected", isConnected);
                command.Parameters.AddWithValue("@status", status);
                command.Parameters.AddWithValue("@lastResponse", DateTime.UtcNow);

                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                // Log error but don't throw - we don't want to break the connection flow
                await LogSystemActionAsync("Database Update Error", clientName, "Error", $"Failed to update client status: {ex.Message}");
            }
        }

        public async Task<List<ConnectedClient>> GetConnectedClientsAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT id, name, ip_address, last_response, is_connected, status 
                FROM connected_clients 
                ORDER BY name
            ";

            using var command = new NpgsqlCommand(query, connection);
            var clients = new List<ConnectedClient>();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                clients.Add(new ConnectedClient
                {
                    Id = reader.GetInt32("id"),
                    Name = reader.GetString("name"),
                    IpAddress = reader.GetString("ip_address"),
                    LastResponse = reader.GetDateTime("last_response"),
                    IsConnected = reader.GetBoolean("is_connected"),
                    Status = reader.GetString("status")
                });
            }

            return clients;
        }

        public async Task<string?> GetSettingAsync(string key)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = "SELECT setting_value FROM settings WHERE setting_key = @key";
            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@key", key);

            var result = await command.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? null : result.ToString();
        }

        public async Task SetSettingAsync(string key, string value)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                INSERT INTO settings (setting_key, setting_value, updated_at)
                VALUES (@key, @value, CURRENT_TIMESTAMP)
                ON CONFLICT (setting_key)
                DO UPDATE SET
                    setting_value = EXCLUDED.setting_value,
                    updated_at = CURRENT_TIMESTAMP
            ";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@value", value);

            await command.ExecuteNonQueryAsync();
        }

        public async Task DeleteSettingAsync(string key)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = "DELETE FROM settings WHERE setting_key = @key";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@key", key);

            await command.ExecuteNonQueryAsync();
        }

        public async Task<int?> GetUserIdByUsernameAsync(string username)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = "SELECT id FROM users WHERE username = @username AND role = 'Admin' LIMIT 1";
            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@username", username);

            var result = await command.ExecuteScalarAsync();
            return result != null && result != DBNull.Value ? Convert.ToInt32(result) : null;
        }

        public async Task LogAdminActionAsync(string username, string action, string? description = null, string? computerName = null, string? ipAddress = null)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                // Get user ID
                var userId = await GetUserIdByUsernameAsync(username);
                if (userId == null)
                {
                    // If user not found, skip logging
                    return;
                }

                // Get computer ID if computer name is provided
                int? computerId = null;
                if (!string.IsNullOrWhiteSpace(computerName))
                {
                    var getComputerQuery = "SELECT id FROM computers WHERE client_name = @clientName LIMIT 1";
                    using var getComputerCmd = new NpgsqlCommand(getComputerQuery, connection);
                    getComputerCmd.Parameters.AddWithValue("@clientName", computerName);
                    var computerIdObj = await getComputerCmd.ExecuteScalarAsync();
                    if (computerIdObj != null)
                    {
                        computerId = Convert.ToInt32(computerIdObj);
                    }
                }

                var query = @"
                    INSERT INTO activity_logs (user_id, computer_id, action, description, timestamp, ip_address)
                    VALUES (@userId, @computerId, @action, @description, CURRENT_TIMESTAMP, @ipAddress)
                ";

                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@userId", userId);
                command.Parameters.AddWithValue("@computerId", computerId ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@action", action);
                command.Parameters.AddWithValue("@description", description ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@ipAddress", !string.IsNullOrWhiteSpace(ipAddress) ? ipAddress : (object)DBNull.Value);

                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                // Log error but don't throw - we don't want to break the admin action flow
                await LogSystemActionAsync("Admin Action Logging Error", username, "Error", $"Failed to log admin action: {ex.Message}");
            }
        }
    }
}
