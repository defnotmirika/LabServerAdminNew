using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace LabServerAdmin.Services
{
    [SupportedOSPlatform("windows")]
    public class VoiceRecognitionService : IDisposable
    {
        private readonly IConfiguration _configuration;
        private readonly object _processLock = new();
        private Process? _listenerProcess;
        private bool _isListening;
        private string? _currentUser;
        private string? _cachedScriptPath;

        public event EventHandler<VoiceCommandEventArgs>? VoiceCommandRecognized;
        public event EventHandler<string>? RecognitionError;
        public event EventHandler<string>? VoiceRejected;

        public VoiceRecognitionService(
            TcpServerService tcpServer,
            WakeOnLanService wakeOnLan,
            IConfiguration configuration,
            VoiceSpeakerService speakerService)
        {
            _configuration = configuration;
        }

        public async Task<bool> StartListeningAsync()
        {
            lock (_processLock)
            {
                if (_isListening)
                {
                    return true;
                }
            }

            var scriptPath = _cachedScriptPath ??= ResolveVoiceListenerPath();
            if (scriptPath == null)
            {
                RecognitionError?.Invoke(this, "main.py was not found.");
                return false;
            }

            var process = await Task.Run(() => StartPythonProcess(scriptPath));
            if (process == null)
            {
                RecognitionError?.Invoke(this, "Python runtime was not found. Install Python or the py launcher.");
                return false;
            }

            lock (_processLock)
            {
                _listenerProcess = process;
                _listenerProcess.OutputDataReceived += OnOutputDataReceived;
                _listenerProcess.ErrorDataReceived += OnErrorDataReceived;
                _listenerProcess.Exited += OnProcessExited;
                _listenerProcess.BeginOutputReadLine();
                _listenerProcess.BeginErrorReadLine();
                _isListening = true;
            }

            RecognitionError?.Invoke(this, "Python voice engine started.");
            return true;
        }

        public void StopListening()
        {
            Process? processToStop;

            lock (_processLock)
            {
                processToStop = _listenerProcess;
                _listenerProcess = null;
                _isListening = false;
            }

            if (processToStop == null)
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    if (!processToStop.HasExited)
                    {
                        processToStop.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }
                finally
                {
                    DetachAndDisposeProcess(processToStop);
                }
            });
        }

        public void SetCurrentUser(string? username)
        {
            _currentUser = username;
        }

        public void Dispose()
        {
            StopListening();
        }

        private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(e.Data);
                var root = document.RootElement;

                if (root.TryGetProperty("status", out var statusElement))
                {
                    var status = statusElement.GetString();
                    switch (status)
                    {
                        case "listening":
                            return;
                        case "stopped":
                            RecognitionError?.Invoke(this, "Python voice engine stopped.");
                            return;
                        case "error":
                            var reason = root.TryGetProperty("reason", out var reasonElement)
                                ? reasonElement.GetString()
                                : "unknown_error";
                            VoiceRejected?.Invoke(this, $"Voice engine error: {reason}");
                            return;
                    }
                }

                if (root.TryGetProperty("accepted", out var acceptedElement))
                {
                    var accepted = acceptedElement.ValueKind == JsonValueKind.True;
                    var speaker = root.TryGetProperty("speaker", out var speakerElement)
                        ? speakerElement.GetString()
                        : null;

                    if (!accepted)
                    {
                        var reason = root.TryGetProperty("reason", out var reasonElement)
                            ? reasonElement.GetString()
                            : "auth_failed";
                        VoiceRejected?.Invoke(this, $"Unknown voice detected ({reason}). Command blocked.");
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(_currentUser) &&
                        !string.Equals(_currentUser, speaker, StringComparison.OrdinalIgnoreCase))
                    {
                        VoiceRejected?.Invoke(this, $"Voice mismatch detected. Expected {_currentUser}, heard {speaker}. Command blocked.");
                        return;
                    }

                    VoiceCommandRecognized?.Invoke(this, new VoiceCommandEventArgs("python", speaker ?? "accepted"));
                    return;
                }

                RecognitionError?.Invoke(this, e.Data);
            }
            catch
            {
                RecognitionError?.Invoke(this, e.Data);
            }
        }

        private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                RecognitionError?.Invoke(this, e.Data);
            }
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            var process = sender as Process;
            lock (_processLock)
            {
                if (ReferenceEquals(_listenerProcess, process))
                {
                    _listenerProcess = null;
                }
                _isListening = false;
            }

            RecognitionError?.Invoke(this, "Python voice engine exited.");

            if (process != null)
            {
                DetachAndDisposeProcess(process);
            }
        }

        private void DetachAndDisposeProcess(Process process)
        {
            process.OutputDataReceived -= OnOutputDataReceived;
            process.ErrorDataReceived -= OnErrorDataReceived;
            process.Exited -= OnProcessExited;
            process.Dispose();
        }

        private Process? StartPythonProcess(string scriptPath)
        {
            var workingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory;

            // ── FIX: Pass current user name to Python listener for targeted authentication ──
            var nameArg = string.IsNullOrWhiteSpace(_currentUser)
                ? ""
                : $" --name \"{_currentUser}\"";

            // Try .exe first (compiled voice listener) - FASTEST
            var exePath = Path.ChangeExtension(scriptPath, ".exe");
            if (File.Exists(exePath))
            {
                var exeProcess = TryStartProcess(exePath, $"--mode listener{nameArg}", workingDirectory);
                if (exeProcess != null)
                {
                    return exeProcess;
                }
            }

            // Fallback to Python runtime (for development)
            var configuredPython = _configuration["VoiceRecognition:PythonExecutable"];

            // Try configured Python first (fastest if set)
            if (!string.IsNullOrWhiteSpace(configuredPython))
            {
                var configuredProcess = TryStartProcess(configuredPython, $"\"{scriptPath}\" --mode listener{nameArg}", workingDirectory);
                if (configuredProcess != null)
                {
                    return configuredProcess;
                }
            }

            // Try system launchers (fallback)
            var launchers = new[] { "py", "python", "python3" };
            var prefixes = new Dictionary<string, string> { { "py", "-3 " }, { "python", "" }, { "python3", "" } };

            foreach (var launcher in launchers)
            {
                var prefix = prefixes[launcher];
                var pythonProcess = TryStartProcess(launcher, $"{prefix}\"{scriptPath}\" --mode listener{nameArg}", workingDirectory);
                if (pythonProcess != null)
                {
                    return pythonProcess;
                }
            }

            return null;
        }

        private Process? TryStartProcess(string fileName, string arguments, string workingDirectory)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                startInfo.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";

                var process = Process.Start(startInfo);
                if (process != null)
                {
                    process.EnableRaisingEvents = true;
                    return process;
                }
            }
            catch
            {
                // Silently fail and try next launcher
            }

            return null;
        }

        private static string? ResolveVoiceListenerPath()
        {
            // Check direct candidates first (fastest)
            var directCandidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Voice Auth", "main.py"),
                Path.Combine(Directory.GetCurrentDirectory(), "Voice Auth", "main.py")
            };

            foreach (var candidate in directCandidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // Try current directory first before traversing up
            try
            {
                var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
                var candidate = Path.Combine(currentDir.FullName, "Voice Auth", "main.py");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch { }

            // Traverse up from AppContext.BaseDirectory
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            var maxLevels = 5; // Limit traversal depth to avoid excessive I/O
            var currentLevel = 0;

            while (directory != null && currentLevel < maxLevels)
            {
                var candidate = Path.Combine(directory.FullName, "Voice Auth", "main.py");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                candidate = Path.Combine(directory.FullName, "LabServerAdmin", "Voice Auth", "main.py");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
                currentLevel++;
            }

            return null;
        }
    }

    public class VoiceCommandEventArgs : EventArgs
    {
        public string Command { get; }
        public string Target { get; }

        public VoiceCommandEventArgs(string command, string target)
        {
            Command = command;
            Target = target;
        }
    }
}