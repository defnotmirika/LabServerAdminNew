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
        
        [MaxLength(45)]
        public string IpAddress { get; set; } = string.Empty;
        
        public int? LabId { get; set; }
        
        [MaxLength(20)]
        public string Status { get; set; } = "Offline"; // Online, Offline, Maintenance
        
        public bool IsLocked { get; set; } = false;
        
        public bool IsOnline { get; set; } = false;
        
        public DateTime? LastSeen { get; set; }
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        
        // Computed properties for backward compatibility
        [NotMapped]
        public string? MacAddress { get; set; }
        
        [NotMapped]
        public string? Location { get; set; }
        
        [NotMapped]
        public string? LabRoom { get; set; }
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
        
        [MaxLength(20)]
        public string? UserIdString { get; set; }
        
        [MaxLength(50)]
        public string? ActionType { get; set; } // Login, Logout, Lock, Unlock, etc.
        
        // Backward compatibility - UserId as int (deprecated)
        [NotMapped]
        public int? UserId 
        { 
            get => string.IsNullOrWhiteSpace(UserIdString) ? null : (int.TryParse(UserIdString, out var id) ? id : null);
            set => UserIdString = value?.ToString();
        }
        
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
        
        [MaxLength(20)]
        public string? StudNo { get; set; }
        
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

        // Backward compatibility - UserId as int (deprecated)
        [NotMapped]
        public int? UserId 
        { 
            get => string.IsNullOrWhiteSpace(StudNo) ? null : (int.TryParse(StudNo, out var id) ? id : null);
            set => StudNo = value?.ToString();
        }

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

    public class ActivityLog
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        [MaxLength(20)]
        public string StudNo { get; set; } = string.Empty;
        
        [Required]
        public int ComputerId { get; set; }
        
        [Required]
        [MaxLength(100)]
        public string Action { get; set; } = string.Empty;
        
        public string? Description { get; set; }
        
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        
        // Computed properties for display (populated from joins)
        public string StudentName { get; set; } = string.Empty;
        
        public string PcName { get; set; } = string.Empty;
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

    /// <summary>
    /// Represents a login request from a client application
    /// </summary>
    public class LoginRequest
    {
        [Key]
        public int Id { get; set; }
        
        /// <summary>
        /// The student number requesting to log in
        /// </summary>
        [Required]
        [MaxLength(20)]
        public string StudNo { get; set; } = string.Empty;
        
        /// <summary>
        /// The resolved student full name (from join)
        /// </summary>
        [NotMapped]
        public string? StudentName { get; set; }
        
        /// <summary>
        /// The computer identifier making the request
        /// </summary>
        public int ComputerId { get; set; }
        
        /// <summary>
        /// The PC name (client name) making the request (from join)
        /// </summary>
        [MaxLength(100)]
        public string PcName { get; set; } = string.Empty;
        
        /// <summary>
        /// IP address of the client making the request
        /// </summary>
        [MaxLength(45)]
        public string? IpAddress { get; set; }
        
        /// <summary>
        /// Type of request: Login or Logout
        /// </summary>
        [Required]
        [MaxLength(20)]
        public string RequestType { get; set; } = "Login";
        
        /// <summary>
        /// Timestamp when the request was created
        /// </summary>
        public DateTime RequestTimestamp { get; set; } = DateTime.UtcNow;
        
        /// <summary>
        /// Optional message from the client
        /// </summary>
        [MaxLength(500)]
        public string? RequestMessage { get; set; }
        
        /// <summary>
        /// Status: Pending, Approved, Declined
        /// </summary>
        [Required]
        [MaxLength(20)]
        public string Status { get; set; } = "Pending";
        
        /// <summary>
        /// Username of the admin who approved/declined
        /// </summary>
        [MaxLength(50)]
        public string? ProcessedBy { get; set; }
        
        /// <summary>
        /// Timestamp when the request was processed
        /// </summary>
        public DateTime? ProcessedTimestamp { get; set; }
        
        /// <summary>
        /// Computed property for display - formatted request time
        /// </summary>
        [NotMapped]
        public string RequestTimeFormatted => RequestTimestamp.ToString("yyyy-MM-dd HH:mm:ss");
        
        /// <summary>
        /// Computed property for display - pending status check
        /// </summary>
        [NotMapped]
        public bool IsPending => Status == "Pending";
    }

    public class InstructorClassListItem
    {
        [MaxLength(20)]
        public string StudNo { get; set; } = string.Empty;
        
        [MaxLength(100)]
        public string FirstName { get; set; } = string.Empty;
        
        [MaxLength(100)]
        public string LastName { get; set; } = string.Empty;
        
        [MaxLength(50)]
        public string SectionName { get; set; } = string.Empty;
        
        public DateTime? LoginTime { get; set; }
        
        public DateTime? LogoutTime { get; set; }
        
        [MaxLength(20)]
        public string? Status { get; set; }
        
        [MaxLength(100)]
        public string? ClientName { get; set; }
        
        [NotMapped]
        public string FullName => string.IsNullOrWhiteSpace(FirstName) && string.IsNullOrWhiteSpace(LastName)
            ? string.Empty
            : $"{LastName}, {FirstName}".Trim(',', ' ');
    }
}
