using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using LabServerClient.Services;

namespace LabServerClient.ViewModels
{
    /// <summary>
    /// ViewModel for the LoginWindow following the MVVM pattern.
    /// Handles all login business logic, PC Name validation, and server communication.
    /// Separates UI logic from service calls and data persistence.
    /// </summary>
    public class LoginViewModel : INotifyPropertyChanged
    {
        private readonly DatabaseService? _databaseService;
        private readonly LoginRequestService _loginRequestService;

        private string _username = string.Empty;
        private string _password = string.Empty;
        private string _errorMessage = string.Empty;
        private bool _isLoginButtonEnabled = true;
        private bool _isAuthenticated = false;
        private string? _authenticatedUsername;
        private string? _userRole;
        private int? _authenticatedClientId;
        private string _currentPcName = string.Empty;

        // Events for UI interaction
        public event Action? LoginSuccess;
        public event Action<string, string>? PcMismatchDetected; // username, assignedPcName
        public event Action? LoginCancelled;

        public LoginViewModel(DatabaseService? databaseService = null)
        {
            _databaseService = databaseService;
            _loginRequestService = new LoginRequestService();

            // Initialize PC Name from registry
            _currentPcName = PcNameService.GetPcName();
        }

        #region Properties

        public string Username
        {
            get => _username;
            set
            {
                if (_username != value)
                {
                    _username = value;
                    OnPropertyChanged();
                    ClearErrorMessage();
                }
            }
        }

