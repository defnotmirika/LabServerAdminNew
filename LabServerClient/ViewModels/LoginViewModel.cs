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

        /// <summary>
        /// Gets or sets the username entered in the login form.
        /// </summary>
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

        /// <summary>
        /// Gets or sets the password entered in the login form.
        /// </summary>
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

        /// <summary>
        /// Gets or sets the error message to display to the user.
        /// </summary>
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

        /// <summary>
        /// Gets or sets whether the login button is enabled.
        /// Disabled during authentication to prevent multiple submissions.
        /// </summary>
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

        /// <summary>
        /// Gets whether the user is authenticated.
        /// </summary>
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

        /// <summary>
        /// Gets the authenticated username.
        /// </summary>
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

        /// <summary>
        /// Gets the user role (Admin, Student, Teacher, etc.).
        /// </summary>
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

        /// <summary>
        /// Gets the authenticated client ID.
        /// </summary>
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

        /// <summary>
        /// Gets the current PC Name from registry.
        /// </summary>
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

        /// <summary>
        /// Validates input and attempts login with PC Name verification.
        /// Flow:
        /// 1. Validate username and password are not empty
        /// 2. Check if TCP server is running (for non-admin users)
        /// 3. Read PC Name from registry
        /// 4. Send credentials and PC Name to server for verification
        /// 5. If PC match: proceed with login
        /// 6. If PC mismatch: show dialog asking user if they want to request access
        /// 7. If credentials invalid: show error message
        /// </summary>
        public async Task AttemptLoginAsync()
        {
            // Validate input
            if (!ValidateInput())
            {
                return;
            }

            // Disable login button during authentication
            IsLoginButtonEnabled = false;

            try
            {
                // Get current PC Name from registry
                CurrentPcName = PcNameService.GetPcName();
                System.Diagnostics.Debug.WriteLine($"[LOGIN] Attempting login for user: '{Username}' on PC: '{CurrentPcName}'");

                // For non-admin users, check if TCP server is running first
                if (!IsAdminCredentials(Username, Password))
                {
                    System.Diagnostics.Debug.WriteLine($"[LOGIN] Non-admin login detected - checking TCP server availability");
                    bool serverAvailable = await CheckTcpServerAvailabilityAsync();
                    
                    if (!serverAvailable)
                    {
                        System.Diagnostics.Debug.WriteLine($"[LOGIN] TCP server not available - blocking login");
                        ErrorMessage = "Server is not running. Please contact your instructor to start the server.";
                        LoginCancelled?.Invoke();
                        return;
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"[LOGIN] TCP server is available - proceeding with login");
                }

                // Attempt login verification with server
                // (or local database if server is unavailable)
                var verificationResult = await VerifyLoginAsync();
                System.Diagnostics.Debug.WriteLine($"[LOGIN] Verification result - Success: {verificationResult.IsSuccessful}, PCMatch: {verificationResult.IsPcNameMatch}");

                if (verificationResult.IsSuccessful && verificationResult.IsPcNameMatch)
                {
                    // Login successful with matching PC
                    System.Diagnostics.Debug.WriteLine($"[LOGIN] Login successful - completing login process");
                    await CompleteLogin(verificationResult);
                    System.Diagnostics.Debug.WriteLine($"[LOGIN] Invoking LoginSuccess event");
                    LoginSuccess?.Invoke();
                }
                else if (!verificationResult.IsPcNameMatch && !string.IsNullOrWhiteSpace(verificationResult.AssignedPcName))
                {
                    // PC mismatch detected - ask user if they want to request access
                    System.Diagnostics.Debug.WriteLine($"[LOGIN] PC mismatch detected");
                    PcMismatchDetected?.Invoke(Username, verificationResult.AssignedPcName);
                    LoginCancelled?.Invoke();
                }
                else if (!string.IsNullOrWhiteSpace(verificationResult.ErrorMessage))
                {
                    // Login failed with specific error
                    System.Diagnostics.Debug.WriteLine($"[LOGIN] Login failed - {verificationResult.ErrorMessage}");
                    ErrorMessage = verificationResult.ErrorMessage;
                    // Don't clear password - let user retry
                    LoginCancelled?.Invoke();
                }
                else
                {
                    // Generic failure
                    System.Diagnostics.Debug.WriteLine($"[LOGIN] Generic login failure");
                    ErrorMessage = "Invalid username or password. Please try again.";
                    // Don't clear password - let user retry
                    LoginCancelled?.Invoke();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOGIN] Exception during login: {ex.Message}");
                ErrorMessage = $"Login error: {ex.Message}";
                // Don't clear password - let user retry
                LoginCancelled?.Invoke();
            }
            finally
            {
                IsLoginButtonEnabled = true;
            }
        }

        /// <summary>
        /// Attempts to send a login request to the admin when PC mismatch is detected.
        /// This allows users to request access from their current PC.
        /// </summary>
        /// <returns>True if request was sent successfully; false otherwise.</returns>
        public async Task<bool> SendLoginRequestAsync()
        {
            try
            {
                if (_databaseService == null)
                {
                    System.Diagnostics.Debug.WriteLine("[LOGIN_REQUEST] No database service available");
                    return false;
                }

                // Create login request using new schema (studNo, computer_id)
                var requestId = await _databaseService.CreateLoginRequestAsync(
                    studNo: Username,
                    clientName: CurrentPcName,
                    ipAddress: null,
                    requestMessage: $"Student '{Username}' requesting access from PC '{CurrentPcName}'"
                );

                if (requestId.HasValue)
                {
                    System.Diagnostics.Debug.WriteLine($"[LOGIN_REQUEST] Request created with ID: {requestId.Value}");
                    return true;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[LOGIN_REQUEST] Failed to create request");
                    return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOGIN_REQUEST] Error sending login request: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Validates username and password input.
        /// </summary>
        /// <returns>True if valid; false otherwise.</returns>
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

        /// <summary>
        /// Verifies login credentials using both server API (if available) and local database.
        /// Falls back to hardcoded credentials for testing.
        /// </summary>
        /// <returns>LoginVerificationResult with verification details.</returns>
        private async Task<LoginRequestService.LoginVerificationResult> VerifyLoginAsync()
        {
            System.Diagnostics.Debug.WriteLine($"[VERIFY] Starting login verification for user: '{Username}'");
            
            // First, attempt server verification if implemented
            var result = await _loginRequestService.VerifyLoginAsync(Username, Password, CurrentPcName);
            
            System.Diagnostics.Debug.WriteLine($"[VERIFY] Server result - Success: {result.IsSuccessful}, PCMatch: {result.IsPcNameMatch}");
            
            // Only use server result if it's not the "not implemented" placeholder
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage) && 
                result.ErrorMessage.Contains("not yet implemented", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"[VERIFY] Server not implemented, skipping to database");
            }
            else if (result.IsSuccessful || result.IsPcNameMatch == false)
            {
                // Server returned a definitive answer
                System.Diagnostics.Debug.WriteLine($"[VERIFY] Using server result");
                return result;
            }

            // Fall back to local database verification
            if (_databaseService != null)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[VERIFY] Attempting database verification with PC Name check");
                    
                    // Query clients table with PC Name validation
                    var loginResult = await _databaseService.ValidateLoginWithPcNameAsync(Username, Password, CurrentPcName);
                    if (loginResult != null && loginResult.IsValid)
                    {
                        System.Diagnostics.Debug.WriteLine($"[VERIFY] Client validation passed - PC Match: {loginResult.IsPcNameMatch}");
                        
                        // Check PC Name match
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
                            // PC mismatch
                            System.Diagnostics.Debug.WriteLine($"[VERIFY] PC Name mismatch detected - Current: {CurrentPcName}, Assigned: {loginResult.AssignedPcName}");
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
                        // Schedule validation failed
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

            // Final fallback: hardcoded credentials for testing
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

            // All verification attempts failed
            System.Diagnostics.Debug.WriteLine($"[VERIFY] All verification attempts failed");
            return new LoginRequestService.LoginVerificationResult
            {
                IsSuccessful = false,
                IsPcNameMatch = false,
                ErrorMessage = "Invalid username or password."
            };
        }

        /// <summary>
        /// Completes the login process by setting authenticated properties.
        /// </summary>
        private async Task CompleteLogin(LoginRequestService.LoginVerificationResult result)
        {
            IsAuthenticated = true;
            AuthenticatedUsername = Username;
            UserRole = result.UserRole ?? "Student";

            // Resolve client ID for non-admin users when DB is available
            if (_databaseService != null && !string.Equals(UserRole, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    AuthenticatedClientId = await _databaseService.EnsureClientIdAsync(Username, Password, CurrentPcName);
                    
                    // Record attendance for student logins
                    if (UserRole.Equals("Student", StringComparison.OrdinalIgnoreCase) || 
                        UserRole.Equals("STUDENT", StringComparison.OrdinalIgnoreCase))
                    {
                        await _databaseService.RecordStudentLoginAsync(Username, CurrentPcName);
                        System.Diagnostics.Debug.WriteLine($"[LOGIN] Attendance recorded for {Username} on {CurrentPcName}");

                        // Log login activity
                        await _databaseService.LogStudentActivityAsync(Username, CurrentPcName, "Login", $"Student logged in successfully");
                        System.Diagnostics.Debug.WriteLine($"[LOGIN] Activity logged for {Username}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error resolving client ID: {ex.Message}");
                    AuthenticatedClientId = null;
                }
            }

            // Clear sensitive data AFTER all operations complete
            System.Diagnostics.Debug.WriteLine($"[LOGIN] Clearing password after successful login");
            Password = string.Empty;
        }

        /// <summary>
        /// Clears the error message.
        /// </summary>
        private void ClearErrorMessage()
        {
            if (!string.IsNullOrEmpty(ErrorMessage))
            {
                ErrorMessage = string.Empty;
            }
        }

        /// <summary>
        /// Checks if credentials are for admin account.
        /// </summary>
        private bool IsAdminCredentials(string username, string password)
        {
            return username == "admin" && password == "admin123";
        }

        /// <summary>
        /// Checks if TCP server is available by attempting a connection.
        /// </summary>
        private async Task<bool> CheckTcpServerAvailabilityAsync()
        {
            try
            {
                // Get server IP from registry
                string serverIp = "192.168.1.100"; // Default
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

                // Try to connect to TCP server with 2 second timeout
                using var testClient = new System.Net.Sockets.TcpClient();
                var connectTask = testClient.ConnectAsync(serverIp, 9000);
                var timeoutTask = Task.Delay(2000);

                var completedTask = await Task.WhenAny(connectTask, timeoutTask);

                if (completedTask == connectTask && testClient.Connected)
                {
                    // Server is reachable
                    testClient.Close();
                    return true;
                }

                // Timeout or connection failed
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
