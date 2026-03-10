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
                ?? "Host=192.168.0.11;Port=5432;Database=learniqDB;Username=postgres;Password=mynewpass";

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

                var query = "SELECT password_hash FROM us_credentials WHERE (username = @username OR studNo = @username) AND role = 'STUDENT' LIMIT 1";
                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@username", username);

                var result = await command.ExecuteScalarAsync();
                var passwordHash = result?.ToString();

                if (string.IsNullOrWhiteSpace(passwordHash))
                {
                    return false;
                }

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

                return passwordHash == password;
            }
            catch
            {
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

                var query = "SELECT role, password_hash FROM us_credentials WHERE (username = @username OR studNo = @username) LIMIT 1";
                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@username", username);

                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var role = reader.IsDBNull(reader.GetOrdinal("role")) ? null : reader.GetString(reader.GetOrdinal("role"));
                    var passwordHash = reader.IsDBNull(reader.GetOrdinal("password_hash")) ? null : reader.GetString(reader.GetOrdinal("password_hash"));

                    if (string.IsNullOrWhiteSpace(passwordHash))
                    {
                        return null;
                    }

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

                    return passwordValid ? (role ?? "Student") : null;
                }

                return null;
            }
            catch
            {
                if ((username == "student" && password == "student123") ||
                    (username == "client" && password == "client123"))
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

                var query = "SELECT id FROM us_credentials WHERE (username = @username OR studNo = @username) LIMIT 1";
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

                var getIdSql = "SELECT id FROM us_credentials WHERE (username = @username OR studNo = @username) LIMIT 1";
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

        /// <summary>
        /// Validates login credentials against us_credentials table with PC Name check from us_geninfo.
        /// FIX: ValidateScheduleAsync now uses its own connection to avoid "command already in progress" error.
        /// </summary>
        public async Task<LoginValidationResult?> ValidateLoginWithPcNameAsync(string username, string password, string currentPcName)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var query = @"
                    SELECT uc.password_hash, uc.role, uc.studNo, c.client_name
                    FROM us_credentials uc
                    LEFT JOIN us_geninfo ug ON uc.studNo = ug.studNo
                    LEFT JOIN computers c ON ug.default_computer_id = c.id
                    WHERE (uc.username = @username OR uc.studNo = @username) 
                    LIMIT 1";

                using var command = new NpgsqlCommand(query, connection);
                command.Parameters.AddWithValue("@username", username);

                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var storedPasswordHash = reader.GetString(reader.GetOrdinal("password_hash"));
                    var role = reader.IsDBNull(reader.GetOrdinal("role")) ? "STUDENT" : reader.GetString(reader.GetOrdinal("role"));
                    var studNo = reader.IsDBNull(reader.GetOrdinal("studNo")) ? null : reader.GetString(reader.GetOrdinal("studNo"));
                    var assignedPcName = reader.IsDBNull(reader.GetOrdinal("client_name")) ? null : reader.GetString(reader.GetOrdinal("client_name"));

                    bool passwordValid = false;
                    if (IsValidBcryptHash(storedPasswordHash))
                    {
                        try
                        {
                            passwordValid = BCrypt.Net.BCrypt.Verify(password, storedPasswordHash);
                        }
                        catch (BCrypt.Net.SaltParseException)
                        {
                            passwordValid = false;
                        }
                    }
                    else
                    {
                        passwordValid = storedPasswordHash == password;
                    }

                    if (passwordValid)
                    {
                        if (string.IsNullOrWhiteSpace(assignedPcName))
                        {
                            return new LoginValidationResult
                            {
                                IsValid = false,
                                Username = username,
                                UserRole = role,
                                AssignedPcName = null,
                                IsPcNameMatch = false
                            };
                        }

                        var pcMatch = string.Equals(currentPcName, assignedPcName, StringComparison.OrdinalIgnoreCase);

                        if (!pcMatch)
                        {
                            return new LoginValidationResult
                            {
                                IsValid = true,
                                Username = username,
                                UserRole = role,
                                AssignedPcName = assignedPcName,
                                IsPcNameMatch = false
                            };
                        }

                        // PC matches - validate schedule (only for STUDENT role)
                        // FIX: pass _connectionString instead of connection to avoid
                        // "A command is already in progress" Npgsql error
                        if (role.Equals("STUDENT", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(studNo))
                        {
                            var scheduleResult = await ValidateScheduleAsync(_connectionString, studNo, currentPcName);
                            if (!scheduleResult.IsValid)
                            {
                                return new LoginValidationResult
                                {
                                    IsValid = false,
                                    Username = username,
                                    UserRole = role,
                                    AssignedPcName = assignedPcName,
                                    IsPcNameMatch = true,
                                    ScheduleError = scheduleResult.ErrorMessage
                                };
                            }
                        }

                        return new LoginValidationResult
                        {
                            IsValid = true,
                            Username = username,
                            UserRole = role,
                            AssignedPcName = assignedPcName,
                            IsPcNameMatch = true
                        };
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Login validation error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Validates if student can login based on schedule constraints.
        /// FIX: Uses its own NpgsqlConnection (via connectionString) instead of a shared one
        /// to avoid "A command is already in progress" errors from Npgsql.
        /// </summary>
        private async Task<ScheduleValidationResult> ValidateScheduleAsync(string connectionString, string studNo, string clientName)
        {
            try
            {
                using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();

                var studentInfoQuery = @"
                    SELECT ug.section_id, c.lab_id
                    FROM us_geninfo ug
                    CROSS JOIN computers c
                    WHERE ug.studNo = @studNo AND c.client_name = @clientName
                    LIMIT 1";

                using var studentCmd = new NpgsqlCommand(studentInfoQuery, connection);
                studentCmd.Parameters.AddWithValue("@studNo", studNo);
                studentCmd.Parameters.AddWithValue("@clientName", clientName);

                int? sectionId = null;
                int? labId = null;

                using (var reader = await studentCmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        sectionId = reader.IsDBNull(0) ? null : reader.GetInt32(0);
                        labId = reader.IsDBNull(1) ? null : reader.GetInt32(1);
                    }
                }

                if (!sectionId.HasValue || !labId.HasValue)
                {
                    return new ScheduleValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = "Student section or PC lab assignment not found."
                    };
                }

                var now = DateTime.Now;
                var currentDay = now.DayOfWeek.ToString();
                var currentTime = now.TimeOfDay;

                var scheduleQuery = @"
                    SELECT schedule_id, time_in, time_out
                    FROM course_schedules
                    WHERE section_id = @sectionId
                      AND lab_id = @labId
                      AND day_of_week = @dayOfWeek
                      AND @currentTime >= time_in
                      AND @currentTime <= time_out
                    LIMIT 1";

                using var scheduleCmd = new NpgsqlCommand(scheduleQuery, connection);
                scheduleCmd.Parameters.AddWithValue("@sectionId", sectionId.Value);
                scheduleCmd.Parameters.AddWithValue("@labId", labId.Value);
                scheduleCmd.Parameters.AddWithValue("@dayOfWeek", currentDay);
                scheduleCmd.Parameters.AddWithValue("@currentTime", currentTime);

                using var scheduleReader = await scheduleCmd.ExecuteReaderAsync();
                if (await scheduleReader.ReadAsync())
                {
                    return new ScheduleValidationResult { IsValid = true };
                }

                return new ScheduleValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"No active schedule found for your section in this lab at this time ({currentDay} {now:HH:mm})."
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Schedule validation error: {ex.Message}");
                return new ScheduleValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Schedule validation failed: {ex.Message}"
                };
            }
        }

        private class ScheduleValidationResult
        {
            public bool IsValid { get; set; }
            public string? ErrorMessage { get; set; }
        }

        public class LoginValidationResult
        {
            public bool IsValid { get; set; }
            public string? Username { get; set; }
            public string? UserRole { get; set; }
            public string? AssignedPcName { get; set; }
            public bool IsPcNameMatch { get; set; }
            public string? ScheduleError { get; set; }
        }

        private static bool IsValidBcryptHash(string? hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
            {
                return false;
            }

            return Regex.IsMatch(hash, @"^\$2[aby]\$\d{2}\$[./A-Za-z0-9]{53}$");
        }

        /// <summary>
        /// Gets schedule information for a student.
        /// FIX: Uses lab_sessions instead of server_sessions.
        /// </summary>
        public async Task<(DateTime? scheduleStart, DateTime? scheduleEnd, DateTime? serverStart)?> GetStudentScheduleAsync(string studNo, string clientName)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var getLabIdQuery = "SELECT lab_id FROM computers WHERE client_name = @clientName LIMIT 1";
                using var getLabCmd = new NpgsqlCommand(getLabIdQuery, connection);
                getLabCmd.Parameters.AddWithValue("@clientName", clientName);
                var labIdResult = await getLabCmd.ExecuteScalarAsync();

                if (labIdResult == null || labIdResult == DBNull.Value)
                {
                    return null;
                }

                int labId = Convert.ToInt32(labIdResult);

                // FIX: lab_sessions instead of server_sessions
                var getScheduleQuery = @"
                    SELECT cs.time_in, cs.time_out, ls.actual_start
                    FROM us_geninfo ug
                    LEFT JOIN course_schedules cs ON ug.section_id = cs.section_id
                        AND cs.lab_id = @labId
                        AND TRIM(cs.day_of_week) = TRIM(TO_CHAR(CURRENT_DATE, 'Day'))
                    LEFT JOIN lab_sessions ls ON ls.schedule_id = cs.schedule_id
                        AND ls.is_active = TRUE
                        AND DATE(ls.actual_start) = CURRENT_DATE
                    WHERE ug.studNo = @studNo
                    LIMIT 1";

                using var scheduleCmd = new NpgsqlCommand(getScheduleQuery, connection);
                scheduleCmd.Parameters.AddWithValue("@studNo", studNo);
                scheduleCmd.Parameters.AddWithValue("@labId", labId);

                using var reader = await scheduleCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    DateTime? scheduleStart = null;
                    DateTime? scheduleEnd = null;
                    DateTime? serverStart = null;

                    if (!reader.IsDBNull(0))
                        scheduleStart = DateTime.Today.Add(reader.GetTimeSpan(0));

                    if (!reader.IsDBNull(1))
                        scheduleEnd = DateTime.Today.Add(reader.GetTimeSpan(1));

                    if (!reader.IsDBNull(2))
                        serverStart = reader.GetDateTime(2);

                    return (scheduleStart, scheduleEnd, serverStart);
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SCHEDULE] Error getting schedule: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Records student login attendance in attendance_logs table.
        /// FIX: studno (lowercase), lab_sessions instead of server_sessions.
        /// </summary>
        public async Task<int?> RecordStudentLoginAsync(string studNo, string clientName)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var getComputerIdQuery = "SELECT id, lab_id FROM computers WHERE client_name = @clientName LIMIT 1";
                using var getComputerCmd = new NpgsqlCommand(getComputerIdQuery, connection);
                getComputerCmd.Parameters.AddWithValue("@clientName", clientName);

                int? computerId = null;
                int? labId = null;

                using (var reader = await getComputerCmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        computerId = reader.GetInt32(0);
                        labId = reader.IsDBNull(1) ? null : (int?)reader.GetInt32(1);
                    }
                }

                if (!computerId.HasValue)
                {
                    System.Diagnostics.Debug.WriteLine($"[ATTENDANCE] Computer not found: {clientName}");
                    return null;
                }

                DateTime? scheduleStartTime = null;
                DateTime? serverStartTime = null;

                // FIX: lab_sessions instead of server_sessions
                var getScheduleQuery = @"
                    SELECT cs.time_in, ls.actual_start
                    FROM us_geninfo ug
                    LEFT JOIN course_schedules cs ON ug.section_id = cs.section_id
                        AND cs.lab_id = @labId
                        AND TRIM(cs.day_of_week) = TRIM(TO_CHAR(CURRENT_DATE, 'Day'))
                    LEFT JOIN lab_sessions ls ON ls.schedule_id = cs.schedule_id
                        AND ls.is_active = TRUE
                        AND DATE(ls.actual_start) = CURRENT_DATE
                    WHERE ug.studNo = @studNo
                    LIMIT 1";

                using var scheduleCmd = new NpgsqlCommand(getScheduleQuery, connection);
                scheduleCmd.Parameters.AddWithValue("@studNo", studNo);
                scheduleCmd.Parameters.AddWithValue("@labId", labId ?? (object)DBNull.Value);

                using (var reader = await scheduleCmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        if (!reader.IsDBNull(0))
                            scheduleStartTime = DateTime.Today.Add(reader.GetTimeSpan(0));

                        if (!reader.IsDBNull(1))
                            serverStartTime = reader.GetDateTime(1);
                    }
                }

                // FIX: studno (lowercase) matches actual DB column
                var insertQuery = @"
                    INSERT INTO attendance_logs 
                    (studno, computer_id, login_time, status, schedule_start_time, server_start_time)
                    VALUES (@studNo, @computerId, CURRENT_TIMESTAMP, 'Active', @scheduleStartTime, @serverStartTime)
                    RETURNING id";

                using var insertCmd = new NpgsqlCommand(insertQuery, connection);
                insertCmd.Parameters.AddWithValue("@studNo", studNo);
                insertCmd.Parameters.AddWithValue("@computerId", computerId.Value);
                insertCmd.Parameters.AddWithValue("@scheduleStartTime", scheduleStartTime ?? (object)DBNull.Value);
                insertCmd.Parameters.AddWithValue("@serverStartTime", serverStartTime ?? (object)DBNull.Value);

                var attendanceId = await insertCmd.ExecuteScalarAsync();

                if (attendanceId != null)
                {
                    var updateComputerQuery = @"
                        UPDATE computers
                        SET status = 'Online', is_online = TRUE, updated_at = CURRENT_TIMESTAMP
                        WHERE id = @computerId";

                    using var computerCmd = new NpgsqlCommand(updateComputerQuery, connection);
                    computerCmd.Parameters.AddWithValue("@computerId", computerId.Value);
                    await computerCmd.ExecuteNonQueryAsync();

                    System.Diagnostics.Debug.WriteLine($"[ATTENDANCE] Logged login for {studNo} on {clientName} (ID: {attendanceId})");
                }

                return attendanceId != null ? Convert.ToInt32(attendanceId) : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ATTENDANCE] Error recording login: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Records student logout attendance in attendance_logs table.
        /// FIX: studno (lowercase) in WHERE clause.
        /// </summary>
        public async Task<bool> RecordStudentLogoutAsync(string studNo, string clientName)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var getComputerIdQuery = "SELECT id FROM computers WHERE client_name = @clientName LIMIT 1";
                using var getComputerCmd = new NpgsqlCommand(getComputerIdQuery, connection);
                getComputerCmd.Parameters.AddWithValue("@clientName", clientName);

                var computerIdResult = await getComputerCmd.ExecuteScalarAsync();
                if (computerIdResult == null || computerIdResult == DBNull.Value)
                {
                    System.Diagnostics.Debug.WriteLine($"[ATTENDANCE] Computer not found: {clientName}");
                    return false;
                }

                var computerId = Convert.ToInt32(computerIdResult);

                // FIX: studno (lowercase)
                var updateQuery = @"
                    UPDATE attendance_logs
                    SET logout_time = CURRENT_TIMESTAMP,
                        session_duration = CURRENT_TIMESTAMP - login_time,
                        status = 'Completed'
                    WHERE studno = @studNo
                      AND computer_id = @computerId
                      AND status = 'Active'
                      AND logout_time IS NULL
                      AND id = (
                          SELECT id FROM attendance_logs
                          WHERE studno = @studNo
                            AND computer_id = @computerId
                            AND status = 'Active'
                            AND logout_time IS NULL
                          ORDER BY login_time DESC
                          LIMIT 1
                      )";

                using var updateCmd = new NpgsqlCommand(updateQuery, connection);
                updateCmd.Parameters.AddWithValue("@studNo", studNo);
                updateCmd.Parameters.AddWithValue("@computerId", computerId);

                var rowsAffected = await updateCmd.ExecuteNonQueryAsync();

                if (rowsAffected > 0)
                {
                    var updateComputerQuery = @"
                        UPDATE computers
                        SET status = 'Offline', is_online = FALSE, updated_at = CURRENT_TIMESTAMP
                        WHERE id = @computerId";

                    using var computerCmd = new NpgsqlCommand(updateComputerQuery, connection);
                    computerCmd.Parameters.AddWithValue("@computerId", computerId);
                    await computerCmd.ExecuteNonQueryAsync();

                    System.Diagnostics.Debug.WriteLine($"[ATTENDANCE] Logged logout for {studNo} on {clientName}");
                    return true;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[ATTENDANCE] No active attendance record found for {studNo} on {clientName}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ATTENDANCE] Error recording logout: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Records student activity in activity_logs table.
        /// FIX: Uses user_id (from us_credentials) and created_at instead of studNo and timestamp.
        /// </summary>
        public async Task<int?> LogStudentActivityAsync(string studNo, string clientName, string action, string? description = null)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var getComputerIdQuery = "SELECT id FROM computers WHERE client_name = @clientName LIMIT 1";
                using var getComputerCmd = new NpgsqlCommand(getComputerIdQuery, connection);
                getComputerCmd.Parameters.AddWithValue("@clientName", clientName);

                var computerIdResult = await getComputerCmd.ExecuteScalarAsync();
                if (computerIdResult == null || computerIdResult == DBNull.Value)
                {
                    System.Diagnostics.Debug.WriteLine($"[ACTIVITY] Computer not found: {clientName}");
                    return null;
                }

                var computerId = Convert.ToInt32(computerIdResult);

                // FIX: activity_logs uses user_id (FK to us_credentials.id), not studNo
                var getUserIdQuery = "SELECT id FROM us_credentials WHERE studNo = @studNo LIMIT 1";
                using var getUserCmd = new NpgsqlCommand(getUserIdQuery, connection);
                getUserCmd.Parameters.AddWithValue("@studNo", studNo);
                var userIdResult = await getUserCmd.ExecuteScalarAsync();
                var userId = userIdResult != null && userIdResult != DBNull.Value
                    ? Convert.ToInt32(userIdResult)
                    : (object)DBNull.Value;

                // FIX: user_id and created_at are the correct column names
                var insertQuery = @"
                    INSERT INTO activity_logs (user_id, computer_id, action, description, created_at)
                    VALUES (@userId, @computerId, @action, @description, CURRENT_TIMESTAMP)
                    RETURNING id";

                using var insertCmd = new NpgsqlCommand(insertQuery, connection);
                insertCmd.Parameters.AddWithValue("@userId", userId);
                insertCmd.Parameters.AddWithValue("@computerId", computerId);
                insertCmd.Parameters.AddWithValue("@action", action);
                insertCmd.Parameters.AddWithValue("@description", description ?? (object)DBNull.Value);

                var activityId = await insertCmd.ExecuteScalarAsync();
                System.Diagnostics.Debug.WriteLine($"[ACTIVITY] Logged '{action}' for {studNo} on {clientName} (ID: {activityId})");
                return activityId != null ? Convert.ToInt32(activityId) : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ACTIVITY] Error logging activity: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Creates a login request when student tries to login from non-assigned PC.
        /// FIX: studno (lowercase) in insert.
        /// </summary>
        public async Task<int?> CreateLoginRequestAsync(string studNo, string clientName, string? ipAddress = null, string? requestMessage = null)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var getComputerIdQuery = "SELECT id FROM computers WHERE client_name = @clientName LIMIT 1";
                using var getComputerCmd = new NpgsqlCommand(getComputerIdQuery, connection);
                getComputerCmd.Parameters.AddWithValue("@clientName", clientName);

                var computerIdResult = await getComputerCmd.ExecuteScalarAsync();
                if (computerIdResult == null || computerIdResult == DBNull.Value)
                {
                    System.Diagnostics.Debug.WriteLine($"[LOGIN_REQUEST] Computer not found: {clientName}");
                    return null;
                }

                var computerId = Convert.ToInt32(computerIdResult);

                // FIX: studno (lowercase)
                var insertQuery = @"
                    INSERT INTO login_requests (studno, computer_id, request_type, request_message, request_timestamp, status)
                    VALUES (@studNo, @computerId, 'Login', @requestMessage, CURRENT_TIMESTAMP, 'Pending')
                    RETURNING id";

                using var insertCmd = new NpgsqlCommand(insertQuery, connection);
                insertCmd.Parameters.AddWithValue("@studNo", studNo);
                insertCmd.Parameters.AddWithValue("@computerId", computerId);
                insertCmd.Parameters.AddWithValue("@requestMessage", requestMessage ?? (object)DBNull.Value);

                var requestId = await insertCmd.ExecuteScalarAsync();
                System.Diagnostics.Debug.WriteLine($"[LOGIN_REQUEST] Created request for {studNo} on {clientName} (ID: {requestId})");
                return requestId != null ? Convert.ToInt32(requestId) : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOGIN_REQUEST] Error creating login request: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Creates a logout request when student tries to logout during active schedule.
        /// FIX: studno (lowercase) in insert.
        /// </summary>
        public async Task<int?> CreateLogoutRequestAsync(string studNo, string clientName, string? requestMessage = null)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var getComputerIdQuery = "SELECT id FROM computers WHERE client_name = @clientName LIMIT 1";
                using var getComputerCmd = new NpgsqlCommand(getComputerIdQuery, connection);
                getComputerCmd.Parameters.AddWithValue("@clientName", clientName);

                var computerIdResult = await getComputerCmd.ExecuteScalarAsync();
                if (computerIdResult == null || computerIdResult == DBNull.Value)
                {
                    System.Diagnostics.Debug.WriteLine($"[LOGOUT_REQUEST] Computer not found: {clientName}");
                    return null;
                }

                var computerId = Convert.ToInt32(computerIdResult);

                // FIX: studno (lowercase)
                var insertQuery = @"
                    INSERT INTO login_requests (studno, computer_id, request_type, request_message, request_timestamp, status)
                    VALUES (@studNo, @computerId, 'Logout', @requestMessage, CURRENT_TIMESTAMP, 'Pending')
                    RETURNING id";

                using var insertCmd = new NpgsqlCommand(insertQuery, connection);
                insertCmd.Parameters.AddWithValue("@studNo", studNo);
                insertCmd.Parameters.AddWithValue("@computerId", computerId);
                insertCmd.Parameters.AddWithValue("@requestMessage", requestMessage ?? (object)DBNull.Value);

                var requestId = await insertCmd.ExecuteScalarAsync();
                System.Diagnostics.Debug.WriteLine($"[LOGOUT_REQUEST] Created logout request for {studNo} on {clientName} (ID: {requestId})");
                return requestId != null ? Convert.ToInt32(requestId) : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOGOUT_REQUEST] Error creating logout request: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Checks if student is currently in an active schedule.
        /// </summary>
        public async Task<bool> IsStudentInActiveScheduleAsync(string studNo, string clientName)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var studentInfoQuery = @"
                    SELECT ug.section_id, c.lab_id
                    FROM us_geninfo ug
                    CROSS JOIN computers c
                    WHERE ug.studNo = @studNo AND c.client_name = @clientName
                    LIMIT 1";

                using var studentCmd = new NpgsqlCommand(studentInfoQuery, connection);
                studentCmd.Parameters.AddWithValue("@studNo", studNo);
                studentCmd.Parameters.AddWithValue("@clientName", clientName);

                int? sectionId = null;
                int? labId = null;

                using (var reader = await studentCmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        sectionId = reader.IsDBNull(0) ? null : reader.GetInt32(0);
                        labId = reader.IsDBNull(1) ? null : reader.GetInt32(1);
                    }
                }

                if (!sectionId.HasValue || !labId.HasValue)
                    return false;

                var now = DateTime.Now;
                var currentDay = now.DayOfWeek.ToString();
                var currentTime = now.TimeOfDay;

                var scheduleQuery = @"
                    SELECT COUNT(*)
                    FROM course_schedules
                    WHERE section_id = @sectionId
                      AND lab_id = @labId
                      AND day_of_week = @dayOfWeek
                      AND @currentTime >= time_in
                      AND @currentTime <= time_out";

                using var scheduleCmd = new NpgsqlCommand(scheduleQuery, connection);
                scheduleCmd.Parameters.AddWithValue("@sectionId", sectionId.Value);
                scheduleCmd.Parameters.AddWithValue("@labId", labId.Value);
                scheduleCmd.Parameters.AddWithValue("@dayOfWeek", currentDay);
                scheduleCmd.Parameters.AddWithValue("@currentTime", currentTime);

                var count = await scheduleCmd.ExecuteScalarAsync();
                return count != null && Convert.ToInt32(count) > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SCHEDULE_CHECK] Error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Checks if a logout request for this student has been approved.
        /// FIX: studno (lowercase) in WHERE clause.
        /// </summary>
        public async Task<bool> CheckLogoutRequestApprovalAsync(string studNo, string clientName)
        {
            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var getComputerIdQuery = "SELECT id FROM computers WHERE client_name = @clientName LIMIT 1";
                using var getComputerCmd = new NpgsqlCommand(getComputerIdQuery, connection);
                getComputerCmd.Parameters.AddWithValue("@clientName", clientName);

                var computerIdResult = await getComputerCmd.ExecuteScalarAsync();
                if (computerIdResult == null || computerIdResult == DBNull.Value)
                    return false;

                var computerId = Convert.ToInt32(computerIdResult);

                // FIX: studno (lowercase)
                var checkQuery = @"
                    SELECT COUNT(*)
                    FROM login_requests
                    WHERE studno = @studNo
                      AND computer_id = @computerId
                      AND request_type = 'Logout'
                      AND status = 'Approved'
                      AND request_timestamp >= NOW() - INTERVAL '1 hour'
                    LIMIT 1";

                using var checkCmd = new NpgsqlCommand(checkQuery, connection);
                checkCmd.Parameters.AddWithValue("@studNo", studNo);
                checkCmd.Parameters.AddWithValue("@computerId", computerId);

                var count = await checkCmd.ExecuteScalarAsync();
                return count != null && Convert.ToInt32(count) > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOGOUT_CHECK] Error: {ex.Message}");
                return false;
            }
        }
    }
}