        public string Password
        {
            get => _password;
            set
            {
                if (_password != value)
                {
                    _password = value;
                    OnPropertyChanged();
                    ClearErrorMessage();
                }
            }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set
            {
                if (_errorMessage != value)
                {
                    _errorMessage = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsLoginButtonEnabled
        {
            get => _isLoginButtonEnabled;
            set
            {
                if (_isLoginButtonEnabled != value)
                {
                    _isLoginButtonEnabled = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsAuthenticated
        {
            get => _isAuthenticated;
            private set
            {
                if (_isAuthenticated != value)
                {
                    _isAuthenticated = value;
                    OnPropertyChanged();
                }
            }
        }

        public string? AuthenticatedUsername
        {
            get => _authenticatedUsername;
            private set
            {
                if (_authenticatedUsername != value)
                {
                    _authenticatedUsername = value;
                    OnPropertyChanged();
                }
            }
        }

        public string? UserRole
        {
            get => _userRole;
            private set
            {
                if (_userRole != value)
                {
                    _userRole = value;
                    OnPropertyChanged();
                }
            }
        }

        public int? AuthenticatedClientId
        {
            get => _authenticatedClientId;
            private set
            {
                if (_authenticatedClientId != value)
                {
                    _authenticatedClientId = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CurrentPcName
        {
            get => _currentPcName;
            set
            {
                if (_currentPcName != value)
                {
                    _currentPcName = value;
                    OnPropertyChanged();
                }
            }
        }

        #endregion

        #region Login Logic

        public async Task AttemptLoginAsync()
        {
            if (!ValidateInput())
                return;

            IsLoginButtonEnabled = false;

            try
            {
                CurrentPcName = PcNameService.GetPcName();
                System.Diagnostics.Debug.WriteLine($"[LOGIN] Attempting login for user: '{Username}' on PC: '{CurrentPcName}'");

                // TEMPORARY: TCP server check commented out for debugging login issue
                // if (!IsAdminCredentials(Username, Password))
                // {
                //     bool serverAvailable = await CheckTcpServerAvailabilityAsync();
                //     if (!serverAvailable)
                //     {
                //         ErrorMessage = "Server is not running. Please contact your instructor to start the server.";
                //         LoginCancelled?.Invoke();
                //         return;
                //     }
                // }

                var verificationResult = await VerifyLoginAsync();
                System.Diagnostics.Debug.WriteLine($"[LOGIN] Verification result - Success: {verificationResult.IsSuccessful}, PCMatch: {verificationResult.IsPcNameMatch}");

                if (verificationResult.IsSuccessful && verificationResult.IsPcNameMatch)
                {
                    System.Diagnostics.Debug.WriteLine($"[LOGIN] Login successful - completing login process");
                    await CompleteLogin(verificationResult);
                    System.Diagnostics.Debug.WriteLine($"[LOGIN] Invoking LoginSuccess event");
                    LoginSuccess?.Invoke();
                }
                else if (!verificationResult.IsPcNameMatch && !string.IsNullOrWhiteSpace(verificationResult.AssignedPcName))
                {
                    System.Diagnostics.Debug.WriteLine($"[LOGIN] PC mismatch detected");
                    PcMismatchDetected?.Invoke(Username, verificationResult.AssignedPcName);
                }
                else if (!string.IsNullOrWhiteSpace(verificationResult.ErrorMessage))
                {
                    System.Diagnostics.Debug.WriteLine($"[LOGIN] Login failed - {verificationResult.ErrorMessage}");
                    ErrorMessage = verificationResult.ErrorMessage;
                    LoginCancelled?.Invoke();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[LOGIN] Generic login failure");
                    ErrorMessage = "Invalid username or password. Please try again.";
                    LoginCancelled?.Invoke();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOGIN] Exception during login: {ex.Message}");
                ErrorMessage = $"Login error: {ex.Message}";
                LoginCancelled?.Invoke();
            }
            finally
            {
                IsLoginButtonEnabled = true;
            }
        }

        public async Task<bool> SendLoginRequestAsync()
        {
            try
            {
                if (_databaseService == null)
                {
                    System.Diagnostics.Debug.WriteLine("[LOGIN_REQUEST] No database service available");
                    return false;
                }

                // Resolve actual studNo first
                string studNo = Username;
                try
                {
                    var resolved = await ResolveStudNoAsync(Username);
                    if (!string.IsNullOrWhiteSpace(resolved))
                        studNo = resolved;
                }
                catch { /* fallback to Username */ }

                var message = $"Student '{studNo}' requesting login access from PC '{CurrentPcName}'";

                var requestId = await _databaseService.CreateLoginRequestAsync(
                    studNo: studNo,
                    clientName: CurrentPcName,
                    ipAddress: null,
                    requestMessage: message
                );

                if (requestId.HasValue)
                {
                    System.Diagnostics.Debug.WriteLine($"[LOGIN_REQUEST] Request created: ID={requestId.Value}, studNo={studNo}, PC={CurrentPcName}");
                    return true;
                }

                System.Diagnostics.Debug.WriteLine("[LOGIN_REQUEST] CreateLoginRequestAsync returned null");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOGIN_REQUEST] Exception: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Private Helper Methods

        private bool ValidateInput()
        {
            if (string.IsNullOrWhiteSpace(Username))
            {
                ErrorMessage = "Please enter a username.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(Password))
            {
                ErrorMessage = "Please enter a password.";
                return false;
            }

            return true;
        }

        private async Task<LoginRequestService.LoginVerificationResult> VerifyLoginAsync()
        {
            System.Diagnostics.Debug.WriteLine($"[VERIFY] Starting login verification for user: '{Username}'");

            var result = await _loginRequestService.VerifyLoginAsync(Username, Password, CurrentPcName);

            System.Diagnostics.Debug.WriteLine($"[VERIFY] Server result - Success: {result.IsSuccessful}, PCMatch: {result.IsPcNameMatch}");

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage) &&
                result.ErrorMessage.Contains("not yet implemented", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"[VERIFY] Server not implemented, skipping to database");
            }
            else if (result.IsSuccessful || result.IsPcNameMatch == false)
            {
                System.Diagnostics.Debug.WriteLine($"[VERIFY] Using server result");
                return result;
            }

            if (_databaseService != null)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[VERIFY] Attempting database verification with PC Name check");

                    var loginResult = await _databaseService.ValidateLoginWithPcNameAsync(Username, Password, CurrentPcName);
                    if (loginResult != null && loginResult.IsValid)
                    {
                        System.Diagnostics.Debug.WriteLine($"[VERIFY] Client validation passed - PC Match: {loginResult.IsPcNameMatch}");

                        if (loginResult.IsPcNameMatch)
                        {
                            System.Diagnostics.Debug.WriteLine($"[VERIFY] PC Name matches!");
                            return new LoginRequestService.LoginVerificationResult
                            {
                                IsSuccessful = true,
                                IsPcNameMatch = true,
                                UserRole = loginResult.UserRole ?? "Student"
                            };
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[VERIFY] PC Name mismatch - Current: {CurrentPcName}, Assigned: {loginResult.AssignedPcName}");

                            // Check if admin already approved a Login request for this student on this PC
                            string studNoToCheck = Username;
                            try
                            {
                                var resolved = await ResolveStudNoAsync(Username);
                                if (!string.IsNullOrWhiteSpace(resolved))
                                    studNoToCheck = resolved;
                            }
                            catch { }

                            bool hasApproved = await _databaseService.CheckApprovedLoginRequestAsync(
                                studNoToCheck, CurrentPcName);

                            if (hasApproved)
                            {
                                System.Diagnostics.Debug.WriteLine($"[VERIFY] Approved login request found — allowing login");
                                return new LoginRequestService.LoginVerificationResult
                                {
                                    IsSuccessful = true,
                                    IsPcNameMatch = true,
                                    UserRole = loginResult.UserRole ?? "Student"
                                };
                            }

                            return new LoginRequestService.LoginVerificationResult
                            {
                                IsSuccessful = false,
                                IsPcNameMatch = false,
                                AssignedPcName = loginResult.AssignedPcName,
                                ErrorMessage = "PC Name does not match"
                            };
                        }
                    }
                    else if (loginResult != null && !loginResult.IsValid && !string.IsNullOrWhiteSpace(loginResult.ScheduleError))
                    {
                        System.Diagnostics.Debug.WriteLine($"[VERIFY] Schedule validation failed: {loginResult.ScheduleError}");
                        return new LoginRequestService.LoginVerificationResult
                        {
                            IsSuccessful = false,
                            IsPcNameMatch = true,
                            ErrorMessage = loginResult.ScheduleError
                        };
                    }

                    System.Diagnostics.Debug.WriteLine($"[VERIFY] Client validation failed");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[VERIFY] Database error: {ex.Message}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[VERIFY] No database service available");
            }

            System.Diagnostics.Debug.WriteLine($"[VERIFY] Checking hardcoded credentials");

            if (Username == "admin" && Password == "admin123")
            {
                System.Diagnostics.Debug.WriteLine($"[VERIFY] Hardcoded admin match!");
                return new LoginRequestService.LoginVerificationResult
                {
                    IsSuccessful = true,
                    IsPcNameMatch = true,
                    UserRole = "Admin"
                };
            }

            if ((Username == "student" && Password == "student123") ||
                (Username == "client" && Password == "client123"))
            {
                System.Diagnostics.Debug.WriteLine($"[VERIFY] Hardcoded student/client match!");
                return new LoginRequestService.LoginVerificationResult
                {
                    IsSuccessful = true,
                    IsPcNameMatch = true,
                    UserRole = "Student"
                };
            }

            System.Diagnostics.Debug.WriteLine($"[VERIFY] All verification attempts failed");
            return new LoginRequestService.LoginVerificationResult
            {
                IsSuccessful = false,
                IsPcNameMatch = false,
                ErrorMessage = "Invalid username or password."
            };
        }

        /// <summary>
        /// Completes the login process.
        /// FIX: Resolves the actual studNo from the database so the timer works correctly.
        /// The timer uses studNo to query GetStudentScheduleAsync — if the student logged in
        /// using their username (not studNo), the schedule lookup would fail and show 00:00:00.
        /// </summary>
        private async Task CompleteLogin(LoginRequestService.LoginVerificationResult result)
        {
            IsAuthenticated = true;
            UserRole = result.UserRole ?? "Student";

            // ── FIX: Resolve actual studNo for non-admin users ──────────────────
            // GetStudentScheduleAsync queries us_geninfo.studNo directly.
            // If the student logged in with a username alias, we need the real studNo.
            if (_databaseService != null &&
                !string.Equals(UserRole, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var resolvedStudNo = await ResolveStudNoAsync(Username);
                    AuthenticatedUsername = resolvedStudNo ?? Username;
                    System.Diagnostics.Debug.WriteLine(
                        $"[LOGIN] Resolved studNo: '{resolvedStudNo}' for username: '{Username}'");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LOGIN] ResolveStudNo failed: {ex.Message}");
                    AuthenticatedUsername = Username;
                }
            }
            else
            {
                AuthenticatedUsername = Username;
            }
            // ────────────────────────────────────────────────────────────────────

            if (_databaseService != null && !string.Equals(UserRole, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    AuthenticatedClientId = await _databaseService.EnsureClientIdAsync(Username, Password, CurrentPcName);

                    if (UserRole.Equals("Student", StringComparison.OrdinalIgnoreCase) ||
                        UserRole.Equals("STUDENT", StringComparison.OrdinalIgnoreCase))
                    {
                        // Use resolved studNo for attendance recording
                        var studNoForAttendance = AuthenticatedUsername ?? Username;
                        await _databaseService.RecordStudentLoginAsync(studNoForAttendance, CurrentPcName);
                        System.Diagnostics.Debug.WriteLine($"[LOGIN] Attendance recorded for {studNoForAttendance} on {CurrentPcName}");

                        await _databaseService.LogStudentActivityAsync(studNoForAttendance, CurrentPcName, "Login", "Student logged in successfully");
                        System.Diagnostics.Debug.WriteLine($"[LOGIN] Activity logged for {studNoForAttendance}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error resolving client ID: {ex.Message}");
                    AuthenticatedClientId = null;
                }
            }

            System.Diagnostics.Debug.WriteLine($"[LOGIN] Clearing password after successful login");
            Password = string.Empty;
        }

        /// <summary>
        /// Resolves the actual studNo from us_credentials given a username or studNo input.
        /// This ensures the session timer uses the correct studNo for schedule lookups.
        /// </summary>
        private async Task<string?> ResolveStudNoAsync(string usernameOrStudNo)
        {
            if (_databaseService == null) return null;

            try
            {
                using var connection = new Npgsql.NpgsqlConnection(_databaseService.ConnectionString);
                await connection.OpenAsync();

                using var cmd = new Npgsql.NpgsqlCommand(@"
                    SELECT studNo FROM us_credentials 
                    WHERE (username = @value OR studNo = @value)
                    AND studNo IS NOT NULL
                    LIMIT 1", connection);
                cmd.Parameters.AddWithValue("@value", usernameOrStudNo);

                var result = await cmd.ExecuteScalarAsync();
                return result == null || result == DBNull.Value ? null : result.ToString();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOGIN] ResolveStudNo error: {ex.Message}");
                return null;
            }
        }

        private void ClearErrorMessage()
        {
            if (!string.IsNullOrEmpty(ErrorMessage))
                ErrorMessage = string.Empty;
        }

        private bool IsAdminCredentials(string username, string password)
        {
            return username == "admin" && password == "admin123";
        }

        private async Task<bool> CheckTcpServerAvailabilityAsync()
        {
            try
            {
                string serverIp = "192.168.1.100";
                try
                {
                    var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\LabServerClient");
                    if (key != null)
                    {
                        serverIp = key.GetValue("ServerIP", "192.168.1.100")?.ToString() ?? "192.168.1.100";
                        key.Close();
                    }
                }
                catch
                {
                    // Use default if registry read fails
                }

                using var testClient = new System.Net.Sockets.TcpClient();
                var connectTask = testClient.ConnectAsync(serverIp, 9000);
                var timeoutTask = Task.Delay(2000);

                var completedTask = await Task.WhenAny(connectTask, timeoutTask);

                if (completedTask == connectTask && testClient.Connected)
                {
                    testClient.Close();
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}