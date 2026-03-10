using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NAudio.Wave;

namespace LabServerAdmin.Services
{
    /// <summary>
    /// Offline voice speaker verification using MFCC (Mel-Frequency Cepstral Coefficients).
    /// No internet required — all voice profiles stored locally as JSON files.
    ///
    /// HOW IT WORKS:
    ///   1. Professor records 3 sample phrases during enrollment
    ///   2. MFCC features are extracted from each sample
    ///   3. Stored as both averaged vector AND individual vectors
    ///   4. On each voice command, incoming audio is compared against ALL stored vectors
    ///   5. Best score is used — if >= threshold → command accepted
    ///   6. If score too low → command rejected (student voice blocked)
    ///
    /// PROFILE STORAGE:
    ///   Profiles saved to: Data/VoiceProfiles/{username}.json
    /// </summary>
    public class VoiceSpeakerService
    {
        // ── Constants ────────────────────────────────────────────────────────
        private const string ProfilesFolder = "Data/VoiceProfiles";
        private const int SampleRate = 16000;  // 16kHz standard for SR
        private const int BitsPerSample = 16;
        private const int Channels = 1;      // Mono
        private const int RecordingSeconds = 3;      // Duration per sample
        private const int MfccCoefficients = 13;     // Standard MFCC count
        private const double VerifyThreshold = 0.82; // 60% similarity required

        // ── Enrollment phrases — short to match actual command length ────────
        public static readonly string[] EnrollmentPhrases = new[]
        {
            "Lock all",
            "Unlock all",
            "Shutdown all"
        };

        public VoiceSpeakerService()
        {
            Directory.CreateDirectory(ProfilesFolder);
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Returns true if the professor has an enrolled voice profile.</summary>
        public bool HasProfile(string username) =>
            File.Exists(GetProfilePath(username));

        /// <summary>
        /// Records a single voice sample.
        /// Returns raw PCM bytes to be passed to SaveProfile().
        /// Progress reported via onProgress callback (0.0 to 1.0).
        /// </summary>
        public async Task<byte[]> RecordSampleAsync(
            IProgress<double>? onProgress = null,
            System.Threading.CancellationToken cancellationToken = default,
            int? durationOverride = null)
        {
            var buffer = new List<byte>();

            using var waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
                BufferMilliseconds = 100
            };

            waveIn.DataAvailable += (_, e) =>
            {
                buffer.AddRange(e.Buffer.Take(e.BytesRecorded));
            };

            waveIn.StartRecording();

            var totalMs = (durationOverride ?? RecordingSeconds) * 1000;
            var elapsed = 0;
            while (elapsed < totalMs && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                elapsed += 100;
                onProgress?.Report((double)elapsed / totalMs);
            }

            waveIn.StopRecording();
            return buffer.ToArray();
        }

        /// <summary>
        /// Saves 3 recorded samples as a voice profile for the given username.
        /// Stores both averaged vector AND all individual vectors for better matching.
        /// </summary>
        public void SaveProfile(string username, List<byte[]> samples)
        {
            if (samples.Count < 3)
                throw new ArgumentException("Need at least 3 voice samples for enrollment.");

            var allMfcc = samples
                .Select(s => ExtractMfcc(s))
                .ToList();

            // Compute averaged MFCC vector across all samples
            var avgMfcc = new double[MfccCoefficients];
            foreach (var mfcc in allMfcc)
                for (int i = 0; i < MfccCoefficients; i++)
                    avgMfcc[i] += mfcc[i];
            for (int i = 0; i < MfccCoefficients; i++)
                avgMfcc[i] /= allMfcc.Count;

            var profile = new VoiceProfile
            {
                Username = username,
                EnrolledAt = DateTime.UtcNow,
                MfccVector = avgMfcc,   // averaged — for quick comparison
                MfccVectors = allMfcc    // all individual samples — for best-match
            };

            var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetProfilePath(username), json);
        }

        /// <summary>
        /// Verifies a recorded audio sample against the stored voice profile.
        /// Uses best-match scoring across all enrolled vectors.
        /// Returns (isMatch, bestScore).
        /// </summary>
        public (bool IsMatch, double Score) Verify(string username, byte[] audioSample)
        {
            if (!HasProfile(username))
                return (false, 0.0);

            var json = File.ReadAllText(GetProfilePath(username));
            var profile = JsonSerializer.Deserialize<VoiceProfile>(json);
            if (profile?.MfccVector == null)
                return (false, 0.0);

            // Strip WAV header if present — get raw PCM
            var pcmBytes = StripWavHeader(audioSample);

            // Too short — not enough audio to verify
            if (pcmBytes.Length < 1000)
                return (false, 0.0);

            var incomingMfcc = ExtractMfcc(pcmBytes);

            // Start with averaged vector score
            var bestScore = CosineSimilarity(incomingMfcc, profile.MfccVector);

            // Also compare against each individual enrolled sample
            // Take the highest score (best match wins)
            if (profile.MfccVectors?.Count > 0)
            {
                foreach (var vector in profile.MfccVectors)
                {
                    var s = CosineSimilarity(incomingMfcc, vector);
                    if (s > bestScore) bestScore = s;
                }
            }

            return (bestScore >= VerifyThreshold, bestScore);
        }

        /// <summary>Deletes the voice profile for the given username.</summary>
        public void DeleteProfile(string username)
        {
            var path = GetProfilePath(username);
            if (File.Exists(path))
                File.Delete(path);
        }

        // ── WAV Header Stripping ──────────────────────────────────────────────

