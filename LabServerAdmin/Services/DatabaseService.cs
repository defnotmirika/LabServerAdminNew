using Microsoft.Extensions.Configuration;
using Npgsql;
using LabServerAdmin.Models;
using System.Data;

namespace LabServerAdmin.Services
{
    public class DatabaseService
    {
        private readonly string _connectionString;
        private readonly IConfiguration _configuration;

        public DatabaseService(IConfiguration configuration)
        {
            _configuration = configuration;
            _connectionString = _configuration.GetConnectionString("DefaultConnection") 
                ?? "Host=localhost;Database=labserver;Username=postgres;Password=abc123";
        }

        public async Task InitializeDatabaseAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Create tables if they don't exist
            var createTables = @"
                CREATE TABLE IF NOT EXISTS admins (
                    id SERIAL PRIMARY KEY,
                    username VARCHAR(50) NOT NULL UNIQUE,
                    password_hash VARCHAR(255) NOT NULL,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );

                CREATE TABLE IF NOT EXISTS system_logs (
                    id SERIAL PRIMARY KEY,
                    timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    action VARCHAR(100) NOT NULL,
                    client_name VARCHAR(100),
                    status VARCHAR(50),
                    details VARCHAR(500)
                );

                CREATE TABLE IF NOT EXISTS attendance_logs (
                    id SERIAL PRIMARY KEY,
                    student_name VARCHAR(100) NOT NULL,
                    pc_name VARCHAR(100) NOT NULL,
                    time_in TIMESTAMP NOT NULL,
                    time_out TIMESTAMP,
                    is_active BOOLEAN DEFAULT TRUE
                );

                CREATE TABLE IF NOT EXISTS connected_clients (
                    id SERIAL PRIMARY KEY,
                    name VARCHAR(100) NOT NULL,
                    ip_address VARCHAR(45) NOT NULL,
                    last_response TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    is_connected BOOLEAN DEFAULT TRUE,
                    status VARCHAR(50) DEFAULT 'Online'
                );

                CREATE TABLE IF NOT EXISTS system_settings (
                    setting_key VARCHAR(100) PRIMARY KEY,
                    setting_value VARCHAR(255),
                    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );
            ";

            using var command = new NpgsqlCommand(createTables, connection);
            await command.ExecuteNonQueryAsync();

            // Create default admin if none exists
            await CreateDefaultAdminAsync(connection);
        }

        private async Task CreateDefaultAdminAsync(NpgsqlConnection connection)
        {
            var checkAdmin = "SELECT COUNT(*) FROM admins";
            using var checkCommand = new NpgsqlCommand(checkAdmin, connection);
            var adminCount = Convert.ToInt32(await checkCommand.ExecuteScalarAsync());

            if (adminCount == 0)
            {
                var insertAdmin = @"
                    INSERT INTO admins (username, password_hash) 
                    VALUES ('admin', @passwordHash)
                ";
                
                // Default password: admin123 (hashed with BCrypt)
                var hashedPassword = BCrypt.Net.BCrypt.HashPassword("admin123");
                
                using var insertCommand = new NpgsqlCommand(insertAdmin, connection);
                insertCommand.Parameters.AddWithValue("@passwordHash", hashedPassword);
                await insertCommand.ExecuteNonQueryAsync();
            }
        }

        public async Task<bool> ValidateAdminAsync(string username, string password)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = "SELECT password_hash FROM admins WHERE username = @username";
            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@username", username);

            var result = await command.ExecuteScalarAsync();
            if (result != null)
            {
                return BCrypt.Net.BCrypt.Verify(password, result.ToString());
            }
            return false;
        }

        public async Task LogSystemActionAsync(string action, string clientName, string status, string? details = null)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                INSERT INTO system_logs (action, client_name, status, details) 
                VALUES (@action, @clientName, @status, @details)
            ";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@action", action);
            command.Parameters.AddWithValue("@clientName", clientName);
            command.Parameters.AddWithValue("@status", status);
            command.Parameters.AddWithValue("@details", details ?? (object)DBNull.Value);

            await command.ExecuteNonQueryAsync();
        }

        public async Task<List<SystemLog>> GetSystemLogsAsync(int limit = 100)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT id, timestamp, action, client_name, status, details 
                FROM system_logs 
                ORDER BY timestamp DESC 
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
                    Timestamp = reader.GetDateTime("timestamp"),
                    Action = reader.GetString("action"),
                    ClientName = reader.IsDBNull("client_name") ? "" : reader.GetString("client_name"),
                    Status = reader.IsDBNull("status") ? "" : reader.GetString("status"),
                    Details = reader.IsDBNull("details") ? null : reader.GetString("details")
                });
            }

            return logs;
        }

        public async Task<List<AttendanceLog>> GetAttendanceLogsAsync()
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT id, student_name, pc_name, time_in, time_out, is_active 
                FROM attendance_logs 
                ORDER BY time_in DESC
            ";

            using var command = new NpgsqlCommand(query, connection);
            var logs = new List<AttendanceLog>();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                logs.Add(new AttendanceLog
                {
                    Id = reader.GetInt32("id"),
                    StudentName = reader.GetString("student_name"),
                    PcName = reader.GetString("pc_name"),
                    TimeIn = reader.GetDateTime("time_in"),
                    TimeOut = reader.IsDBNull("time_out") ? null : reader.GetDateTime("time_out"),
                    IsActive = reader.GetBoolean("is_active")
                });
            }

            return logs;
        }

        public async Task UpdateClientStatusAsync(string clientName, string ipAddress, bool isConnected, string status = "Online")
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                INSERT INTO connected_clients (name, ip_address, is_connected, status, last_response) 
                VALUES (@name, @ipAddress, @isConnected, @status, @lastResponse)
                ON CONFLICT (name) 
                DO UPDATE SET 
                    ip_address = @ipAddress,
                    is_connected = @isConnected,
                    status = @status,
                    last_response = @lastResponse
            ";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@name", clientName);
            command.Parameters.AddWithValue("@ipAddress", ipAddress);
            command.Parameters.AddWithValue("@isConnected", isConnected);
            command.Parameters.AddWithValue("@status", status);
            command.Parameters.AddWithValue("@lastResponse", DateTime.UtcNow);

            await command.ExecuteNonQueryAsync();
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

            var query = "SELECT setting_value FROM system_settings WHERE setting_key = @key";
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
                INSERT INTO system_settings (setting_key, setting_value, updated_at)
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

            var query = "DELETE FROM system_settings WHERE setting_key = @key";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@key", key);

            await command.ExecuteNonQueryAsync();
        }
    }
}
