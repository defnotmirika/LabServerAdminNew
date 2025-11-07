# Lab Server PC Management System - Separate Applications

## 🎯 Overview

The Lab Server PC Management System has been restructured into **two completely separate applications** that can be built, deployed, and maintained independently.

## 📁 Project Structure

```
LabServer2/
├── LabServerAdmin/                  # 🔧 Admin Application (Server)
│   ├── Models/
│   │   └── DatabaseModels.cs        # Database entities
│   ├── Services/
│   │   ├── DatabaseService.cs       # PostgreSQL operations
│   │   ├── TcpServerService.cs      # TCP server implementation
│   │   └── VoiceRecognitionService.cs # Voice command processing
│   ├── MainWindow.xaml              # Admin UI
│   ├── MainWindow.xaml.cs           # Admin logic
│   ├── App.xaml                     # Admin app entry point
│   ├── App.xaml.cs                  # Admin app logic
│   ├── LabServerAdmin.csproj        # Admin project file
│   └── appsettings.json            # Admin configuration
├── LabServerClient/                 # 🖥️ Client Application
│   ├── ClientWindow.xaml            # Client UI
│   ├── ClientWindow.xaml.cs         # Client logic
│   ├── App.xaml                     # Client app entry point
│   ├── App.xaml.cs                  # Client app logic
│   └── LabServerClient.csproj       # Client project file
├── LabServerAdmin.sln               # Admin solution file
├── LabServerClient.sln               # Client solution file
├── build-admin.bat                  # Build admin script
├── build-client.bat                 # Build client script
├── build-all.bat                    # Build both scripts
├── database_setup.sql               # Database schema
├── README.md                        # Documentation
└── DEPLOYMENT.md                    # Deployment guide
```

## 🚀 Building the Applications

### Individual Builds

**Build Admin Application:**
```bash
dotnet build LabServerAdmin.sln --configuration Release
# or
build-admin.bat
```

**Build Client Application:**
```bash
dotnet build LabServerClient.sln --configuration Release
# or
build-client.bat
```

**Build Both Applications:**
```bash
build-all.bat
```

### Running from Source

**Run Admin Application:**
```bash
dotnet run --project LabServerAdmin/LabServerAdmin.csproj
```

**Run Client Application:**
```bash
dotnet run --project LabServerClient/LabServerClient.csproj
```

## 🔧 Admin Application Features

### Core Components
- **DatabaseService** - PostgreSQL database operations
- **TcpServerService** - TCP server for client communication
- **VoiceRecognitionService** - Voice command processing
- **Modern WPF UI** - Dashboard with real-time monitoring

### Key Features
- ✅ TCP Server on port 9000
- ✅ PostgreSQL database integration
- ✅ Voice recognition for commands
- ✅ Real-time client monitoring
- ✅ Bulk operations (Lock All, Shutdown All, etc.)
- ✅ Individual PC control
- ✅ System logs and attendance tracking
- ✅ Export functionality (CSV/TXT)
- ✅ Admin authentication

### Dependencies
- Npgsql (PostgreSQL)
- System.Speech (Voice recognition)
- Microsoft.Extensions.* (Dependency injection)
- BCrypt.Net-Next (Password hashing)

## 🖥️ Client Application Features

### Core Components
- **TCP Client** - Connection to admin server
- **System Control** - Windows API integration
- **Configuration** - Registry-based settings
- **Auto-startup** - Windows startup integration

### Key Features
- ✅ TCP Client connection
- ✅ System control (lock, unlock, shutdown, restart, sleep)
- ✅ Auto-startup on Windows boot
- ✅ Status monitoring and heartbeat
- ✅ Configuration persistence
- ✅ Minimal UI for management

### Dependencies
- System.Speech (Optional)
- Microsoft.Win32.Registry (Configuration)

## 📊 Deployment Options

### Option 1: Source Code Deployment
1. **Deploy source code** to target machines
2. **Build on each machine** using build scripts
3. **Run from source** using dotnet run commands

### Option 2: Compiled Executables
1. **Build applications** using build scripts
2. **Copy executables** to target machines
3. **Run executables** directly

### Option 3: Mixed Deployment
1. **Build admin** on server machine
2. **Build clients** on each PC
3. **Deploy independently**

## 🔒 Security Considerations

### Admin Application
- **Database security** - Secure PostgreSQL configuration
- **Network security** - TCP server on LAN only
- **Authentication** - BCrypt password hashing
- **Logging** - Comprehensive audit trails

### Client Application
- **Registry security** - Secure configuration storage
- **Network security** - TCP client to admin only
- **System permissions** - Proper Windows API usage
- **Auto-startup** - Secure registry integration

## 🛠️ Maintenance

### Independent Updates
- **Admin updates** - Deploy new admin version
- **Client updates** - Deploy new client versions
- **Database migrations** - Handle schema changes
- **Configuration changes** - Update appsettings.json

### Version Management
- **Separate versioning** - Admin and client can have different versions
- **Compatibility** - Ensure protocol compatibility
- **Rollback capability** - Independent rollback options

## 📈 Benefits of Separate Applications

### Development Benefits
- ✅ **Independent development** - Teams can work separately
- ✅ **Separate versioning** - Different release cycles
- ✅ **Focused testing** - Test each application independently
- ✅ **Cleaner codebase** - No shared dependencies

### Deployment Benefits
- ✅ **Flexible deployment** - Deploy admin and clients separately
- ✅ **Independent updates** - Update one without affecting the other
- ✅ **Scalability** - Add more clients without changing admin
- ✅ **Maintenance** - Easier to maintain and troubleshoot

### Operational Benefits
- ✅ **Reduced complexity** - Simpler individual applications
- ✅ **Better performance** - Optimized for specific roles
- ✅ **Easier troubleshooting** - Isolated issues
- ✅ **Flexible configuration** - Independent settings

## 🎯 Next Steps

1. **Test both applications** independently
2. **Deploy admin** to server machine
3. **Deploy clients** to PC machines
4. **Configure network** and database
5. **Test communication** between applications
6. **Monitor and maintain** the system

---

**Note**: Both applications are now completely independent and can be developed, built, and deployed separately while maintaining full compatibility through the TCP communication protocol.
