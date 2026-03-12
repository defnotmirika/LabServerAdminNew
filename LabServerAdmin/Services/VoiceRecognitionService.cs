using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Speech.Recognition;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace LabServerAdmin.Services
{
    [SupportedOSPlatform("windows")]
    public class VoiceRecognitionService
    {
        private SpeechRecognitionEngine? _recognizer;
        private readonly TcpServerService _tcpServer;
        private readonly WakeOnLanService _wakeOnLan;
        private bool _isListening = false;
        private readonly Dictionary<string, string> _voiceApplications;
        private readonly CultureInfo _recognizerCulture;
        private readonly VoiceSpeakerService _speakerService;
        private string? _currentUser;
        private bool _verificationEnabled = false;

        public event EventHandler<VoiceCommandEventArgs>? VoiceCommandRecognized;
        public event EventHandler<string>? RecognitionError;
        public event EventHandler<string>? VoiceRejected;

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
            { "log",            "lock" },
            { "lok",            "lock" },
            { "lack",           "lock" },
            { "loch",           "lock" },
            { "block",          "lock" },
            { "lak",            "lock" },
            { "loc",            "lock" },

            // "unlock" misheard as:
            { "on lock",        "unlock" },
            { "in lock",        "unlock" },
            { "un log",         "unlock" },
            { "unblock",        "unlock" },
            { "on log",         "unlock" },
            { "and lock",       "unlock" },
            { "an lock",        "unlock" },

            // "shutdown" misheard as:
            { "shut down",      "shutdown" },
            { "shot down",      "shutdown" },
            { "shatter down",   "shutdown" },
            { "shattered",      "shutdown" },
            { "shadow",         "shutdown" },
            { "shut ton",       "shutdown" },
            { "shutter",        "shutdown" },

            // "restart" misheard as:
            { "re start",       "restart" },
            { "the start",      "restart" },
            { "restarted",      "restart" },
            { "re-start",       "restart" },

            // "sleep" misheard as:
            { "slip",           "sleep" },
            { "sleet",          "sleep" },
            { "slim",           "sleep" },
            { "seep",           "sleep" },
            { "steep",          "sleep" },

            // "refresh" misheard as:
            { "the fresh",      "refresh" },
            { "re fresh",       "refresh" },
            { "refres",         "refresh" },
        };

        public VoiceRecognitionService(TcpServerService tcpServer, WakeOnLanService wakeOnLan, IConfiguration configuration, VoiceSpeakerService speakerService)
        {
            _tcpServer = tcpServer;
            _wakeOnLan = wakeOnLan;
            _speakerService = speakerService;

            _voiceApplications = configuration.GetSection("VoiceRecognition:Applications")
                .Get<Dictionary<string, string>>()?
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var languageSetting = configuration["VoiceRecognition:Language"];
            try
            {
                _recognizerCulture = !string.IsNullOrWhiteSpace(languageSetting)
                    ? new CultureInfo(languageSetting)
                    : new CultureInfo("en-US");
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

                // ── FIX 1: Lowered thresholds for Filipino accent + built-in mic ──
                // Original was 30/60 — too strict for non-native English speakers.
                _recognizer.UpdateRecognizerSetting("CFGConfidenceRejectionThreshold", 10);
                _recognizer.UpdateRecognizerSetting("HighConfidenceThreshold", 40);

                // ── Structured grammar ──────────────────────────────────────
                // Include alias words so the SR engine can match Filipino pronunciations
                var actions = new Choices(
                    // Correct words
                    "lock", "unlock", "shutdown", "shut down", "restart", "re start", "sleep", "refresh",
                    // Filipino accent variants — lock
                    "log", "lok", "lack", "lak", "loc",
                    // Filipino accent variants — unlock
                    "on lock", "in lock", "un log", "and lock", "an lock",
                    // Filipino accent variants — shutdown
                    "shot down", "shattered", "shut ton",
                    // Filipino accent variants — sleep
                    "slip", "sleet", "seep", "steep",
                    // Filipino accent variants — refresh
                    "the fresh", "re fresh"
                );

                var numbers = Enumerable.Range(1, 50)
                    .Select(i => i.ToString(CultureInfo.InvariantCulture))
                    .ToArray();
                var numberChoices = new Choices(numbers);

                // "lock all" / "unlock all" etc.
                var allBuilder = new GrammarBuilder();
                allBuilder.Append(actions);
                allBuilder.Append(new Choices("all", "all pc", "all pcs"));
                _recognizer.LoadGrammar(new Grammar(allBuilder) { Name = "CommandAll" });

                // "lock PC 2" / "unlock PC 5" etc.
                var pcBuilder = new GrammarBuilder();
                pcBuilder.Append(actions);
                pcBuilder.Append(new Choices("pc", "p c", "computer"), 0, 1);
                pcBuilder.Append(numberChoices);
                _recognizer.LoadGrammar(new Grammar(pcBuilder) { Name = "CommandPC" });

                // "lock 2" shorthand
                var numBuilder = new GrammarBuilder();
                numBuilder.Append(actions);
                numBuilder.Append(numberChoices);
                _recognizer.LoadGrammar(new Grammar(numBuilder) { Name = "CommandNumber" });

                // ── FIX 2: DictationGrammar REMOVED ────────────────────────
                // The DictationGrammar was competing with structured grammars,
                // causing the engine to prefer free-form text over exact commands.
                // Removing it forces the engine to match structured grammars only.

                // ── Open PC grammar ─────────────────────────────────────────
                // "open all PC" / "open all"
                var openAllBuilder = new GrammarBuilder();
                openAllBuilder.Append("open");
                openAllBuilder.Append(new Choices("all", "all pc", "all pcs"));
                _recognizer.LoadGrammar(new Grammar(openAllBuilder) { Name = "OpenAll" });

                // "open PC 2" / "open 2"
                var openPcBuilder = new GrammarBuilder();
                openPcBuilder.Append("open");
                openPcBuilder.Append(new Choices("pc", "p c", "computer"), 0, 1);
                openPcBuilder.Append(numberChoices);
                _recognizer.LoadGrammar(new Grammar(openPcBuilder) { Name = "OpenPC" });

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

                RecognitionError?.Invoke(this, $"Heard: \"{text}\" (confidence: {confidence:P0})");

                if (confidence < 0.30f)
                {
                    RecognitionError?.Invoke(this, $"⚠️ Ignored low confidence: \"{text}\" ({confidence:P0})");
                    return;
                }

                // ── SPEAKER VERIFICATION ─────────────────────────────────────────
                if (_verificationEnabled && !string.IsNullOrWhiteSpace(_currentUser))
                {
                    try
                    {
                        var ms = new System.IO.MemoryStream();
                        e.Result.Audio.WriteToWaveStream(ms);
                        ms.Position = 0;

                        // Skip 44-byte WAV header to get raw PCM bytes for MFCC
                        var allBytes = ms.ToArray();
                        var audioBytes = allBytes.Length > 44
                            ? allBytes[44..]
                            : allBytes;

                        var (isMatch, score) = _speakerService.Verify(_currentUser, audioBytes);
                        RecognitionError?.Invoke(this,
                            $"🔍 Speaker score: {score:P0} — {(isMatch ? "✅ ACCEPTED" : "❌ REJECTED")}");

                        if (!isMatch)
                        {
                            RecognitionError?.Invoke(this, "🚫 Command rejected — voice not recognized.");
                            VoiceRejected?.Invoke(this, $"❌ Unknown voice detected! (score: {score:P0}) — Command blocked.");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        // If verification fails technically, log and allow command (fail-open)
                        RecognitionError?.Invoke(this, $"⚠️ Verification error (allowing): {ex.Message}");
                    }
                }
                // ────────────────────────────────────────────────────────────────

                var parsed = ParseVoiceCommand(text.ToLowerInvariant());

                if (parsed != null)
                {
                    VoiceCommandRecognized?.Invoke(this, new VoiceCommandEventArgs(parsed.Command, parsed.Target));
                    await ExecuteVoiceCommandAsync(parsed);
                }
                else
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
        }

        // ── Command parser ───────────────────────────────────────────────────
        private VoiceCommand? ParseVoiceCommand(string text)
        {
            // Normalize everything to lowercase and trim
            text = text.Trim().ToLowerInvariant();

            foreach (var alias in _commandAliases.OrderByDescending(k => k.Key.Length))
            {
                // Ensure the alias matches a whole word, not a prefix of another word
                if (text.StartsWith(alias.Key, StringComparison.OrdinalIgnoreCase))
                {
                    var afterAlias = text.Substring(alias.Key.Length);
                    // Only replace if followed by whitespace, end of string, or punctuation
                    if (afterAlias.Length == 0 || char.IsWhiteSpace(afterAlias[0]))
                    {
                        text = alias.Value + afterAlias;
                        break;
                    }
                }
            }

            // ── Step 2: Handle "open" commands ──────────────────────────────
            if (text.StartsWith("open "))
            {
                var openRest = text.Substring(5).Trim();

                if (openRest == "all" || openRest == "all pc" || openRest == "all pcs")
                    return new VoiceCommand { Command = "open", Target = "all" };

                var openTarget = Regex.Replace(openRest, @"^(pc|p\.?c\.?|computer)\s*", "", RegexOptions.IgnoreCase).Trim();

                var openDigit = Regex.Match(openTarget, @"^(\d{1,2})$");
                if (openDigit.Success)
                    return new VoiceCommand { Command = "open", Target = FormatPcName(int.Parse(openDigit.Groups[1].Value)) };

                var openNormalized = Regex.Replace(openTarget, @"\b(oh|zero)\b", "0").Trim();
                var openZeroDigit = Regex.Match(openNormalized, @"^0\s*(\d)$");
                if (openZeroDigit.Success)
                    return new VoiceCommand { Command = "open", Target = FormatPcName(int.Parse(openZeroDigit.Groups[1].Value)) };

                if (_wordNumbers.TryGetValue(openNormalized, out var openWordNum))
                    return new VoiceCommand { Command = "open", Target = FormatPcName(openWordNum) };

                foreach (var kvp in _wordNumbers.OrderByDescending(k => k.Key.Length))
                {
                    if (openNormalized.Contains(kvp.Key))
                        return new VoiceCommand { Command = "open", Target = FormatPcName(kvp.Value) };
                }

                var appName = Regex.Replace(openRest, @"^(application|app)\s+", "", RegexOptions.IgnoreCase).Trim();
                if (!string.IsNullOrWhiteSpace(appName))
                    return new VoiceCommand { Command = "open", Target = appName };

                return null;
            }

            // ── Step 3: Match action keyword ─────────────────────────────────
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
            if (string.IsNullOrWhiteSpace(rest) || rest == "all")
                return new VoiceCommand { Command = action, Target = "all" };

            // Strip "pc", "computer" prefix regardless of case or spacing
            rest = Regex.Replace(rest, @"^(pc|p\.?c\.?|computer)\s*", "", RegexOptions.IgnoreCase).Trim();

            // Still "all" after stripping prefix
            if (rest == "all" || rest == "all pc" || rest == "all pcs" || string.IsNullOrWhiteSpace(rest))
                return new VoiceCommand { Command = action, Target = "all" };

            // Digit: "1", "02"
            var digitMatch = Regex.Match(rest, @"^(\d{1,2})$");
            if (digitMatch.Success)
                return new VoiceCommand { Command = action, Target = FormatPcName(int.Parse(digitMatch.Groups[1].Value)) };

            // "oh 2" or "zero 2"
            var normalized = Regex.Replace(rest, @"\b(oh|zero)\b", "0").Trim();
            var zeroDigit = Regex.Match(normalized, @"^0\s*(\d)$");
            if (zeroDigit.Success)
                return new VoiceCommand { Command = action, Target = FormatPcName(int.Parse(zeroDigit.Groups[1].Value)) };

            // Word number: "one", "twenty two"
            if (_wordNumbers.TryGetValue(normalized, out var wordNum))
                return new VoiceCommand { Command = action, Target = FormatPcName(wordNum) };

            // Partial word match fallback
            foreach (var kvp in _wordNumbers.OrderByDescending(k => k.Key.Length))
            {
                if (normalized.Contains(kvp.Key))
                    return new VoiceCommand { Command = action, Target = FormatPcName(kvp.Value) };
            }

            // Last resort — any digit found anywhere in rest
            var anyDigit = Regex.Match(rest, @"(\d{1,2})");
            if (anyDigit.Success)
                return new VoiceCommand { Command = action, Target = FormatPcName(int.Parse(anyDigit.Groups[1].Value)) };

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
                    // "open all" → Wake ALL PCs via WOL, then also send TCP open to any already-on PCs
                    if (command.Target == "all")
                    {
                        _wakeOnLan.WakeAllPCs();
                        RecognitionError?.Invoke(this, "✅ Wake-on-LAN sent to ALL PCs");

                        // Also send TCP open to PCs that are already awake and connected
                        var connected = _tcpServer.GetConnectedClients();
                        if (connected.Count > 0)
                        {
                            await _tcpServer.SendCommandToAllAsync("open");
                            RecognitionError?.Invoke(this, $"✅ OPEN also sent via TCP to {connected.Count} connected PC(s)");
                        }
                        return;
                    }

                    // "open PC-03" → Try WOL first (wakes sleeping/off PCs)
                    if (command.Target.StartsWith("PC-", StringComparison.OrdinalIgnoreCase)
                        || System.Text.RegularExpressions.Regex.IsMatch(command.Target, @"\d"))
                    {
                        var wokeUp = _wakeOnLan.WakePC(command.Target);
                        if (wokeUp)
                        {
                            RecognitionError?.Invoke(this, $"✅ Wake-on-LAN sent to {command.Target}");
                        }
                        else
                        {
                            RecognitionError?.Invoke(this, $"⚠️ No MAC address configured for {command.Target} — WOL skipped");
                        }

                        // Also send TCP open if PC is already connected
                        if (_tcpServer.IsClientConnected(command.Target))
                        {
                            await _tcpServer.SendCommandAsync(command.Target, "open");
                            RecognitionError?.Invoke(this, $"✅ OPEN also sent via TCP to {command.Target}");
                        }
                        else
                        {
                            // Fuzzy match by number
                            var openClients = _tcpServer.GetConnectedClients();
                            var openTargetNum = Regex.Match(command.Target, @"\d+").Value.TrimStart('0');
                            var openMatched = openClients.Keys.FirstOrDefault(k =>
                                Regex.Match(k, @"\d+").Value.TrimStart('0') == openTargetNum);
                            if (openMatched != null)
                            {
                                await _tcpServer.SendCommandAsync(openMatched, "open");
                                RecognitionError?.Invoke(this, $"✅ OPEN also sent via TCP to {openMatched}");
                            }
                        }
                        return;
                    }

                    // Fallback: treat as app launcher
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


        public void SetCurrentUser(string? username)
        {
            _currentUser = username;
            _verificationEnabled = !string.IsNullOrWhiteSpace(username)
                                   && _speakerService.HasProfile(username);
            RecognitionError?.Invoke(this,
                _verificationEnabled
                    ? $"🔒 Voice verification ENABLED for {username}"
                    : $"⚠️ Voice verification DISABLED — no profile for {username}");
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