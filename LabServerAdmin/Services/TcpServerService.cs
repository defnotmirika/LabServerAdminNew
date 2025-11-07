using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var messageBuilder = new StringBuilder();

                while (client.Connected)
                {
                    var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    messageBuilder.Append(message);

                    // Process complete messages (assuming they end with newline)
                    var fullMessage = messageBuilder.ToString();
                    var lines = fullMessage.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                    foreach (var line in lines)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            await ProcessMessageAsync(line.Trim(), client);
                        }
                    }

                    messageBuilder.Clear();
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
                    await _databaseService.UpdateClientStatusAsync(clientName, "", false, "Disconnected");
                }
                client.Close();
            }
        }

        private async Task ProcessMessageAsync(string message, TcpClient client)
        {
            try
            {
                var messageData = JsonSerializer.Deserialize<ClientMessage>(message);
                if (messageData == null) return;

                var clientEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
                var clientName = messageData.ClientName ?? $"Client_{clientEndpoint}";

                // Handle different message types
                switch (messageData.Type)
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
                }
            }
            catch (Exception ex)
            {
                await _databaseService.LogSystemActionAsync("Message Processing Error", "Unknown", "Error", ex.Message);
            }
        }

        private async Task HandleClientRegistrationAsync(string clientName, string clientEndpoint, TcpClient client)
        {
            _connectedClients[clientName] = client;
            _clientInfo[clientName] = new ClientInfo
            {
                Name = clientName,
                IpAddress = clientEndpoint.Split(':')[0],
                LastResponse = DateTime.UtcNow,
                IsConnected = true
            };

            await _databaseService.UpdateClientStatusAsync(clientName, _clientInfo[clientName].IpAddress, true, "Online");
            ClientConnected?.Invoke(this, new ClientConnectedEventArgs(clientName, _clientInfo[clientName].IpAddress));

            await _databaseService.LogSystemActionAsync("Client Registered", clientName, "Success", $"Client {clientName} connected from {clientEndpoint}");
        }

        private async Task HandleClientResponseAsync(string clientName, ClientMessage messageData)
        {
            if (_clientInfo.ContainsKey(clientName))
            {
                _clientInfo[clientName].LastResponse = DateTime.UtcNow;
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
            {
                return;
            }

            try
            {
                var imageBytes = Convert.FromBase64String(messageData.Data);
                ScreenDataReceived?.Invoke(this, new ScreenDataReceivedEventArgs(clientName, imageBytes, messageData.Metadata ?? new Dictionary<string, string>()));
            }
            catch
            {
                // Ignore invalid data; streaming should continue with next frame
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

                await stream.WriteAsync(data, 0, data.Length);
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
        public string Type { get; set; } = string.Empty;
        public string? ClientName { get; set; }
        public string Data { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
    }

    public class ServerMessage
    {
        public string Type { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string? Parameters { get; set; }
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
