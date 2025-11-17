using Microsoft.Extensions.Configuration;
using Npgsql;
using System;
using System.Text.RegularExpressions;

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

        public async Task<bool> ValidateClientAsync(string username, string password)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var query = "SELECT password_hash FROM clients WHERE username = @username";
                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@username", username);

                var result = await command.ExecuteScalarAsync();
                var passwordHash = result?.ToString();

                if (string.IsNullOrWhiteSpace(passwordHash) || !IsValidBcryptHash(passwordHash))
                {
                    return false;
                }

                try
                {
                    return BCrypt.Net.BCrypt.Verify(password, passwordHash);
                }
                catch (BCrypt.Net.SaltParseException)
                {
                    return false;
                }
            }
            catch
            {
                // If database is not available, fall back to hardcoded credentials
                return (username == "client" && password == "client123");
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

