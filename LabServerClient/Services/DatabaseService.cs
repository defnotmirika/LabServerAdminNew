using Microsoft.Extensions.Configuration;
using Npgsql;

namespace LabServerClient.Services
{
    public class DatabaseService
    {
        private readonly string _connectionString;

        public DatabaseService(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") 
                ?? "Host=localhost;Database=labserver;Username=postgres;Password=abc123";
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
                if (result != null)
                {
                    return BCrypt.Net.BCrypt.Verify(password, result.ToString());
                }
                return false;
            }
            catch
            {
                // If database is not available, fall back to hardcoded credentials
                return (username == "client" && password == "client123");
            }
        }
    }
}

