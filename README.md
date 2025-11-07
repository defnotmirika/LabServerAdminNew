# Lab Server PC Management System

A comprehensive TCP-based PC management system for computer labs, featuring voice commands, attendance tracking, and remote system control.

## 🏗️ System Architecture

The system consists of **two separate applications**:

### 🔧 Lab Server Admin (Server Application)
- **TCP Server** running on port 9000
- **PostgreSQL Database** for data storage
- **Voice Recognition** for hands-free control
- **Modern WPF UI** with real-time client monitoring
- **Export functionality** for logs and attendance
- **Independent project** with its own solution file

### 🖥️ Lab Server Client (Client Application)
- **TCP Client** connecting to admin server
- **System control** (lock, unlock, shutdown, restart, sleep)
- **Auto-startup** on Windows boot
- **Status monitoring** and heartbeat
- **Minimal UI** for configuration
- **Independent project** with its own solution file

## 🚀 Quick Start

### Prerequisites
- Windows 10/11
- .NET 8.0 Runtime
- PostgreSQL 12+ (for admin app)
- Microphone (for voice commands)

### Installation

#### Option 1: Build from Source
1. **Build Admin Application**
   ```bash
   dotnet build LabServerAdmin.sln --configuration Release
   # or use: build-admin.bat
   ```

2. **Build Client Application**
   ```bash
   dotnet build LabServerClient.sln --configuration Release
   # or use: build-client.bat
   ```

3. **Build Both Applications**
   ```bash
   build-all.bat
   ```

#### Option 2: Run from Source
1. **Run Admin Application**
   ```bash
   dotnet run --project LabServerAdmin/LabServerAdmin.csproj
   ```

2. **Run Client Application**
   ```bash
   dotnet run --project LabServerClient/LabServerClient.csproj
   ```

### Database Setup
1. **Setup PostgreSQL Database**
   ```sql
   CREATE DATABASE labserver;
   CREATE USER labuser WITH PASSWORD 'yourpassword';
   GRANT ALL PRIVILEGES ON DATABASE labserver TO labuser;
   ```

2. **Configure Connection String**
   - Edit `LabServerAdmin/appsettings.json`
   - Update the connection string with your PostgreSQL credentials

### Deployment
1. **Deploy Admin Application**
   - Run `LabServerAdmin.exe` on the admin machine
   - Click "Start Server" to begin listening for clients
   - Enable voice commands if desired

2. **Deploy Client Application**
   - Copy `LabServerClient.exe` to each PC
   - Run the client on each PC
   - Configure server IP address (default: 192.168.1.100)
   - Client will auto-start on Windows boot

## 🎯 Features

### Admin Dashboard
- **Real-time client monitoring** with connection status
- **Bulk operations**: Lock/Unlock/Shutdown/Restart all PCs
- **Individual PC control** with action buttons
- **System logs** with command history and responses
- **Attendance tracking** with student login/logout times
- **Export functionality** for CSV/TXT reports

### Voice Commands
- **"Lock PC 1"** - Lock specific PC
- **"Unlock all computers"** - Unlock all connected PCs
- **"Shutdown PC 3"** - Shutdown specific PC
- **"Restart all"** - Restart all connected PCs
- **"Sleep PC 2"** - Put specific PC to sleep

### Client Features
- **Automatic connection** to admin server
- **System control execution** via Windows APIs
- **Status reporting** and heartbeat
- **Configuration persistence** in Windows Registry
- **Startup integration** for automatic launch

## 🗄️ Database Schema

### Tables
- **`admins`** - Admin user credentials
- **`system_logs`** - Command execution logs
- **`attendance_logs`** - Student attendance tracking
- **`connected_clients`** - Client connection status

### Default Admin Account
- **Username**: `admin`
- **Password**: `admin123`
- Change these credentials after first login!

## 🔧 Configuration

### Admin App Settings
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=labserver;Username=postgres;Password=password"
  },
  "ServerSettings": {
    "Port": 9000,
    "HeartbeatInterval": 30
  }
}
```

### Client App Settings
- Server IP address (stored in Windows Registry)
- PC Name identifier
- Auto-startup configuration

## 📊 Usage Examples

### Basic Operations
1. **Start the admin server** and wait for clients to connect
2. **Use voice commands** or click buttons to control PCs
3. **Monitor system logs** for command execution status
4. **Export attendance data** for reporting

### Voice Command Examples
- "Lock PC 1" - Locks the first PC
- "Unlock all computers" - Unlocks all connected PCs
- "Shutdown PC 3" - Shuts down the third PC
- "Restart all" - Restarts all connected PCs

### Bulk Operations
- Use the dashboard buttons to control all connected PCs at once
- Individual PC control via action buttons in the client list
- Real-time status updates and response monitoring

## 🔒 Security Features

- **Password hashing** using BCrypt
- **TCP communication** over LAN (not internet-exposed)
- **Admin authentication** required for system access
- **Command logging** for audit trails
- **Confirmation dialogs** for destructive operations

## 🛠️ Technical Details

### Communication Protocol
- **TCP sockets** on port 9000
- **JSON message format** for commands and responses
- **Heartbeat mechanism** for connection monitoring
- **Automatic reconnection** handling

### System Requirements
- **Admin**: Windows 10/11, .NET 8.0, PostgreSQL
- **Client**: Windows 10/11, .NET 8.0
- **Network**: LAN connectivity between admin and clients

### Dependencies
- **Npgsql** - PostgreSQL database access
- **System.Speech** - Voice recognition
- **BCrypt.Net-Next** - Password hashing
- **Microsoft.Extensions** - Dependency injection and configuration

## 📝 Troubleshooting

### Common Issues
1. **Client won't connect**: Check server IP and port 9000
2. **Voice commands not working**: Ensure microphone is enabled
3. **Database connection failed**: Verify PostgreSQL is running and credentials are correct
4. **Commands not executing**: Check Windows permissions and firewall settings

### Log Files
- Admin logs are stored in the database
- Client logs are displayed in the client application
- Export functionality available for both system and attendance logs

## 🔄 Updates and Maintenance

### Regular Tasks
- **Monitor system logs** for errors
- **Export attendance data** regularly
- **Update client applications** as needed
- **Backup database** periodically

### Performance Optimization
- **Limit log retention** to prevent database bloat
- **Monitor network traffic** for large deployments
- **Regular database maintenance** for optimal performance

## 📞 Support

For technical support or feature requests, please refer to the system logs and ensure all prerequisites are properly configured.

---

**Note**: This system is designed for educational environments and requires proper network security measures when deployed in production environments.
