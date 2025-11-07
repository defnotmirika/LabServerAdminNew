using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Speech.Recognition;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace LabServerAdmin.Services
{
    public class VoiceRecognitionService
    {
        private SpeechRecognitionEngine? _recognizer;
        private readonly TcpServerService _tcpServer;
        private bool _isListening = false;
        private readonly Dictionary<string, string> _voiceApplications;
        private readonly CultureInfo _recognizerCulture;

        public event EventHandler<VoiceCommandEventArgs>? VoiceCommandRecognized;
        public event EventHandler<string>? RecognitionError;

        public VoiceRecognitionService(TcpServerService tcpServer, IConfiguration configuration)
        {
            _tcpServer = tcpServer;
            _voiceApplications = configuration.GetSection("VoiceRecognition:Applications")
                .Get<Dictionary<string, string>>()?
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var languageSetting = configuration["VoiceRecognition:Language"];
            try
            {
                _recognizerCulture = !string.IsNullOrWhiteSpace(languageSetting)
                    ? new CultureInfo(languageSetting)
                    : CultureInfo.CurrentCulture;
            }
            catch (CultureNotFoundException)
            {
                _recognizerCulture = CultureInfo.CurrentCulture;
            }
            InitializeRecognizer();
        }

        private void InitializeRecognizer()
        {
            try
            {
                _recognizer = new SpeechRecognitionEngine(_recognizerCulture);

                var deviceCommands = new Choices("lock", "unlock", "shutdown", "restart", "sleep", "refresh", "view logs");
                var deviceGrammarBuilder = new GrammarBuilder();
                deviceGrammarBuilder.Append(deviceCommands);

                var targetChoices = new Choices("all");
                var numberedTargets = Enumerable.Range(1, 50).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();
                targetChoices.Add(numberedTargets);

                var targetGrammar = new GrammarBuilder();
                targetGrammar.Append("PC", 0, 1);
                targetGrammar.Append(targetChoices);

                deviceGrammarBuilder.Append(targetGrammar);
                var deviceGrammar = new Grammar(deviceGrammarBuilder) { Name = "DeviceCommands" };
                _recognizer.LoadGrammar(deviceGrammar);

                if (_voiceApplications.Count > 0)
                {
                    var applicationNames = new Choices(_voiceApplications.Keys.ToArray());
                    var openGrammarBuilder = new GrammarBuilder();
                    openGrammarBuilder.Append("open");
                    openGrammarBuilder.Append(new Choices(new[] { "application", "app" }), 0, 1);
                    openGrammarBuilder.Append(applicationNames);

                    var openGrammar = new Grammar(openGrammarBuilder) { Name = "OpenApplications" };
                    _recognizer.LoadGrammar(openGrammar);
                }
                
                // Set up event handlers
                _recognizer.SpeechRecognized += OnSpeechRecognized;
                _recognizer.SpeechRecognitionRejected += OnSpeechRecognitionRejected;
            }
            catch (Exception ex)
            {
                RecognitionError?.Invoke(this, $"Failed to initialize voice recognition: {ex.Message}");
            }
        }

        public Task StartListeningAsync()
        {
            if (_recognizer == null || _isListening) return Task.CompletedTask;

            try
            {
                _recognizer.SetInputToDefaultAudioDevice();
                _recognizer.RecognizeAsync(RecognizeMode.Multiple);
                _isListening = true;
            }
            catch (Exception ex)
            {
                RecognitionError?.Invoke(this, $"Failed to start voice recognition: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        public void StopListening()
        {
            if (_recognizer != null && _isListening)
            {
                _recognizer.RecognizeAsyncStop();
                _isListening = false;
            }
        }

        private async void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
        {
            try
            {
                var command = e.Result.Text.ToLowerInvariant();
                var parsedCommand = ParseVoiceCommand(command);
                
                if (parsedCommand != null)
                {
                    VoiceCommandRecognized?.Invoke(this, new VoiceCommandEventArgs(parsedCommand.Command, parsedCommand.Target));
                    await ExecuteVoiceCommandAsync(parsedCommand);
                }
            }
            catch (Exception ex)
            {
                RecognitionError?.Invoke(this, $"Error processing voice command: {ex.Message}");
            }
        }

        private void OnSpeechRecognitionRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
        {
            RecognitionError?.Invoke(this, "Voice command not recognized. Please try again.");
        }

        private VoiceCommand? ParseVoiceCommand(string command)
        {
            if (command.StartsWith("open ", StringComparison.OrdinalIgnoreCase))
            {
                var appName = command.Substring(5).Trim();

                if (appName.StartsWith("application ", StringComparison.OrdinalIgnoreCase))
                {
                    appName = appName.Substring("application ".Length).Trim();
                }
                else if (appName.StartsWith("app ", StringComparison.OrdinalIgnoreCase))
                {
                    appName = appName.Substring("app ".Length).Trim();
                }

                if (string.IsNullOrWhiteSpace(appName))
                {
                    RecognitionError?.Invoke(this, "Please specify an application to open.");
                    return null;
                }

                return new VoiceCommand
                {
                    Command = "open",
                    Target = appName
                };
            }

            var devicePattern = @"^(lock|unlock|shutdown|restart|sleep|refresh|view logs)\s+(?:pc\s+)?(\d+|all)$";
            var deviceMatch = Regex.Match(command, devicePattern, RegexOptions.IgnoreCase);
            if (deviceMatch.Success)
            {
                var action = deviceMatch.Groups[1].Value.ToLowerInvariant();
                var target = deviceMatch.Groups[2].Value.ToLowerInvariant();

                return new VoiceCommand
                {
                    Command = action,
                    Target = target
                };
            }

            return null;
        }

        private async Task ExecuteVoiceCommandAsync(VoiceCommand command)
        {
            try
            {
                if (command.Command == "open")
                {
                    await LaunchApplicationAsync(command.Target);
                    return;
                }

                if (command.Target == "all")
                {
                    await _tcpServer.SendCommandToAllAsync(command.Command);
                }
                else
                {
                    var clientName = $"PC {command.Target}";
                    if (_tcpServer.IsClientConnected(clientName))
                    {
                        await _tcpServer.SendCommandAsync(clientName, command.Command);
                    }
                    else
                    {
                        RecognitionError?.Invoke(this, $"PC {command.Target} is not connected");
                    }
                }
            }
            catch (Exception ex)
            {
                RecognitionError?.Invoke(this, $"Failed to execute voice command: {ex.Message}");
            }
        }

        private Task LaunchApplicationAsync(string applicationKey)
        {
            if (string.IsNullOrWhiteSpace(applicationKey))
            {
                RecognitionError?.Invoke(this, "No application specified");
                return Task.CompletedTask;
            }

            var lookupKey = applicationKey.Trim();

            if (!_voiceApplications.TryGetValue(lookupKey, out var executable))
            {
                RecognitionError?.Invoke(this, $"Application '{lookupKey}' is not configured for voice control.");
                return Task.CompletedTask;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = true
                };

                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                RecognitionError?.Invoke(this, $"Failed to open {lookupKey}: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            StopListening();
            _recognizer?.Dispose();
        }
    }

    public class VoiceCommand
    {
        public string Command { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
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
