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

        // Word-to-number mapping so "two" → 2, "zero two" → 02, etc.
        private static readonly Dictionary<string, int> _wordNumbers = new(StringComparer.OrdinalIgnoreCase)
        {
            {"one",1},{"two",2},{"three",3},{"four",4},{"five",5},
            {"six",6},{"seven",7},{"eight",8},{"nine",9},{"ten",10},
            {"eleven",11},{"twelve",12},{"thirteen",13},{"fourteen",14},{"fifteen",15},
            {"sixteen",16},{"seventeen",17},{"eighteen",18},{"nineteen",19},{"twenty",20},
            {"twenty one",21},{"twenty two",22},{"twenty three",23},{"twenty four",24},{"twenty five",25},
            {"twenty six",26},{"twenty seven",27},{"twenty eight",28},{"twenty nine",29},{"thirty",30},
        };

        // ── Filipino accent alias mappings ───────────────────────────────────
        // Maps misheard words → correct command
        private static readonly Dictionary<string, string> _commandAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            // "lock" misheard as:
            { "log",        "lock" },
            { "lok",        "lock" },
            { "lack",       "lock" },
            { "loch",       "lock" },
            { "block",      "lock" },

            // "unlock" misheard as:
            { "on lock",    "unlock" },
            { "in lock",    "unlock" },
            { "un log",     "unlock" },
            { "unblock",    "unlock" },
            { "on log",     "unlock" },

            // "shutdown" misheard as:
            { "shut down",  "shutdown" },
            { "shot down",  "shutdown" },
            { "shatter down","shutdown" },
            { "shattered",  "shutdown" },
            { "shadow",     "shutdown" },

            // "restart" misheard as:
            { "re start",   "restart" },
            { "the start",  "restart" },
            { "restarted",  "restart" },

            // "sleep" misheard as:
            { "slip",       "sleep" },
            { "sleet",      "sleep" },
            { "slim",       "sleep" },

            // "refresh" misheard as:
            { "the fresh",  "refresh" },
            { "re fresh",   "refresh" },
        };

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
                    : new CultureInfo("en-US"); // Default to en-US for best SR compatibility
            }
            catch (CultureNotFoundException)
            {
                _recognizerCulture = new CultureInfo("en-US");
            }

            InitializeRecognizer();
        }

        private void InitializeRecognizer()
        {
            try
            {
                _recognizer = new SpeechRecognitionEngine(_recognizerCulture);

                // ── Lower confidence threshold for Filipino accent + built-in mic ──
                _recognizer.UpdateRecognizerSetting("CFGConfidenceRejectionThreshold", 30);
                _recognizer.UpdateRecognizerSetting("HighConfidenceThreshold", 60);

                // ── Structured grammar ──────────────────────────────────────
                // Include alias words so the SR engine can match Filipino pronunciations
                var actions = new Choices(
                    // Correct words
                    "lock", "unlock", "shutdown", "shut down", "restart", "re start", "sleep", "refresh",
                    // Filipino accent variants
                    "log", "lok", "lack",           // lock
                    "on lock", "in lock", "un log", // unlock
                    "shot down", "shattered",        // shutdown
                    "slip", "sleet",                 // sleep
                    "the fresh", "re fresh"          // refresh
                );

                var numbers = Enumerable.Range(1, 50)
                    .Select(i => i.ToString(CultureInfo.InvariantCulture))
                    .ToArray();
                var numberChoices = new Choices(numbers);

                // "lock all" / "unlock all" etc.
                var allBuilder = new GrammarBuilder();
                allBuilder.Append(actions);
                allBuilder.Append("all");
                _recognizer.LoadGrammar(new Grammar(allBuilder) { Name = "CommandAll" });

                // "lock PC 2" / "unlock PC 5" etc.
                var pcBuilder = new GrammarBuilder();
                pcBuilder.Append(actions);
                pcBuilder.Append("PC");
                pcBuilder.Append(numberChoices);
                _recognizer.LoadGrammar(new Grammar(pcBuilder) { Name = "CommandPC" });

                // "lock 2" shorthand
                var numBuilder = new GrammarBuilder();
                numBuilder.Append(actions);
                numBuilder.Append(numberChoices);
                _recognizer.LoadGrammar(new Grammar(numBuilder) { Name = "CommandNumber" });

                // ── Dictation fallback (catches anything else) ──────────────
                var dictation = new DictationGrammar();
                dictation.Name = "Dictation";
                dictation.Weight = 0.1f;
                _recognizer.LoadGrammar(dictation);

                // ── Open application grammar ────────────────────────────────
                if (_voiceApplications.Count > 0)
                {
                    var appNames = new Choices(_voiceApplications.Keys.ToArray());
                    var openBuilder = new GrammarBuilder();
                    openBuilder.Append("open");
                    openBuilder.Append(new Choices("application", "app"), 0, 1);
                    openBuilder.Append(appNames);
                    _recognizer.LoadGrammar(new Grammar(openBuilder) { Name = "OpenApplications" });
                }

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
                var text = e.Result.Text.Trim();
                var confidence = e.Result.Confidence;

                // Always show what was heard + confidence score for debugging
                RecognitionError?.Invoke(this, $"Heard: \"{text}\" (confidence: {confidence:P0})");

                // Skip very low confidence results to avoid false triggers
                if (confidence < 0.50f)
                {
                    RecognitionError?.Invoke(this, $"⚠️ Ignored low confidence: \"{text}\" ({confidence:P0})");
                    return;
                }

                var parsed = ParseVoiceCommand(text.ToLowerInvariant());

                if (parsed != null)
                {
                    VoiceCommandRecognized?.Invoke(this, new VoiceCommandEventArgs(parsed.Command, parsed.Target));
                    await ExecuteVoiceCommandAsync(parsed);
                }
                else if (e.Result.Grammar.Name != "Dictation")
                {
                    RecognitionError?.Invoke(this, $"❓ Could not parse: \"{text}\"");
                }
            }
            catch (Exception ex)
            {
                RecognitionError?.Invoke(this, $"Error processing voice command: {ex.Message}");
            }
        }

        private void OnSpeechRecognitionRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
        {
            // Silently ignore — don't spam on ambient noise
        }

        // ── Command parser ───────────────────────────────────────────────────
        private VoiceCommand? ParseVoiceCommand(string text)
        {
            text = text.Trim().ToLowerInvariant();

            // ── Step 1: Normalize misheard words using alias map ─────────────
            // Try multi-word aliases first (longer matches take priority)
            foreach (var alias in _commandAliases.OrderByDescending(k => k.Key.Length))
            {
                if (text.StartsWith(alias.Key, StringComparison.OrdinalIgnoreCase))
                {
                    text = alias.Value + text.Substring(alias.Key.Length);
                    break;
                }
            }

            // ── Step 2: Handle "open" commands ──────────────────────────────
            if (text.StartsWith("open "))
            {
                var appName = text.Substring(5).Trim();
                appName = Regex.Replace(appName, @"^(application|app)\s+", "", RegexOptions.IgnoreCase).Trim();
                if (!string.IsNullOrWhiteSpace(appName))
                    return new VoiceCommand { Command = "open", Target = appName };
                return null;
            }

            // ── Step 3: Match action keyword ─────────────────────────────────
            // Longest first to avoid "lock" matching "unlock"
            var validActions = new[] { "view logs", "shutdown", "restart", "unlock", "refresh", "sleep", "lock" };
            string? action = null;
            string rest = text;

            foreach (var a in validActions)
            {
                if (text.StartsWith(a))
                {
                    action = a;
                    rest = text.Substring(a.Length).Trim();
                    break;
                }
            }

            if (action == null) return null;

            // ── Step 4: Parse target ─────────────────────────────────────────

            // "all" or empty → all PCs
            if (rest == "all" || rest == "")
                return new VoiceCommand { Command = action, Target = "all" };

            // Strip "pc", "p.c.", "computer" prefix
            rest = Regex.Replace(rest, @"^(pc|p\.?c\.?|computer)\s*", "", RegexOptions.IgnoreCase).Trim();

            // Digit number: "2", "02"
            var digitMatch = Regex.Match(rest, @"^(\d{1,2})$");
            if (digitMatch.Success)
                return new VoiceCommand { Command = action, Target = FormatPcName(int.Parse(digitMatch.Groups[1].Value)) };

            // "oh 2" or "zero 2" → 2
            var normalized = Regex.Replace(rest, @"\b(oh|zero)\b", "0").Trim();
            var zeroDigit = Regex.Match(normalized, @"^0\s*(\d)$");
            if (zeroDigit.Success)
                return new VoiceCommand { Command = action, Target = FormatPcName(int.Parse(zeroDigit.Groups[1].Value)) };

            // Word number: "two", "twenty two"
            if (_wordNumbers.TryGetValue(normalized, out var wordNum))
                return new VoiceCommand { Command = action, Target = FormatPcName(wordNum) };

            // Partial word match fallback
            foreach (var kvp in _wordNumbers.OrderByDescending(k => k.Key.Length))
            {
                if (normalized.Contains(kvp.Key))
                    return new VoiceCommand { Command = action, Target = FormatPcName(kvp.Value) };
            }

            return null;
        }

        /// <summary>
        /// Change this format to match YOUR actual PC naming convention.
        /// Current: PC-02, PC-03, etc.
        /// If your PCs are named "PC 02" (with space), change to $"PC {number:D2}"
        /// If named "PC02" (no separator), change to $"PC{number:D2}"
        /// </summary>
        private static string FormatPcName(int number) => $"PC-{number:D2}";

        // ── Command executor ─────────────────────────────────────────────────
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
                    RecognitionError?.Invoke(this, $"✅ {command.Command.ToUpper()} sent to ALL clients");
                    return;
                }

                // Try exact name first
                if (_tcpServer.IsClientConnected(command.Target))
                {
                    await _tcpServer.SendCommandAsync(command.Target, command.Command);
                    RecognitionError?.Invoke(this, $"✅ {command.Command.ToUpper()} sent to {command.Target}");
                    return;
                }

                // Fuzzy fallback: match connected clients by number only
                var connectedClients = _tcpServer.GetConnectedClients();
                var targetNum = Regex.Match(command.Target, @"\d+").Value.TrimStart('0');

                var matched = connectedClients.Keys.FirstOrDefault(k =>
                    Regex.Match(k, @"\d+").Value.TrimStart('0') == targetNum);

                if (matched != null)
                {
                    await _tcpServer.SendCommandAsync(matched, command.Command);
                    RecognitionError?.Invoke(this, $"✅ {command.Command.ToUpper()} sent to {matched}");
                }
                else
                {
                    var available = connectedClients.Count > 0
                        ? string.Join(", ", connectedClients.Keys)
                        : "none connected";
                    RecognitionError?.Invoke(this, $"❌ '{command.Target}' not found. Connected: {available}");
                }
            }
            catch (Exception ex)
            {
                RecognitionError?.Invoke(this, $"Failed to execute command: {ex.Message}");
            }
        }

        private Task LaunchApplicationAsync(string applicationKey)
        {
            if (string.IsNullOrWhiteSpace(applicationKey))
            {
                RecognitionError?.Invoke(this, "No application specified");
                return Task.CompletedTask;
            }

            if (!_voiceApplications.TryGetValue(applicationKey.Trim(), out var executable))
            {
                RecognitionError?.Invoke(this, $"Application '{applicationKey}' not configured.");
                return Task.CompletedTask;
            }

            try { Process.Start(new ProcessStartInfo { FileName = executable, UseShellExecute = true }); }
            catch (Exception ex) { RecognitionError?.Invoke(this, $"Failed to open {applicationKey}: {ex.Message}"); }

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
        public VoiceCommandEventArgs(string command, string target) { Command = command; Target = target; }
    }
}