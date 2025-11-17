# Lab Server PC Management System - Deployment Guide

## 📋 Pre-Deployment Checklist

### System Requirements
- [ ] Windows 10/11 on all machines
- [ ] .NET 8.0 Runtime installed on all machines
- [ ] PostgreSQL 12+ installed on admin machine **or** access to a Supabase project
- [ ] Network connectivity between admin and client machines
- [ ] Microphone available for voice commands (optional)

### Network Configuration
- [ ] Admin machine has static IP address (e.g., 192.168.1.100)
- [ ] Port 9000 is open on admin machine firewall
- [ ] All client machines can reach admin machine on port 9000

## 🚀 Step-by-Step Deployment

### 1. Database Setup

#### Option A – Self-hosted PostgreSQL
1. **Install PostgreSQL** on the admin machine
2. **Create database**:
   ```sql
   CREATE DATABASE labserver;
   ```
3. **Run the setup script**:
   ```bash
   psql -U postgres -d labserver -f database_setup.sql
   ```
4. **Update connection string** in `appsettings.json`:
   ```json
   {
     "ConnectionStrings": {
       "DefaultConnection": "Host=localhost;Database=labserver;Username=postgres;Password=yourpassword"
     }
   }
   ```

#### Option B – Supabase Managed PostgreSQL
1. **Create (or reuse) a Supabase project** and open the **Project Settings → Database** tab.
2. **Copy the pooled connection string** (or build one manually) and note:
   - Host (e.g., `aws-0-ap-southeast-1.pooler.supabase.com`)
   - Port (commonly `6543` for pooled connections)
   - Database (default `postgres`)
   - User (e.g., `postgres.your-project-ref`)
   - Password (Supabase-generated)
3. **Enable SSL** on the connection string. Supabase requires at least:
   ```
   Ssl Mode=Require;Trust Server Certificate=true
   ```
4. **Provide the connection string to both apps** using one of:
   - Set the environment variable `SUPABASE_DB_CONNECTION`
   - Edit `appsettings.json` and set `Supabase:ConnectionString`
5. **Run `database_setup.sql`** against the Supabase database once to provision tables:
   ```bash
   psql "<your_supabase_connection_string>" -f database_setup.sql
   ```

### 2. Admin Application Deployment

1. **Build the admin application**:
   ```bash
   dotnet build LabServer2.csproj --configuration Release
   ```

2. **Copy files** to admin machine:
   - `LabServer2.exe`
   - `appsettings.json`
   - All dependencies (handled by .NET)

3. **Run the admin application**:
   ```bash
   dotnet run --project LabServer2.csproj
   ```

4. **Configure admin account**:
   - Default: username `admin`, password `admin123`
   - Change these credentials after first login!

### 3. Client Application Deployment

1. **Build the client application**:
   ```bash
   dotnet build ClientApp/ClientApp.csproj --configuration Release
   ```

2. **Deploy to each client PC**:
   - Copy `ClientApp` folder to each client machine
   - Run `ClientApp.exe` on each client
   - Configure server IP address (admin machine IP)
   - Set PC name identifier

3. **Configure auto-startup**:
   - Client automatically adds itself to Windows startup
   - Registry key: `HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`

### 4. Network Configuration

1. **Admin machine**:
   - Set static IP address (e.g., 192.168.1.100)
   - Open Windows Firewall for port 9000
   - Ensure PostgreSQL is accessible

2. **Client machines**:
   - Configure to connect to admin IP address
   - Ensure network connectivity
   - Test connection from client to admin

## 🔧 Configuration Files

