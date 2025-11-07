using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LabServerAdmin.Models
{
    public class Admin
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        [MaxLength(50)]
        public string Username { get; set; } = string.Empty;
        
        [Required]
        [MaxLength(255)]
        public string PasswordHash { get; set; } = string.Empty;
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class SystemLog
    {
        [Key]
        public int Id { get; set; }
        
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        
        [Required]
        [MaxLength(100)]
        public string Action { get; set; } = string.Empty;
        
        [MaxLength(100)]
        public string ClientName { get; set; } = string.Empty;
        
        [MaxLength(50)]
        public string Status { get; set; } = string.Empty;
        
        [MaxLength(500)]
        public string? Details { get; set; }
    }

    public class AttendanceLog
    {
        [Key]
        public int Id { get; set; }
        
        [Required]
        [MaxLength(100)]
        public string StudentName { get; set; } = string.Empty;
        
        [Required]
        [MaxLength(100)]
        public string PcName { get; set; } = string.Empty;
        
        public DateTime TimeIn { get; set; }
        
        public DateTime? TimeOut { get; set; }
        
        public bool IsActive { get; set; } = true;
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
