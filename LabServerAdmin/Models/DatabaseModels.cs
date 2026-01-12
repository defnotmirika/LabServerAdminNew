using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LabServerAdmin.Models
{
    public class User
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        [MaxLength(50)]
        public string Username { get; set; } = string.Empty;
        
        [Required]
        [MaxLength(255)]
        public string Password { get; set; } = string.Empty; // Hashed password
        
        [Required]
        [MaxLength(20)]
        public string Role { get; set; } = "Student"; // Admin, Student, Teacher
        
        [MaxLength(100)]
        public string? Email { get; set; }
        
        [MaxLength(100)]
        public string? FullName { get; set; }
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        
        public bool IsActive { get; set; } = true;
    }

    public class Computer
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        [MaxLength(100)]
        public string ClientName { get; set; } = string.Empty;
        
        [Required]
        public string IpAddress { get; set; } = string.Empty;
        
        [MaxLength(17)]
        public string? MacAddress { get; set; }
        
        [MaxLength(20)]
        public string Status { get; set; } = "Offline"; // Online, Offline, Maintenance
        
        public bool IsLocked { get; set; } = false;
        
        public bool IsOnline { get; set; } = false;
        
        public DateTime? LastSeen { get; set; }
        
        [MaxLength(100)]
        public string? Location { get; set; }
        
        [MaxLength(50)]
        public string? LabRoom { get; set; }
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class SystemLog
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        [MaxLength(10)]
        public string LogLevel { get; set; } = "INFO"; // INFO, WARNING, ERROR, DEBUG
        
        [Required]
        public string LogMessage { get; set; } = string.Empty;
        
        public DateTime LogTime { get; set; } = DateTime.UtcNow;
        
        public int? ComputerId { get; set; }
        
        public int? UserId { get; set; }
        
        [MaxLength(50)]
        public string? ActionType { get; set; } // Login, Logout, Lock, Unlock, etc.
        
        // Computed properties for display compatibility
        public DateTime Timestamp => LogTime;
        
        public string Action => ActionType ?? LogMessage;
        
        public string ClientName { get; set; } = string.Empty; // Populated from join
        
        public string Status => LogLevel;
        
        public string? Details => LogMessage;
    }

    public class AttendanceLog
    {
        [Key]
        public int Id { get; set; }
        
        public int? UserId { get; set; }
        
        public int? ComputerId { get; set; }
        
        public DateTime LoginTime { get; set; }
        
        public DateTime? LogoutTime { get; set; }
        
        public TimeSpan? SessionDuration { get; set; }
        
        [MaxLength(20)]
        public string Status { get; set; } = "Active"; // Active, Completed, Forced_Logout
        
        // Computed properties for display compatibility
        public string StudentName { get; set; } = string.Empty; // Populated from join
        
        public string PcName { get; set; } = string.Empty; // Populated from join
        
        public DateTime TimeIn => LoginTime;
        
        public DateTime? TimeOut => LogoutTime;
        
        public bool IsActive => Status == "Active";

        // Computed property for display
        public string Duration
        {
            get
            {
                if (LogoutTime.HasValue && LoginTime != default)
                {
                    var duration = LogoutTime.Value - LoginTime;
                    return $"{duration.Hours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
                }
                if (SessionDuration.HasValue)
                {
                    var duration = SessionDuration.Value;
                    return $"{duration.Hours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
                }
                return "Active";
            }
        }
    }

    public class ConnectedClient
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        [MaxLength(100)]
        public string Name { get; set; } = string.Empty;
        
        [Required]
        [MaxLength(45)]
        public string IpAddress { get; set; } = string.Empty;
        
        public DateTime LastResponse { get; set; } = DateTime.UtcNow;
        
        public bool IsConnected { get; set; } = true;
        
        public string Status { get; set; } = "Online";
    }
}
