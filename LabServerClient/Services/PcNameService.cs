using System;
using Microsoft.Win32;

namespace LabServerClient.Services
{
    /// <summary>
    /// Service for safely reading the PC Name from Windows registry.
    /// Provides a wrapper around registry operations with proper error handling.
    /// </summary>
    public class PcNameService
    {
        /// <summary>
        /// Reads the PC Name from Windows registry.
        /// First attempts to read from the application's custom registry location,
        /// then falls back to the system's computer name.
        /// </summary>
        /// <returns>The PC Name as a string. Never returns null or empty.</returns>
        public static string GetPcName()
        {
            try
            {
                // First, try to read from application's custom registry location
                var customPcName = ReadCustomRegistryPcName();
                if (!string.IsNullOrWhiteSpace(customPcName))
                {
                    return customPcName;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reading custom registry PC name: {ex.Message}");
            }

            // Fallback to Windows system computer name
            try
            {
                var systemPcName = Environment.MachineName;
                if (!string.IsNullOrWhiteSpace(systemPcName))
                {
                    return systemPcName;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reading system machine name: {ex.Message}");
            }

            // Last resort: return a default value
            return "UNKNOWN_PC";
        }

        /// <summary>
        /// Reads the PC Name from the application's registry location.
        /// </summary>
        /// <returns>PC Name if found; null or empty otherwise.</returns>
        private static string? ReadCustomRegistryPcName()
        {
            RegistryKey? key = null;
            try
            {
                key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\LabServerClient", writable: false);
                if (key != null)
                {
                    var pcName = key.GetValue("PCName")?.ToString();
                    return pcName;
                }
            }
            finally
            {
                key?.Close();
            }

            return null;
        }

        /// <summary>
        /// Saves the PC Name to the application's registry location.
        /// Used to persist the assigned PC Name for future login attempts.
        /// </summary>
        /// <param name="pcName">The PC Name to save.</param>
        /// <returns>True if successful; false otherwise.</returns>
        public static bool SavePcName(string pcName)
        {
            if (string.IsNullOrWhiteSpace(pcName))
            {
                return false;
            }

            RegistryKey? key = null;
            try
            {
                key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\LabServerClient", writable: true);
                if (key != null)
                {
                    key.SetValue("PCName", pcName, RegistryValueKind.String);
                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving PC name to registry: {ex.Message}");
                return false;
            }
            finally
            {
                key?.Close();
            }

            return false;
        }

        /// <summary>
        /// Validates if a given PC Name matches the current system's PC Name.
        /// </summary>
        /// <param name="assignedPcName">The assigned PC Name from the server.</param>
        /// <returns>True if the assigned PC Name matches the current system; false otherwise.</returns>
        public static bool IsPcNameMatch(string assignedPcName)
        {
            if (string.IsNullOrWhiteSpace(assignedPcName))
            {
                return false;
            }

            var currentPcName = GetPcName();
            return string.Equals(currentPcName, assignedPcName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
