using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using LabServerClient.Services;

namespace LabServerClient.ViewModels
{
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

        // ✅ NEW: Attempt tracking properties
        private int _failedAttemptCount = 0;
        private bool _isAccountLocked = false;

        public event Action? LoginSuccess;
        public event Action<string, string>? PcMismatchDetected;
        public event Action? LoginCancelled;

        public LoginViewModel(DatabaseService? databaseService = null)
        {
            _databaseService = databaseService;
            _loginRequestService = new LoginRequestService();
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

        // ✅ NEW: Exposed so LoginWindow.xaml.cs can read directly — no string parsing needed
        public int FailedAttemptCount
        {
            get => _failedAttemptCount;
            private set
            {
                if (_failedAttemptCount != value)
                {
                    _failedAttemptCount = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsAccountLocked
        {
            get => _isAccountLocked;
            private set
            {
                if (_isAccountLocked != value)
                {
                    _isAccountLocked = value;
                    OnPropertyChanged();
                }
            }
        }

        #endregion

        #region Login Logic

        public async Task AttemptLoginAsync()
        {
            if (!ValidateInput())
            {
                LoginCancelled?.Invoke();
                return;
            }

            IsLoginButtonEnabled = false;

            try
            {
                CurrentPcName = PcNameService.GetPcName();

                // ✅ Check lockout BEFORE attempting verification
                if (_databaseService != null)
                {
                    bool isLocked = await _databaseService.IsStudentLockedAsync(Username);
                    if (isLocked)
                    {
                        IsAccountLocked = true;
                        FailedAttemptCount = 0;
                        ErrorMessage = "Your account has been locked.";
                        LoginCancelled?.Invoke();
                        return;
                    }
                }

                var verificationResult = await VerifyLoginAsync();

                if (verificationResult.IsSuccessful && verificationResult.IsPcNameMatch)
                {
                    // ✅ Reset on success
                    FailedAttemptCount = 0;
                    IsAccountLocked = false;

                    if (_databaseService != null)
                        await _databaseService.ResetStudentFailedAttemptsAsync(Username);

                    await CompleteLogin(verificationResult);
                    LoginSuccess?.Invoke();
                }
                else if (!verificationResult.IsPcNameMatch && !string.IsNullOrWhiteSpace(verificationResult.AssignedPcName))
                {
                    // PC mismatch — not a failed attempt, no counter change
                    PcMismatchDetected?.Invoke(Username, verificationResult.AssignedPcName);
                }
                else if (!string.IsNullOrWhiteSpace(verificationResult.ErrorMessage) &&
                         verificationResult.ErrorMessage.Contains("schedule", StringComparison.OrdinalIgnoreCase))
                {
                    // Schedule error — not a failed attempt
                    FailedAttemptCount = 0;
                    IsAccountLocked = false;
                    ErrorMessage = verificationResult.ErrorMessage;
                    LoginCancelled?.Invoke();
                }
                else
                {
                    // ✅ Wrong credentials — increment counter
                    if (_databaseService != null)
                    {
                        bool studentExists = await _databaseService.StudentExistsAsync(Username);
                        if (studentExists)
                        {
                            int failedCount = await _databaseService.IncrementStudentFailedAttemptsAsync(Username);
                            int remaining = MAX_ATTEMPTS - failedCount;

                            if (failedCount >= MAX_ATTEMPTS)
                            {
                                await _databaseService.LockStudentAccountAsync(Username);

                                // ✅ Signal locked — LoginWindow will call ShowLockedError()
                                FailedAttemptCount = MAX_ATTEMPTS;
                                IsAccountLocked = true;
                                ErrorMessage = "Account locked.";
                            }
                            else
                            {
                                // ✅ Signal attempt warning — LoginWindow will call ShowAttemptError()
                                FailedAttemptCount = failedCount;
                                IsAccountLocked = false;
                                ErrorMessage = "Invalid credentials.";
                            }

                            LoginCancelled?.Invoke();
                            return;
                        }
                    }

                    // Unknown username — generic error, no counter
                    FailedAttemptCount = 0;
                    IsAccountLocked = false;
                    ErrorMessage = verificationResult.ErrorMessage ?? "Invalid username or password. Please try again.";
                    LoginCancelled?.Invoke();
                }
            }
            catch (Exception ex)
            {
                FailedAttemptCount = 0;
                IsAccountLocked = false;
                ErrorMessage = $"Login error: {ex.Message}";
                LoginCancelled?.Invoke();
            }
            finally
            {
                IsLoginButtonEnabled = true;
            }
        }

        private const int MAX_ATTEMPTS = 4;

        public async Task<bool> SendLoginRequestAsync()
        {
            try
            {
                if (_databaseService == null)
                {
                    System.Diagnostics.Debug.WriteLine("[LOGIN_REQUEST] No database service available");
                    return false;
                }

                string studNo = Username;
                try
                {
                    var resolved = await ResolveStudNoAsync(Username);
                    if (!string.IsNullOrWhiteSpace(resolved))
                        studNo = resolved;
                }
                catch { }

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
                FailedAttemptCount = 0;
                IsAccountLocked = false;
                ErrorMessage = "Please enter a username.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(Password))
            {
                FailedAttemptCount = 0;
                IsAccountLocked = false;
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

        private async Task CompleteLogin(LoginRequestService.LoginVerificationResult result)
        {
            IsAuthenticated = true;
            UserRole = result.UserRole ?? "Student";

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

            if (_databaseService != null && !string.Equals(UserRole, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    AuthenticatedClientId = await _databaseService.EnsureClientIdAsync(Username, Password, CurrentPcName);

                    if (UserRole.Equals("Student", StringComparison.OrdinalIgnoreCase) ||
                        UserRole.Equals("STUDENT", StringComparison.OrdinalIgnoreCase))
                    {
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
            {
                ErrorMessage = string.Empty;
                FailedAttemptCount = 0;
                IsAccountLocked = false;
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