        /// <summary>
        /// Safely strips WAV header by finding the "data" chunk marker.
        /// More reliable than assuming a fixed 44-byte header.
        /// </summary>
        private static byte[] StripWavHeader(byte[] wavBytes)
        {
            // Check if it's a WAV file (starts with "RIFF")
            if (wavBytes.Length > 44 &&
                wavBytes[0] == 'R' && wavBytes[1] == 'I' &&
                wavBytes[2] == 'F' && wavBytes[3] == 'F')
            {
                // Find "data" chunk marker
                for (int i = 12; i < wavBytes.Length - 8; i++)
                {
                    if (wavBytes[i] == 'd' && wavBytes[i + 1] == 'a' &&
                        wavBytes[i + 2] == 't' && wavBytes[i + 3] == 'a')
                    {
                        // PCM data starts after "data" marker + 4-byte size field
                        return wavBytes[(i + 8)..];
                    }
                }
            }

            // Not a WAV file — return as-is (already raw PCM)
            return wavBytes;
        }

        // ── MFCC Extraction ───────────────────────────────────────────────────

        private double[] ExtractMfcc(byte[] pcmBytes)
        {
            // Convert PCM bytes → float samples
            var samples = new double[pcmBytes.Length / 2];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = BitConverter.ToInt16(pcmBytes, i * 2) / 32768.0;

            // Frame the signal (25ms frames, 10ms hop)
            int frameSize = (int)(SampleRate * 0.025);
            int hopSize = (int)(SampleRate * 0.010);
            var frames = new List<double[]>();

            for (int start = 0; start + frameSize < samples.Length; start += hopSize)
            {
                var frame = new double[frameSize];
                Array.Copy(samples, start, frame, 0, frameSize);
                ApplyHammingWindow(frame);
                frames.Add(frame);
            }

            if (frames.Count == 0)
                return new double[MfccCoefficients];

            // Compute average MFCC over all frames
            var mfccSum = new double[MfccCoefficients];
            foreach (var frame in frames)
            {
                var frameMfcc = ComputeFrameMfcc(frame);
                for (int i = 0; i < MfccCoefficients; i++)
                    mfccSum[i] += frameMfcc[i];
            }

            for (int i = 0; i < MfccCoefficients; i++)
                mfccSum[i] /= frames.Count;

            return mfccSum;
        }

        private double[] ComputeFrameMfcc(double[] frame)
        {
            // Power spectrum via DFT
            int n = frame.Length;
            var power = new double[n / 2];
            for (int k = 0; k < n / 2; k++)
            {
                double real = 0, imag = 0;
                for (int t = 0; t < n; t++)
                {
                    double angle = 2 * Math.PI * k * t / n;
                    real += frame[t] * Math.Cos(angle);
                    imag -= frame[t] * Math.Sin(angle);
                }
                power[k] = (real * real + imag * imag) / n;
            }

            // Mel filter bank (26 filters)
            int numFilters = 26;
            double minFreq = 0;
            double maxFreq = SampleRate / 2.0;
            double minMel = FreqToMel(minFreq);
            double maxMel = FreqToMel(maxFreq);

            var filterEnergies = new double[numFilters];
            for (int m = 1; m <= numFilters; m++)
            {
                double centerMel = minMel + m * (maxMel - minMel) / (numFilters + 1);
                double leftMel = minMel + (m - 1) * (maxMel - minMel) / (numFilters + 1);
                double rightMel = minMel + (m + 1) * (maxMel - minMel) / (numFilters + 1);

                double centerFreq = MelToFreq(centerMel);
                double leftFreq = MelToFreq(leftMel);
                double rightFreq = MelToFreq(rightMel);

                for (int k = 0; k < power.Length; k++)
                {
                    double freq = k * SampleRate / (double)n;
                    double weight = 0;

                    if (freq >= leftFreq && freq <= centerFreq)
                        weight = (freq - leftFreq) / (centerFreq - leftFreq);
                    else if (freq > centerFreq && freq <= rightFreq)
                        weight = (rightFreq - freq) / (rightFreq - centerFreq);

                    filterEnergies[m - 1] += weight * power[k];
                }

                filterEnergies[m - 1] = Math.Log(Math.Max(filterEnergies[m - 1], 1e-10));
            }

            // DCT → MFCC coefficients
            var mfcc = new double[MfccCoefficients];
            for (int i = 0; i < MfccCoefficients; i++)
                for (int j = 0; j < numFilters; j++)
                    mfcc[i] += filterEnergies[j] * Math.Cos(Math.PI * i * (j + 0.5) / numFilters);

            return mfcc;
        }

        private static void ApplyHammingWindow(double[] frame)
        {
            int n = frame.Length;
            for (int i = 0; i < n; i++)
                frame[i] *= 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (n - 1));
        }

        private static double FreqToMel(double freq) =>
            2595 * Math.Log10(1 + freq / 700.0);

        private static double MelToFreq(double mel) =>
            700 * (Math.Pow(10, mel / 2595.0) - 1);

        private static double CosineSimilarity(double[] a, double[] b)
        {
            double dot = 0, magA = 0, magB = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }
            double denom = Math.Sqrt(magA) * Math.Sqrt(magB);
            return denom == 0 ? 0 : (dot / denom + 1) / 2.0; // Normalized 0–1
        }

        private string GetProfilePath(string username) =>
            Path.Combine(ProfilesFolder, $"{username.ToLowerInvariant()}.json");
    }

    // ── Data Model ────────────────────────────────────────────────────────────

    public class VoiceProfile
    {
        public string Username { get; set; } = string.Empty;
        public DateTime EnrolledAt { get; set; }
        public double[] MfccVector { get; set; } = Array.Empty<double>();  // averaged
        public List<double[]> MfccVectors { get; set; } = new();           // individual samples
    }
}