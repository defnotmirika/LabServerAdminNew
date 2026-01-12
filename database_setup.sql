CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

-- Users/Admins table
CREATE TABLE IF NOT EXISTS users (
    id SERIAL PRIMARY KEY,
    username VARCHAR(50) UNIQUE NOT NULL,
    password VARCHAR(255) NOT NULL, -- Store hashed passwords
    role VARCHAR(20) NOT NULL DEFAULT 'Student', -- Admin, Student, Teacher
    email VARCHAR(100),
    full_name VARCHAR(100),
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    is_active BOOLEAN DEFAULT TRUE
);

-- Computers/Clients table
CREATE TABLE IF NOT EXISTS computers (
    id SERIAL PRIMARY KEY,
    client_name VARCHAR(100) UNIQUE NOT NULL,
    ip_address INET NOT NULL,
    mac_address VARCHAR(17),
    status VARCHAR(20) DEFAULT 'Offline', -- Online, Offline, Maintenance
    is_locked BOOLEAN DEFAULT FALSE,
    is_online BOOLEAN DEFAULT FALSE,
    last_seen TIMESTAMP,
    location VARCHAR(100),
    lab_room VARCHAR(50),
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- System logs table
CREATE TABLE IF NOT EXISTS system_logs (
    id SERIAL PRIMARY KEY,
    log_level VARCHAR(10) NOT NULL, -- INFO, WARNING, ERROR, DEBUG
    log_message TEXT NOT NULL,
    log_time TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    computer_id INTEGER REFERENCES computers(id),
    user_id INTEGER REFERENCES users(id),
    action_type VARCHAR(50) -- Login, Logout, Lock, Unlock, etc.
);

-- Activity logs table (for user activities)
CREATE TABLE IF NOT EXISTS activity_logs (
    id SERIAL PRIMARY KEY,
    user_id INTEGER REFERENCES users(id),
    computer_id INTEGER REFERENCES computers(id),
    action VARCHAR(100) NOT NULL,
    description TEXT,
    timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    ip_address INET
);

-- Attendance logs table
CREATE TABLE IF NOT EXISTS attendance_logs (
    id SERIAL PRIMARY KEY,
    user_id INTEGER REFERENCES users(id),
    computer_id INTEGER REFERENCES computers(id),
    login_time TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    logout_time TIMESTAMP,
    session_duration INTERVAL,
    status VARCHAR(20) DEFAULT 'Active' -- Active, Completed, Forced_Logout
);

-- Lab sessions table
CREATE TABLE IF NOT EXISTS lab_sessions (
    id SERIAL PRIMARY KEY,
    session_name VARCHAR(100) NOT NULL,
    lab_room VARCHAR(50),
    start_time TIMESTAMP,
    end_time TIMESTAMP,
    instructor_id INTEGER REFERENCES users(id),
    is_active BOOLEAN DEFAULT FALSE,
    max_students INTEGER DEFAULT 30,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Session participants
CREATE TABLE IF NOT EXISTS session_participants (
    id SERIAL PRIMARY KEY,
    session_id INTEGER REFERENCES lab_sessions(id),
    user_id INTEGER REFERENCES users(id),
    computer_id INTEGER REFERENCES computers(id),
    joined_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    left_at TIMESTAMP,
    status VARCHAR(20) DEFAULT 'Active' -- Active, Left, Removed
);

-- Settings table
CREATE TABLE IF NOT EXISTS settings (
    id SERIAL PRIMARY KEY,
    setting_key VARCHAR(100) UNIQUE NOT NULL,
    setting_value TEXT,
    description TEXT,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Create indexes for better performance
CREATE INDEX IF NOT EXISTS idx_computers_ip ON computers(ip_address);
CREATE INDEX IF NOT EXISTS idx_computers_status ON computers(status);
CREATE INDEX IF NOT EXISTS idx_system_logs_time ON system_logs(log_time);
CREATE INDEX IF NOT EXISTS idx_activity_logs_user ON activity_logs(user_id);
CREATE INDEX IF NOT EXISTS idx_activity_logs_time ON activity_logs(timestamp);
CREATE INDEX IF NOT EXISTS idx_attendance_logs_user ON attendance_logs(user_id);
CREATE INDEX IF NOT EXISTS idx_attendance_logs_time ON attendance_logs(login_time);

-- Create triggers for updated_at timestamps
CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$$ language 'plpgsql';

CREATE TRIGGER update_users_updated_at BEFORE UPDATE ON users
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

CREATE TRIGGER update_computers_updated_at BEFORE UPDATE ON computers
    FOR EACH ROW EXECUTE FUNCTION update_updated_at_column();

-- Insert default admin user (password should be hashed in production)
INSERT INTO users (username, password, role, full_name, email) 
VALUES ('admin', 'admin123', 'Admin', 'System Administrator', 'admin@labserver.local')
ON CONFLICT (username) DO NOTHING;

-- Insert default settings
INSERT INTO settings (setting_key, setting_value, description) VALUES
('server_port', '8080', 'TCP Server Port'),
('log_retention_days', '30', 'Number of days to keep logs'),
('max_concurrent_users', '50', 'Maximum concurrent users'),
('session_timeout_minutes', '120', 'Session timeout in minutes'),
('auto_lock_enabled', 'true', 'Enable automatic computer locking')
ON CONFLICT (setting_key) DO NOTHING;

-- Insert sample computers (replace with your actual lab computers)
INSERT INTO computers (client_name, ip_address, location, lab_room) VALUES
('LAB-PC-01', '192.168.1.10', 'Computer Lab A', 'Lab A'),
('LAB-PC-02', '192.168.1.11', 'Computer Lab A', 'Lab A'),
('LAB-PC-03', '192.168.1.12', 'Computer Lab A', 'Lab A'),
('LAB-PC-04', '192.168.1.13', 'Computer Lab B', 'Lab B'),
('LAB-PC-05', '192.168.1.14', 'Computer Lab B', 'Lab B')
ON CONFLICT (client_name) DO NOTHING;
