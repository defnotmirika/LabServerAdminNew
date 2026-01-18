using System;
using System.Threading.Tasks;

namespace LabServerClient.Services
{
    /// <summary>
    /// Service for handling login-related requests to the server.
    /// This service provides placeholder methods for server API calls that should be
    /// implemented with actual HTTP/TCP communication logic.
    /// </summary>
    public class LoginRequestService
    {
        /// <summary>
        /// Represents the result of a login verification request.
        /// </summary>
        public class LoginVerificationResult
        {
            /// <summary>
            /// Indicates if the login was successful.
            /// </summary>
            public bool IsSuccessful { get; set; }

            /// <summary>
            /// Indicates if the PC Name matches the assigned one.
            /// </summary>
            public bool IsPcNameMatch { get; set; }

            /// <summary>
            /// The assigned PC Name for this user (from server).
            /// Useful for PC mismatch scenarios.
            /// </summary>
            public string? AssignedPcName { get; set; }

            /// <summary>
            /// Error message if verification failed.
            /// </summary>
            public string? ErrorMessage { get; set; }

            /// <summary>
            /// User role returned by server (e.g., "Admin", "Student", "Teacher").
            /// </summary>
            public string? UserRole { get; set; }
        }

        /// <summary>
        /// Represents a login request to be sent to the admin.
        /// </summary>
        public class LoginRequestData
        {
            /// <summary>
            /// The username attempting to log in.
            /// </summary>
            public string? Username { get; set; }

            /// <summary>
            /// The PC Name of the current machine.
            /// </summary>
            public string? CurrentPcName { get; set; }

            /// <summary>
            /// The assigned PC Name (if known).
            /// </summary>
            public string? AssignedPcName { get; set; }

            /// <summary>
            /// Timestamp of the login request.
            /// </summary>
            public DateTime RequestTimestamp { get; set; }

            /// <summary>
            /// Additional notes or reason for the request.
            /// </summary>
            public string? Notes { get; set; }
        }

        /// <summary>
        /// Placeholder method for verifying login credentials and PC Name with the server.
        /// 
        /// Implementation flow:
        /// 1. Send username, password, and current PC Name to server
        /// 2. Server validates credentials and checks PC Name assignment
        /// 3. Server returns success/failure status with assigned PC Name if applicable
        /// 4. Handle PC mismatch scenario where credentials are valid but PC doesn't match
        /// </summary>
        /// <param name="username">The username to verify.</param>
        /// <param name="password">The password to verify.</param>
        /// <param name="currentPcName">The current PC Name from registry/system.</param>
        /// <returns>LoginVerificationResult containing verification details.</returns>
        public async Task<LoginVerificationResult> VerifyLoginAsync(string username, string password, string currentPcName)
        {
            // TODO: IMPLEMENTATION REQUIRED
            // This method should make an API call to the server (HTTP REST or TCP socket)
            // Pseudocode for implementation:
            //
            // try:
            //   - Create authentication payload with username, password, currentPcName
            //   - Send to server endpoint /api/auth/verify-login or via TCP socket
            //   - Parse server response
            //   - Return LoginVerificationResult with:
            //     * IsSuccessful: true if credentials valid AND PC matches
            //     * IsPcNameMatch: true if current PC name matches assigned
            //     * AssignedPcName: PC name from server database
            //     * UserRole: role from server database
            //   - If PC mismatch: IsSuccessful=false, IsPcNameMatch=false, AssignedPcName=actual value
            //   - If credential invalid: IsSuccessful=false, set ErrorMessage
            // catch:
            //   - Log error and return failure result

            return await Task.FromResult(new LoginVerificationResult
            {
                IsSuccessful = false,
                IsPcNameMatch = false,
                ErrorMessage = "Server verification not yet implemented"
            });
        }

        /// <summary>
        /// Placeholder method for sending a PC mismatch request to the admin.
        /// 
        /// Implementation flow:
        /// 1. Package login request data with username, PC names, and timestamp
        /// 2. Send to server's admin notification system
        /// 3. Admin can then approve/deny the request
        /// 4. Return confirmation that request was received
        /// </summary>
        /// <param name="requestData">The login request data to send.</param>
        /// <returns>True if request was successfully sent to server; false otherwise.</returns>
        public async Task<bool> SendLoginRequestToAdminAsync(LoginRequestData requestData)
        {
            // TODO: IMPLEMENTATION REQUIRED
            // This method should notify the admin/server about the PC mismatch request
            // Pseudocode for implementation:
            //
            // try:
            //   - Validate requestData (non-null username, PC names, timestamp)
            //   - Create request payload with user details and timestamp
            //   - Send to server endpoint /api/auth/request-access or via TCP socket
            //   - Server stores request for admin review
            //   - Return true on success
            // catch:
            //   - Log error
            //   - Return false

            return await Task.FromResult(false);
        }

        /// <summary>
        /// Placeholder method for checking if a user has a pending or approved login request.
        /// This could be used to provide status feedback to the user.
        /// </summary>
        /// <param name="username">The username to check.</param>
        /// <returns>True if user has an approved request for current PC; false otherwise.</returns>
        public async Task<bool> HasApprovedAccessAsync(string username)
        {
            // TODO: IMPLEMENTATION REQUIRED
            // This method should query the server to check if user has approved access
            // for the current PC.
            //
            // Pseudocode for implementation:
            //   - Query server for approved access records for this user
            //   - Check if current PC name is in approved list
            //   - Return true/false based on result

            return await Task.FromResult(false);
        }
    }
}
