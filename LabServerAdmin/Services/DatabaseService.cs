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

            // Lightweight initialization - assumes main tables already exist in PostgreSQL
            // Only create application-specific helper tables if needed
            
            // Create connected_clients table if it doesn't exist (runtime tracking)
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

            // Create login_requests table if it doesn't exist (runtime tracking)
            var createLoginRequests = @"
                CREATE TABLE IF NOT EXISTS login_requests (
                    id SERIAL PRIMARY KEY,
                    studNo VARCHAR(20) NOT NULL REFERENCES us_geninfo(studNo),
                    computer_id INT NOT NULL REFERENCES computers(id),
                    ip_address VARCHAR(45),
                    request_type VARCHAR(20) NOT NULL DEFAULT 'Login',
                    request_timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    request_message VARCHAR(500),
                    status VARCHAR(20) NOT NULL DEFAULT 'Pending',
                    processed_by VARCHAR(20) REFERENCES ua_geninfo(empID),
                    processed_timestamp TIMESTAMP,
                    CONSTRAINT chk_status CHECK (status IN ('Pending', 'Approved', 'Declined')),
                    CONSTRAINT chk_request_type CHECK (request_type IN ('Login', 'Logout'))
                );
                
                CREATE INDEX IF NOT EXISTS idx_login_requests_status ON login_requests(status);
                CREATE INDEX IF NOT EXISTS idx_login_requests_timestamp ON login_requests(request_timestamp DESC);
                CREATE INDEX IF NOT EXISTS idx_login_requests_type ON login_requests(request_type);
            ";

            using var loginRequestCommand = new NpgsqlCommand(createLoginRequests, connection);
            await loginRequestCommand.ExecuteNonQueryAsync();

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

            // All other tables (ua_geninfo, ui_geninfo, us_geninfo, ua_credentials, ui_credentials, 
            // system_logs, attendance_logs, computers, settings, etc.) should already exist in PostgreSQL
        }

        private static bool IsValidBcryptHash(string? hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
            {
                return false;
            }

            return Regex.IsMatch(hash, @"^\$2[aby]\$\d{2}\$[./A-Za-z0-9]{53}$");
        }

        public async Task<bool> ValidateAdminAsync(string username, string password)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            try
            {
                // Query ua_credentials table for admin authentication
                var query = @"
                    SELECT password_hash 
                    FROM ua_credentials 
                    WHERE (username = @username OR empID = @username) 
                    AND role = 'ADMIN'";
                
                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@username", username);

                var result = await command.ExecuteScalarAsync();
                var passwordHash = result?.ToString();

                if (string.IsNullOrWhiteSpace(passwordHash))
                {
                    // Fallback to hardcoded credentials if table doesn't exist or user not found
                    return (username == "admin" && password == "admin123");
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
                    var updateQuery = @"
                        UPDATE ua_credentials 
                        SET password_hash = @newHash 
                        WHERE (username = @username OR empID = @username) 
                        AND role = 'ADMIN'";
        
                    var hashedPassword = BCrypt.Net.BCrypt.HashPassword(password);
                    using var updateCommand = new NpgsqlCommand(updateQuery, connection);
                    updateCommand.Parameters.AddWithValue("@newHash", hashedPassword);
                    updateCommand.Parameters.AddWithValue("@username", username);
                    await updateCommand.ExecuteNonQueryAsync();
        
                    return true;
                }
                return false;
            }
            catch (Exception)
            {
                // If database error, fallback to hardcoded credentials
                return (username == "admin" && password == "admin123");
            }
        }

        public async Task<bool> ValidateClientAsync(string username, string password)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            try
            {
                // First try instructor credentials (ui_credentials)
                var instructorQuery = @"
                    SELECT password_hash 
                    FROM ui_credentials 
                    WHERE (username = @username OR empID = @username) 
                    AND role = 'INSTRUCTOR'";
                
                using var instructorCommand = new NpgsqlCommand(instructorQuery, connection);
                instructorCommand.Parameters.AddWithValue("@username", username);

                var instructorResult = await instructorCommand.ExecuteScalarAsync();
                var instructorPasswordHash = instructorResult?.ToString();

                if (!string.IsNullOrWhiteSpace(instructorPasswordHash))
                {
                    // Check if it's a valid bcrypt hash
                    if (IsValidBcryptHash(instructorPasswordHash))
                    {
                        try
                        {
                            return BCrypt.Net.BCrypt.Verify(password, instructorPasswordHash);
                        }
                        catch (BCrypt.Net.SaltParseException)
                        {
                            return false;
                        }
                    }

                    // Fallback: plain text comparison (for migration)
                    if (instructorPasswordHash == password)
                    {
                        // Update to hashed password
                        var updateQuery = @"
                            UPDATE ui_credentials 
                            SET password_hash = @newHash 
                            WHERE (username = @username OR empID = @username) 
                            AND role = 'INSTRUCTOR'";
            
                        var hashedPassword = BCrypt.Net.BCrypt.HashPassword(password);
                        using var updateCommand = new NpgsqlCommand(updateQuery, connection);
                        updateCommand.Parameters.AddWithValue("@newHash", hashedPassword);
                        updateCommand.Parameters.AddWithValue("@username", username);
                        await updateCommand.ExecuteNonQueryAsync();
                        
                        return true;
                    }
                }

                // Fallback to hardcoded credentials if not found
                return (username == "student" && password == "student123") || 
                       (username == "client" && password == "client123");
            }
            catch (Exception)
            {
                // If database error, fallback to hardcoded credentials
                return (username == "student" && password == "student123") || 
                       (username == "client" && password == "client123");
            }
        }

        public async Task<bool> ValidateInstructorAsync(string username, string password)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            try
            {
                var instructorQuery = @"
                    SELECT password_hash 
                    FROM ui_credentials 
                    WHERE (username = @username OR empID = @username) 
                    AND role = 'INSTRUCTOR'";

                using var instructorCommand = new NpgsqlCommand(instructorQuery, connection);
                instructorCommand.Parameters.AddWithValue("@username", username);

                var instructorResult = await instructorCommand.ExecuteScalarAsync();
                var instructorPasswordHash = instructorResult?.ToString();

                if (string.IsNullOrWhiteSpace(instructorPasswordHash))
                {
                    return false;
                }

                if (IsValidBcryptHash(instructorPasswordHash))
                {
                    try
                    {
                        return BCrypt.Net.BCrypt.Verify(password, instructorPasswordHash);
                    }
                    catch (BCrypt.Net.SaltParseException)
                    {
                        return false;
                    }
                }

                // Fallback: plain text comparison (for migration)
                if (instructorPasswordHash == password)
                {
                    var updateQuery = @"
                        UPDATE ui_credentials 
                        SET password_hash = @newHash 
                        WHERE (username = @username OR empID = @username) 
                        AND role = 'INSTRUCTOR'";

                    var hashedPassword = BCrypt.Net.BCrypt.HashPassword(password);
                    using var updateCommand = new NpgsqlCommand(updateQuery, connection);
                    updateCommand.Parameters.AddWithValue("@newHash", hashedPassword);
                    updateCommand.Parameters.AddWithValue("@username", username);
                    await updateCommand.ExecuteNonQueryAsync();

                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        public async Task LogSystemActionAsync(string action, string clientName, string status, string? details = null, string? userId = null)
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
                INSERT INTO system_logs (log_level, log_message, action_type, user_id, computer_id, log_time) 
                VALUES (@logLevel, @logMessage, @actionType, @userId, @computerId, CURRENT_TIMESTAMP)
            ";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@logLevel", logLevel);
            command.Parameters.AddWithValue("@logMessage", logMessage);
            command.Parameters.AddWithValue("@actionType", actionType);
            command.Parameters.AddWithValue("@userId", string.IsNullOrWhiteSpace(userId) ? (object)DBNull.Value : userId);
            command.Parameters.AddWithValue("@computerId", computerId ?? (object)DBNull.Value);

            await command.ExecuteNonQueryAsync();
        }

        public async Task<List<SystemLog>> GetSystemLogsAsync(DateTime? startDate = null, DateTime? endDate = null, int limit = 100)
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
                WHERE 1=1
            ";

            // Add date filtering if provided
            if (startDate.HasValue)
            {
                query += " AND DATE(sl.log_time) >= @startDate";
            }

            if (endDate.HasValue)
            {
                query += " AND DATE(sl.log_time) <= @endDate";
            }

            query += " ORDER BY sl.log_time DESC LIMIT @limit";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@limit", limit);

            // Add parameters if dates are provided
            if (startDate.HasValue)
            {
                command.Parameters.AddWithValue("@startDate", startDate.Value.Date);
            }

            if (endDate.HasValue)
            {
                command.Parameters.AddWithValue("@endDate", endDate.Value.Date);
            }

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
                    UserIdString = reader.IsDBNull("user_id") ? null : reader.GetString("user_id"),
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
                    al.studNo,
                    al.computer_id, 
                    al.login_time, 
                    al.logout_time, 
                    al.session_duration, 
                    al.status,
                    COALESCE(us.full_name, al.studNo, '') as student_name,
                    COALESCE(c.client_name, '') as pc_name
                FROM attendance_logs al
                LEFT JOIN us_geninfo us ON al.studNo = us.studNo
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
                    StudNo = reader.IsDBNull("studNo") ? null : reader.GetString("studNo"),
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

        public async Task<List<ActivityLog>> GetActivityLogsAsync(DateTime? startDate = null, DateTime? endDate = null, int limit = 100)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT 
                    al.id, 
                    al.studNo,
                    al.computer_id, 
                    al.action, 
                    al.description, 
                    al.timestamp,
                    COALESCE(us.full_name, al.studNo, '') as student_name,
                    COALESCE(c.client_name, '') as pc_name
                FROM activity_logs al
                LEFT JOIN us_geninfo us ON al.studNo = us.studNo
                LEFT JOIN computers c ON al.computer_id = c.id
                WHERE 1=1
            ";

            // Add date filtering if provided
            if (startDate.HasValue)
            {
                query += " AND DATE(al.timestamp) >= @startDate";
            }

            if (endDate.HasValue)
            {
                query += " AND DATE(al.timestamp) <= @endDate";
            }

            query += " ORDER BY al.timestamp DESC LIMIT @limit";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@limit", limit);

            // Add parameters if dates are provided
            if (startDate.HasValue)
            {
                command.Parameters.AddWithValue("@startDate", startDate.Value.Date);
            }

            if (endDate.HasValue)
            {
                command.Parameters.AddWithValue("@endDate", endDate.Value.Date);
            }

            var logs = new List<ActivityLog>();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var log = new ActivityLog
                {
                    Id = reader.GetInt32("id"),
                    StudNo = reader.GetString("studNo"),
                    ComputerId = reader.GetInt32("computer_id"),
                    Action = reader.GetString("action"),
                    Description = reader.IsDBNull("description") ? null : reader.GetString("description"),
                    Timestamp = reader.GetDateTime("timestamp"),
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
                    VALUES (@clientName, @ipAddress::inet, @status, @isOnline, FALSE, CURRENT_TIMESTAMP)
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

            try
            {
                // First try admin credentials (ua_credentials)
                var adminQuery = @"
                    SELECT id 
                    FROM ua_credentials 
                    WHERE (username = @username OR empID = @username) 
                    AND role = 'ADMIN' 
                    LIMIT 1";
        
                using var adminCommand = new NpgsqlCommand(adminQuery, connection);
                adminCommand.Parameters.AddWithValue("@username", username);
                var adminResult = await adminCommand.ExecuteScalarAsync();
        
                if (adminResult != null && adminResult != DBNull.Value)
                {
                    return Convert.ToInt32(adminResult);
                }

                // Try instructor credentials (ui_credentials)
                var instructorQuery = @"
                    SELECT id 
                    FROM ui_credentials 
                    WHERE (username = @username OR empID = @username) 
                    AND role = 'INSTRUCTOR' 
                    LIMIT 1";
        
                using var instructorCommand = new NpgsqlCommand(instructorQuery, connection);
                instructorCommand.Parameters.AddWithValue("@username", username);
                var instructorResult = await instructorCommand.ExecuteScalarAsync();
        
                if (instructorResult != null && instructorResult != DBNull.Value)
                {
                    return Convert.ToInt32(instructorResult);
                }

                // No fallback - return null if not found
                return null;
            }
            catch (Exception)
            {
                // If database error, return null
                return null;
            }
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

        #region Login Request Management

        /// <summary>
        /// Creates a new login request from a client
        /// </summary>
        public async Task<int> CreateLoginRequestAsync(string studNo, string pcName, string? ipAddress = null, string? requestMessage = null, string requestType = "Login")
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Get computer_id from pc_name
            var computerId = await GetComputerIdByNameAsync(connection, pcName, ipAddress);
            if (!computerId.HasValue)
            {
                throw new Exception($"Computer not found: {pcName}");
            }

            var query = @"
                INSERT INTO login_requests (studNo, computer_id, ip_address, request_type, request_message, request_timestamp, status)
                VALUES (@studNo, @computerId, @ipAddress, @requestType, @requestMessage, CURRENT_TIMESTAMP, 'Pending')
                RETURNING id
            ";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@studNo", studNo);
            command.Parameters.AddWithValue("@computerId", computerId.Value);
            command.Parameters.AddWithValue("@ipAddress", ipAddress ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@requestType", requestType);
            command.Parameters.AddWithValue("@requestMessage", requestMessage ?? (object)DBNull.Value);

            var requestId = await command.ExecuteScalarAsync();
            
            // Log the request in system logs
            await LogSystemActionAsync(
                $"{requestType} Request", 
                pcName, 
                "INFO", 
                $"Student '{studNo}' requests {requestType.ToLower()} approval on PC '{pcName}'"
            );

            return Convert.ToInt32(requestId);
        }

        private async Task<int?> GetComputerIdByNameAsync(NpgsqlConnection connection, string pcName, string? ipAddress = null)
        {
            var getComputerQuery = "SELECT id FROM computers WHERE client_name = @clientName LIMIT 1";
            using var getComputerCmd = new NpgsqlCommand(getComputerQuery, connection);
            getComputerCmd.Parameters.AddWithValue("@clientName", pcName);
            var existingId = await getComputerCmd.ExecuteScalarAsync();
            if (existingId != null && existingId != DBNull.Value)
            {
                return Convert.ToInt32(existingId);
            }

            // Create a minimal computer entry if it does not exist
            var insertComputer = @"
                INSERT INTO computers (client_name, ip_address, status, is_online, is_locked, last_seen, created_at, updated_at)
                VALUES (@clientName, @ipAddress::inet, 'Online', TRUE, FALSE, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
                RETURNING id;
            ";

            using var insertCmd = new NpgsqlCommand(insertComputer, connection);
            insertCmd.Parameters.AddWithValue("@clientName", pcName);
            insertCmd.Parameters.AddWithValue("@ipAddress", string.IsNullOrWhiteSpace(ipAddress) ? "0.0.0.0" : ipAddress);
            var newId = await insertCmd.ExecuteScalarAsync();
            return newId == null || newId == DBNull.Value ? null : Convert.ToInt32(newId);
        }

        /// <summary>
        /// Retrieves all pending login requests
        /// </summary>
        /// <returns>List of pending LoginRequest objects</returns>
        public async Task<List<LoginRequest>> GetPendingLoginRequestsAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT lr.id,
                       lr.studNo,
                       COALESCE(CONCAT(us.f_name, ' ', us.l_name), lr.studNo) AS student_name,
                       lr.computer_id,
                       COALESCE(c.client_name, '') AS pc_name,
                       lr.ip_address,
                       lr.request_type,
                       lr.request_timestamp,
                       lr.request_message,
                       lr.status,
                       lr.processed_by,
                       lr.processed_timestamp
                FROM login_requests lr
                LEFT JOIN us_geninfo us ON lr.studNo = us.studNo
                LEFT JOIN computers c ON lr.computer_id = c.id
                WHERE lr.status = 'Pending'
                ORDER BY lr.request_timestamp DESC
            ";

            using var command = new NpgsqlCommand(query, connection);
            var requests = new List<LoginRequest>();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                requests.Add(new LoginRequest
                {
                    Id = reader.GetInt32("id"),
                    StudNo = reader.GetString("studNo"),
                    StudentName = reader.IsDBNull("student_name") ? null : reader.GetString("student_name"),
                    ComputerId = reader.GetInt32("computer_id"),
                    PcName = reader.IsDBNull("pc_name") ? string.Empty : reader.GetString("pc_name"),
                    IpAddress = reader.IsDBNull("ip_address") ? null : reader.GetString("ip_address"),
                    RequestType = reader.IsDBNull("request_type") ? "Login" : reader.GetString("request_type"),
                    RequestTimestamp = reader.GetDateTime("request_timestamp"),
                    RequestMessage = reader.IsDBNull("request_message") ? null : reader.GetString("request_message"),
                    Status = reader.GetString("status"),
                    ProcessedBy = reader.IsDBNull("processed_by") ? null : reader.GetString("processed_by"),
                    ProcessedTimestamp = reader.IsDBNull("processed_timestamp") ? null : reader.GetDateTime("processed_timestamp")
                });
            }

            return requests;
        }

        /// <summary>
        /// Retrieves all login requests (for history/reporting)
        /// </summary>
        /// <param name="limit">Maximum number of requests to return</param>
        /// <returns>List of LoginRequest objects</returns>
        public async Task<List<LoginRequest>> GetAllLoginRequestsAsync(int limit = 100)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT lr.id,
                       lr.studNo,
                       COALESCE(CONCAT(us.f_name, ' ', us.l_name), lr.studNo) AS student_name,
                       lr.computer_id,
                       COALESCE(c.client_name, '') AS pc_name,
                       lr.ip_address,
                       lr.request_type,
                       lr.request_timestamp,
                       lr.request_message,
                       lr.status,
                       lr.processed_by,
                       lr.processed_timestamp
                FROM login_requests lr
                LEFT JOIN us_geninfo us ON lr.studNo = us.studNo
                LEFT JOIN computers c ON lr.computer_id = c.id
                ORDER BY lr.request_timestamp DESC
                LIMIT @limit
            ";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@limit", limit);

            var requests = new List<LoginRequest>();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                requests.Add(new LoginRequest
                {
                    Id = reader.GetInt32("id"),
                    StudNo = reader.GetString("studNo"),
                    StudentName = reader.IsDBNull("student_name") ? null : reader.GetString("student_name"),
                    ComputerId = reader.GetInt32("computer_id"),
                    PcName = reader.IsDBNull("pc_name") ? string.Empty : reader.GetString("pc_name"),
                    IpAddress = reader.IsDBNull("ip_address") ? null : reader.GetString("ip_address"),
                    RequestType = reader.IsDBNull("request_type") ? "Login" : reader.GetString("request_type"),
                    RequestTimestamp = reader.GetDateTime("request_timestamp"),
                    RequestMessage = reader.IsDBNull("request_message") ? null : reader.GetString("request_message"),
                    Status = reader.GetString("status"),
                    ProcessedBy = reader.IsDBNull("processed_by") ? null : reader.GetString("processed_by"),
                    ProcessedTimestamp = reader.IsDBNull("processed_timestamp") ? null : reader.GetDateTime("processed_timestamp")
                });
            }

            return requests;
        }

        /// <summary>
        /// Approves a login request
        /// </summary>
        /// <param name="requestId">ID of the request to approve</param>
        /// <param name="adminUsername">Username of the admin approving the request</param>
        /// <returns>True if successful, false otherwise</returns>
        public async Task<bool> ApproveLoginRequestAsync(int requestId, string adminUsername)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var getRequestQuery = @"
                SELECT lr.studNo, lr.request_type, c.client_name 
                FROM login_requests lr
                LEFT JOIN computers c ON lr.computer_id = c.id
                WHERE lr.id = @id AND lr.status = 'Pending'";
            using var getCmd = new NpgsqlCommand(getRequestQuery, connection);
            getCmd.Parameters.AddWithValue("@id", requestId);

            string? studNo = null;
            string? requestType = null;
            string? pcName = null;

            using (var reader = await getCmd.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    studNo = reader.GetString("studNo");
                    requestType = reader.IsDBNull("request_type") ? "Login" : reader.GetString("request_type");
                    pcName = reader.IsDBNull("client_name") ? null : reader.GetString("client_name");
                }
                else
                {
                    return false;
                }
            }

            var query = @"
                UPDATE login_requests
                SET status = 'Approved',
                    processed_by = @processedBy,
                    processed_timestamp = CURRENT_TIMESTAMP
                WHERE id = @id AND status = 'Pending'
            ";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@id", requestId);
            command.Parameters.AddWithValue("@processedBy", adminUsername);

            var rowsAffected = await command.ExecuteNonQueryAsync();

            if (rowsAffected > 0)
            {
                await LogSystemActionAsync(
                    $"{requestType} Request Approved",
                    pcName ?? "Unknown",
                    "INFO",
                    $"Admin '{adminUsername}' approved {requestType?.ToLower()} request for student '{studNo}' on PC '{pcName ?? "Unknown"}'"
                );
                return true;
            }

            return false;
        }

        /// <summary>
        /// Declines a login request
        /// </summary>
        /// <param name="requestId">ID of the request to decline</param>
        /// <param name="adminUsername">Username of the admin declining the request</param>
        /// <returns>True if successful, false otherwise</returns>
        public async Task<bool> DeclineLoginRequestAsync(int requestId, string adminUsername)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var getRequestQuery = @"
                SELECT lr.studNo, lr.request_type, c.client_name 
                FROM login_requests lr
                LEFT JOIN computers c ON lr.computer_id = c.id
                WHERE lr.id = @id AND lr.status = 'Pending'";
            using var getCmd = new NpgsqlCommand(getRequestQuery, connection);
            getCmd.Parameters.AddWithValue("@id", requestId);

            string? studNo = null;
            string? requestType = null;
            string? pcName = null;

            using (var reader = await getCmd.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    studNo = reader.GetString("studNo");
                    requestType = reader.IsDBNull("request_type") ? "Login" : reader.GetString("request_type");
                    pcName = reader.IsDBNull("client_name") ? null : reader.GetString("client_name");
                }
                else
                {
                    return false;
                }
            }

            var query = @"
                UPDATE login_requests
                SET status = 'Declined',
                    processed_by = @processedBy,
                    processed_timestamp = CURRENT_TIMESTAMP
                WHERE id = @id AND status = 'Pending'
            ";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@id", requestId);
            command.Parameters.AddWithValue("@processedBy", adminUsername);

            var rowsAffected = await command.ExecuteNonQueryAsync();

            if (rowsAffected > 0)
            {
                await LogSystemActionAsync(
                    $"{requestType} Request Declined",
                    pcName ?? "Unknown",
                    "INFO",
                    $"Admin '{adminUsername}' declined {requestType?.ToLower()} request for student '{studNo}' on PC '{pcName ?? "Unknown"}'"
                );
                return true;
            }

            return false;
        }

        public async Task<List<Computer>> GetComputersAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT id,
                       client_name,
                       ip_address,
                       lab_id,
                       status,
                       is_locked,
                       is_online,
                       last_seen,
                       created_at,
                       updated_at
                FROM computers
                ORDER BY client_name";

            using var command = new NpgsqlCommand(query, connection);
            var computers = new List<Computer>();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                computers.Add(new Computer
                {
                    Id = reader.GetInt32("id"),
                    ClientName = reader.GetString("client_name"),
                    IpAddress = reader.IsDBNull("ip_address") ? string.Empty : reader.GetString("ip_address"),
                    LabId = reader.IsDBNull("lab_id") ? null : reader.GetInt32("lab_id"),
                    Status = reader.IsDBNull("status") ? "Offline" : reader.GetString("status"),
                    IsLocked = reader.IsDBNull("is_locked") ? false : reader.GetBoolean("is_locked"),
                    IsOnline = reader.IsDBNull("is_online") ? false : reader.GetBoolean("is_online"),
                    LastSeen = reader.IsDBNull("last_seen") ? null : reader.GetDateTime("last_seen"),
                    CreatedAt = reader.IsDBNull("created_at") ? DateTime.UtcNow : reader.GetDateTime("created_at"),
                    UpdatedAt = reader.IsDBNull("updated_at") ? DateTime.UtcNow : reader.GetDateTime("updated_at")
                });
            }

            return computers;
        }

        public async Task<List<InstructorClassListItem>> GetInstructorClassListAsync(string instructorId, DateTime? dateFilter = null)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT al.studNo,
                       COALESCE(us.f_name, '') AS first_name,
                       COALESCE(us.l_name, '') AS last_name,
                       '' AS section_name,
                       al.login_time,
                       al.logout_time,
                       al.status,
                       COALESCE(c.client_name, '') AS client_name
                FROM attendance_logs al
                LEFT JOIN us_geninfo us ON al.studNo = us.studNo
                LEFT JOIN computers c ON al.computer_id = c.id
                WHERE 1=1";

            if (dateFilter.HasValue)
            {
                query += " AND DATE(al.login_time) = @date";
            }

            query += " ORDER BY al.login_time DESC";

            using var command = new NpgsqlCommand(query, connection);
            if (dateFilter.HasValue)
            {
                command.Parameters.AddWithValue("@date", dateFilter.Value.Date);
            }

            var list = new List<InstructorClassListItem>();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new InstructorClassListItem
                {
                    StudNo = reader.IsDBNull("studNo") ? string.Empty : reader.GetString("studNo"),
                    FirstName = reader.IsDBNull("first_name") ? string.Empty : reader.GetString("first_name"),
                    LastName = reader.IsDBNull("last_name") ? string.Empty : reader.GetString("last_name"),
                    SectionName = reader.IsDBNull("section_name") ? string.Empty : reader.GetString("section_name"),
                    LoginTime = reader.IsDBNull("login_time") ? null : reader.GetDateTime("login_time"),
                    LogoutTime = reader.IsDBNull("logout_time") ? null : reader.GetDateTime("logout_time"),
                    Status = reader.IsDBNull("status") ? null : reader.GetString("status"),
                    ClientName = reader.IsDBNull("client_name") ? null : reader.GetString("client_name")
                });
            }

            return list;
        }

        #endregion
    }
}
