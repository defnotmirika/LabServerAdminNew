using Microsoft.Extensions.Configuration;
using Npgsql;
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LabServerClient.Services
{
    public class DatabaseService
    {
        private readonly string _connectionString;

        public DatabaseService(IConfiguration configuration)
        {
            var supabaseConnection = Environment.GetEnvironmentVariable("SUPABASE_DB_CONNECTION")
                ?? configuration["Supabase:ConnectionString"];

            var defaultConnection = configuration.GetConnectionString("DefaultConnection")
                ?? "Host=localhost;Database=labserver;Username=postgres;Password=abc123";

            _connectionString = !string.IsNullOrWhiteSpace(supabaseConnection)
                ? supabaseConnection
                : defaultConnection;
        }

        public string ConnectionString => _connectionString;

        public async Task<bool> ValidateClientAsync(string username, string password)
        {
            try
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
            catch
            {
                // If database is not available, fall back to hardcoded credentials
                return (username == "student" && password == "student123") || 
                       (username == "client" && password == "client123");
            }
        }

        public async Task<string?> GetUserRoleAsync(string username, string password)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                // Get user role and password
                var query = "SELECT role, password FROM users WHERE username = @username AND is_active = TRUE LIMIT 1";
                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@username", username);

                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var roleOrdinal = reader.GetOrdinal("role");
                    var passwordOrdinal = reader.GetOrdinal("password");

                    var role = reader.IsDBNull(roleOrdinal) ? null : reader.GetString(roleOrdinal);
                    var passwordHash = reader.IsDBNull(passwordOrdinal) ? null : reader.GetString(passwordOrdinal);

                    if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(passwordHash))
                    {
                        return null;
                    }

                    // Verify password
                    bool passwordValid = false;
                    if (IsValidBcryptHash(passwordHash))
                    {
                        try
                        {
                            passwordValid = BCrypt.Net.BCrypt.Verify(password, passwordHash);
                        }
                        catch (BCrypt.Net.SaltParseException)
                        {
                            passwordValid = false;
                        }
                    }
                    else
                    {
                        passwordValid = passwordHash == password;
                    }

                    return passwordValid ? role : null;
                }

                return null;
            }
            catch
            {
                // Fallback: check hardcoded credentials
                if (username == "admin" && password == "admin123")
                    return "Admin";
                if (username == "student" && password == "student123" || username == "client" && password == "client123")
                    return "Student";
                return null;
            }
        }

        public async Task<int?> GetClientIdByUsernameAsync(string username)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var query = "SELECT id FROM clients WHERE username = @username LIMIT 1";
                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@username", username);

                var result = await command.ExecuteScalarAsync();
                return result != null && result != DBNull.Value ? Convert.ToInt32(result) : null;
            }
            catch
            {
                return null;
            }
        }

        public async Task<int?> EnsureClientIdAsync(string username, string password, string pcName)
        {
            try
            {
                using var connection = new Npgsql.NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var passwordHash = BCrypt.Net.BCrypt.HashPassword(password);

                var sql = @"
                    INSERT INTO clients (username, password_hash, created_at)
                    VALUES (@username, @password_hash, NOW())
                    ON CONFLICT (username) DO NOTHING
                    RETURNING id;";

                using var cmd = new Npgsql.NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@username", username);
                cmd.Parameters.AddWithValue("@password_hash", passwordHash);

                var result = await cmd.ExecuteScalarAsync();
                if (result != null && result != DBNull.Value)
                {
                    return Convert.ToInt32(result);
                }

                // If conflict (user exists), just return the existing ID
                var getIdSql = "SELECT id FROM clients WHERE username = @username LIMIT 1";
                using var getCmd = new Npgsql.NpgsqlCommand(getIdSql, connection);
                getCmd.Parameters.AddWithValue("@username", username);
                var existingId = await getCmd.ExecuteScalarAsync();
                return existingId != null && existingId != DBNull.Value ? Convert.ToInt32(existingId) : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsValidBcryptHash(string? hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
            {
                return false;
            }

            return Regex.IsMatch(hash, @"^\$2[aby]\$\d{2}\$[./A-Za-z0-9]{53}$");
        }
    }
}

