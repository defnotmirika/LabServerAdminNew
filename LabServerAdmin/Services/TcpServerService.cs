using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LabServerAdmin.Models;

namespace LabServerAdmin.Services
{
    public class TcpServerService
    {
        private TcpListener? _listener;
        private readonly Dictionary<string, TcpClient> _connectedClients = new();
        private readonly Dictionary<string, ClientInfo> _clientInfo = new();
        private readonly DatabaseService _databaseService;
        private bool _isRunning = false;
        private readonly int _port = 9000;

        public event EventHandler<ClientConnectedEventArgs>? ClientConnected;
        public event EventHandler<ClientDisconnectedEventArgs>? ClientDisconnected;
        public event EventHandler<CommandReceivedEventArgs>? CommandReceived;
        public event EventHandler<ScreenDataReceivedEventArgs>? ScreenDataReceived;

        public TcpServerService(DatabaseService databaseService)
        {
            _databaseService = databaseService;
        }

        public async Task StartServerAsync()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
                _isRunning = true;

                await _databaseService.LogSystemActionAsync("Server Started", "System", "Success", $"TCP Server started on port {_port}");

                // Start accepting clients
                _ = Task.Run(AcceptClientsAsync);
            }
            catch (Exception ex)
            {
                await _databaseService.LogSystemActionAsync("Server Start Failed", "System", "Error", ex.Message);
                throw;
            }
        }

        public async Task StopServerAsync()
        {
            _isRunning = false;
            _listener?.Stop();

            // Disconnect all clients
            foreach (var client in _connectedClients.Values)
            {
                client.Close();
            }
            _connectedClients.Clear();
            _clientInfo.Clear();

            await _databaseService.LogSystemActionAsync("Server Stopped", "System", "Success", "TCP Server stopped");
        }

        private async Task AcceptClientsAsync()
        {
            while (_isRunning && _listener != null)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleClientAsync(client));
                }
                catch (ObjectDisposedException)
                {
                    // Server was stopped
                    break;
                }
                catch (Exception ex)
                {
                    await _databaseService.LogSystemActionAsync("Client Connection Error", "System", "Error", ex.Message);
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            string? clientName = null;
            try
            {

                client.NoDelay = true;
                client.SendBufferSize = 8192;
                client.ReceiveBufferSize = 8192;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                var stream = client.GetStream();
                var buffer = new byte[8192];
                var messageBuilder = new StringBuilder();

                while (client.Connected)
                {
                    var bytesRead = await stream.ReadAsync(buffer.AsMemory());
                    if (bytesRead == 0) break;

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    messageBuilder.Append(message);

                    // Process complete messages (assuming they end with newline)
                    var fullMessage = messageBuilder.ToString();
                    var lines = fullMessage.Split('\n');

                    // Process all complete lines (those that end with \n)
                    // Keep the last line in the builder if it doesn't end with \n (partial message)
                    for (int i = 0; i < lines.Length - 1; i++)
                    {
                        var line = lines[i].Trim();
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            // Extract clientName from registration messages for tracking
                            if (clientName == null && (line.Contains("\"type\":\"register\"", StringComparison.OrdinalIgnoreCase) || 
                                                       line.Contains("\"type\":\"register\"", StringComparison.OrdinalIgnoreCase)))
                            {
                                try
                                {
                                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                                    var msg = JsonSerializer.Deserialize<ClientMessage>(line, options);
                                    if (msg != null)
                                    {
                                        if (!string.IsNullOrWhiteSpace(msg.ClientName))
                                        {
                                            clientName = msg.ClientName;
                                        }
                                        else
                                        {
                                            // Try to extract from JSON directly
                                            using var doc = JsonDocument.Parse(line);
                                            if (doc.RootElement.TryGetProperty("clientName", out var clientNameElement))
                                            {
                                                clientName = clientNameElement.GetString();
                                            }
                                        }
                                    }
                                }
                                catch
                                {
                                    // Ignore parsing errors here - ProcessMessageAsync will handle it
                                }
                            }
                            
                            await ProcessMessageAsync(line, client);
                        }
                    }

                    // Keep the last line (which might be partial) in the builder
                    messageBuilder.Clear();
                    if (lines.Length > 0 && !fullMessage.EndsWith('\n'))
                    {
                        messageBuilder.Append(lines[lines.Length - 1]);
                    }
                }
            }
            catch (Exception ex)
            {
                await _databaseService.LogSystemActionAsync("Client Communication Error", clientName ?? "Unknown", "Error", ex.Message);
            }
            finally
            {
                if (clientName != null)
                {
                    _connectedClients.Remove(clientName);
                    _clientInfo.Remove(clientName);
                    ClientDisconnected?.Invoke(this, new ClientDisconnectedEventArgs(clientName));
                    await _databaseService.UpdateClientStatusAsync(clientName, "", false, "Offline");
                }
                client.Close();
            }
        }

        private async Task ProcessMessageAsync(string message, TcpClient client)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                var messageData = JsonSerializer.Deserialize<ClientMessage>(message, options);
                if (messageData == null) 
                {
                    await _databaseService.LogSystemActionAsync("Message Processing Error", "Unknown", "Error", "Failed to deserialize message");
                    return;
                }

                var clientEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
                // Extract clientName from message - check both ClientName and clientName properties
                var clientName = messageData.ClientName;
                
                // If ClientName is null, try to extract from JSON directly (for camelCase)
                if (string.IsNullOrWhiteSpace(clientName))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(message);
                        if (doc.RootElement.TryGetProperty("clientName", out var clientNameElement))
                        {
                            clientName = clientNameElement.GetString();
                        }
                    }
                    catch
                    {
                        // Ignore parsing errors
                    }
                }
                
                if (string.IsNullOrWhiteSpace(clientName))
                {
                    clientName = $"Client_{clientEndpoint}";
                }

                // Handle different message types
                switch (messageData.Type.ToLower())
                {
                    case "register":
                        await HandleClientRegistrationAsync(clientName, clientEndpoint, client);
                        break;
                    case "response":
                        await HandleClientResponseAsync(clientName, messageData);
                        break;
                    case "status":
                        await HandleStatusUpdateAsync(clientName, messageData);
                        break;
                    case "screen":
                        HandleScreenData(clientName, messageData);
                        break;
                    default:
                        await _databaseService.LogSystemActionAsync("Unknown Message Type", clientName, "Warning", $"Received unknown message type: {messageData.Type}");
                        break;
                }
            }
            catch (JsonException ex)
            {
                await _databaseService.LogSystemActionAsync("JSON Parse Error", "Unknown", "Error", $"Failed to parse JSON: {ex.Message}. Message: {message.Substring(0, Math.Min(100, message.Length))}");
            }
            catch (Exception ex)
            {
                await _databaseService.LogSystemActionAsync("Message Processing Error", "Unknown", "Error", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private async Task HandleClientRegistrationAsync(string clientName, string clientEndpoint, TcpClient client)
        {
            try
            {
                var ipAddress = clientEndpoint.Split(':')[0];
                
                _connectedClients[clientName] = client;
                _clientInfo[clientName] = new ClientInfo
                {
                    Name = clientName,
                    IpAddress = ipAddress,
                    LastResponse = DateTime.UtcNow,
                    IsConnected = true
                };

                // Update database - this should now work with the UNIQUE constraint
                await _databaseService.UpdateClientStatusAsync(clientName, ipAddress, true, "Online");
                
                // Log the registration
                await _databaseService.LogSystemActionAsync("Client Registered", clientName, "Success", $"Client {clientName} connected from {clientEndpoint}");
                
                // Notify UI
                ClientConnected?.Invoke(this, new ClientConnectedEventArgs(clientName, ipAddress));
            }
            catch (Exception ex)
            {
                await _databaseService.LogSystemActionAsync("Client Registration Error", clientName, "Error", $"Failed to register client: {ex.Message}");
                throw;
            }
        }

        private async Task HandleClientResponseAsync(string clientName, ClientMessage messageData)
        {
            if (_clientInfo.ContainsKey(clientName))
            {
                _clientInfo[clientName].LastResponse = DateTime.UtcNow;
            }

            // Skip logging heartbeats to avoid cluttering the logs
            if (!string.IsNullOrWhiteSpace(messageData.Data) && 
                messageData.Data.Equals("heartbeat", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await _databaseService.LogSystemActionAsync("Client Response", clientName, "Success", messageData.Data);
            CommandReceived?.Invoke(this, new CommandReceivedEventArgs(clientName, messageData.Data));
        }

        private async Task HandleStatusUpdateAsync(string clientName, ClientMessage messageData)
        {
            if (_clientInfo.ContainsKey(clientName))
            {
                _clientInfo[clientName].LastResponse = DateTime.UtcNow;
                _clientInfo[clientName].Status = messageData.Data;
            }

            await _databaseService.UpdateClientStatusAsync(clientName, _clientInfo[clientName].IpAddress, true, messageData.Data);
        }

        private void HandleScreenData(string clientName, ClientMessage messageData)
        {
            if (string.IsNullOrWhiteSpace(messageData.Data))
                return;

            try
            {
                // Validate base64 length (must be multiple of 4)
                var data = messageData.Data.Trim();
                var imageBytes = Convert.FromBase64String(data);

                if (imageBytes.Length == 0)
                    return;

                ScreenDataReceived?.Invoke(this, new ScreenDataReceivedEventArgs(
                    clientName, imageBytes, messageData.Metadata ?? new Dictionary<string, string>()));
            }
            catch (FormatException)
            {
                // Base64 was incomplete — frame was split across TCP packets
                // This should no longer happen after fixing the buffer
            }
        }

        public async Task SendCommandAsync(string clientName, string command, string? parameters = null)
        {
            if (!_connectedClients.ContainsKey(clientName))
            {
                await _databaseService.LogSystemActionAsync("Command Failed", clientName, "Error", "Client not connected");
                return;
            }

            try
            {
                var client = _connectedClients[clientName];
                var stream = client.GetStream();

                var commandMessage = new ServerMessage
                {
                    Type = "command",
                    Command = command,
                    Parameters = parameters,
                    Timestamp = DateTime.UtcNow
                };

                var message = JsonSerializer.Serialize(commandMessage);
                var data = Encoding.UTF8.GetBytes(message + "\n");

                await stream.WriteAsync(data.AsMemory());
                await stream.FlushAsync();

                await _databaseService.LogSystemActionAsync($"Command Sent: {command}", clientName, "Success", parameters);
            }
            catch (Exception ex)
            {
                await _databaseService.LogSystemActionAsync($"Command Failed: {command}", clientName, "Error", ex.Message);
            }
        }

        public async Task SendCommandToAllAsync(string command, string? parameters = null)
        {
            var tasks = _connectedClients.Keys.Select(clientName => SendCommandAsync(clientName, command, parameters));
            await Task.WhenAll(tasks);
        }

        public Dictionary<string, ClientInfo> GetConnectedClients()
        {
            return new Dictionary<string, ClientInfo>(_clientInfo);
        }

        public bool IsClientConnected(string clientName)
        {
            return _connectedClients.ContainsKey(clientName) && _connectedClients[clientName].Connected;
        }

        public Task StartScreenStreamAsync(string clientName, int intervalMs = 1000)
        {
            var payload = JsonSerializer.Serialize(new { interval = intervalMs });
            return SendCommandAsync(clientName, "start_screen_stream", payload);
        }

        public Task StopScreenStreamAsync(string clientName)
        {
            return SendCommandAsync(clientName, "stop_screen_stream");
        }

        public Task SendRemoteInputAsync(string clientName, string eventType, Dictionary<string, string> data)
        {
            var payload = JsonSerializer.Serialize(new RemoteInputPayload
            {
                Event = eventType,
                Data = data
            });

            return SendCommandAsync(clientName, "remote_input", payload);
        }
    }

    public class ClientInfo
    {
        public string Name { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public DateTime LastResponse { get; set; }
        public bool IsConnected { get; set; }
        public string Status { get; set; } = "Online";
    }

    public class ClientMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
        
        [JsonPropertyName("clientName")]
        public string? ClientName { get; set; }
        
        [JsonPropertyName("data")]
        public string Data { get; set; } = string.Empty;
        
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }
        
        [JsonPropertyName("metadata")]
        public Dictionary<string, string>? Metadata { get; set; }
    }

    public class ServerMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
        
        [JsonPropertyName("command")]
        public string Command { get; set; } = string.Empty;
        
        [JsonPropertyName("parameters")]
        public string? Parameters { get; set; }
        
        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }
    }

    public class ClientConnectedEventArgs : EventArgs
    {
        public string ClientName { get; }
        public string IpAddress { get; }

        public ClientConnectedEventArgs(string clientName, string ipAddress)
        {
            ClientName = clientName;
            IpAddress = ipAddress;
        }
    }

    public class ClientDisconnectedEventArgs : EventArgs
    {
        public string ClientName { get; }

        public ClientDisconnectedEventArgs(string clientName)
        {
            ClientName = clientName;
        }
    }

    public class CommandReceivedEventArgs : EventArgs
    {
        public string ClientName { get; }
        public string Response { get; }

        public CommandReceivedEventArgs(string clientName, string response)
        {
            ClientName = clientName;
            Response = response;
        }
    }

    public class ScreenDataReceivedEventArgs : EventArgs
    {
        public string ClientName { get; }
        public byte[] ImageBytes { get; }
        public Dictionary<string, string> Metadata { get; }

        public ScreenDataReceivedEventArgs(string clientName, byte[] imageBytes, Dictionary<string, string> metadata)
        {
            ClientName = clientName;
            ImageBytes = imageBytes;
            Metadata = metadata;
        }
    }

    public class RemoteInputPayload
    {
        public string Event { get; set; } = string.Empty;
        public Dictionary<string, string> Data { get; set; } = new();
    }
}