### Admin App Configuration (`appsettings.json`)
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=labserver;Username=postgres;Password=yourpassword"
  },
  "Supabase": {
    "ConnectionString": "Host=YOUR_SUPABASE_HOST;Port=6543;Database=postgres;Username=postgres.YOUR_PROJECT_REF;Password=YOUR_SUPABASE_PASSWORD;Ssl Mode=Require;Trust Server Certificate=true"
  },
  "ServerSettings": {
    "Port": 9000,
    "HeartbeatInterval": 30
  },
  "VoiceRecognition": {
    "Enabled": true,
    "Language": "en-US"
  }
}
```

### Client App Configuration
- Stored in Windows Registry
- Key: `HKEY_CURRENT_USER\SOFTWARE\LabServerClient`
- Values: `ServerIP`, `PCName`
- Optional: Set `SUPABASE_DB_CONNECTION` environment variable or `Supabase:ConnectionString` in `appsettings.json` (if using local config file alongside the client) to point at your Supabase database.

## 🧪 Testing Deployment

### 1. Admin Application Test
1. Start the admin application
2. Click "Start Server" - should show "Server: Running on Port 9000"
3. Check database connection in logs
4. Verify voice recognition (if enabled)

### 2. Client Application Test
1. Start client application on a test machine
2. Configure server IP address
3. Click "Connect" - should show "Connected" status
4. Verify client appears in admin dashboard

### 3. Command Testing
1. From admin dashboard, try sending commands:
   - Lock/Unlock
   - Shutdown (with confirmation)
   - Restart (with confirmation)
   - Sleep
2. Verify commands execute on client
3. Check system logs for command history

### 4. Voice Command Testing
1. Enable voice recognition in admin app
2. Test voice commands:
   - "Lock PC 1"
   - "Unlock all computers"
   - "Shutdown PC 2"
3. Verify commands are recognized and executed

## 🔒 Security Considerations

### Network Security
- [ ] Use VPN or secure network for remote access
- [ ] Implement proper firewall rules
- [ ] Consider SSL/TLS for production environments
- [ ] Regular security updates on all machines

### Database Security
- [ ] Use strong passwords for database users
- [ ] Limit database access to application only
- [ ] Regular database backups
- [ ] Monitor database logs for suspicious activity

### Application Security
- [ ] Change default admin credentials
- [ ] Implement proper user authentication
- [ ] Log all administrative actions
- [ ] Regular application updates

## 📊 Monitoring and Maintenance

### Daily Tasks
- [ ] Check system logs for errors
- [ ] Monitor client connection status
- [ ] Verify voice recognition functionality
- [ ] Test critical commands

### Weekly Tasks
- [ ] Export system logs
- [ ] Review attendance data
- [ ] Update client applications if needed
- [ ] Database maintenance

### Monthly Tasks
- [ ] Full system backup
- [ ] Security audit
- [ ] Performance review
- [ ] Update documentation

## 🚨 Troubleshooting

### Common Issues

1. **Client won't connect**:
   - Check server IP address
   - Verify port 9000 is open
   - Check Windows Firewall settings
   - Test network connectivity

2. **Voice commands not working**:
   - Ensure microphone is enabled
   - Check Windows Speech Recognition settings
   - Verify voice recognition is enabled in admin app

3. **Database connection failed**:
   - Verify PostgreSQL is running
   - Check connection string in appsettings.json
   - Ensure database user has proper permissions

4. **Commands not executing**:
   - Check Windows permissions
   - Verify client application is running
   - Review system logs for errors

### Log Locations
- **Admin logs**: Stored in PostgreSQL database
- **Client logs**: Displayed in client application
- **System logs**: Windows Event Viewer
- **Database logs**: PostgreSQL log files

## 📞 Support

### Getting Help
1. Check system logs first
2. Review this deployment guide
3. Verify all prerequisites are met
4. Test individual components

### Emergency Procedures
1. **System-wide shutdown**: Use admin dashboard "Shutdown All"
2. **Emergency unlock**: Physical access to machines required
3. **Database recovery**: Use PostgreSQL backup/restore procedures
4. **Network issues**: Check connectivity and firewall settings

---

**Note**: This system is designed for educational environments. For production use, implement additional security measures and regular security audits.
