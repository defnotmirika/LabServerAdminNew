-- Migration script to create new credential tables for admins and instructors
-- This script MUST be run on your PostgreSQL database before the application can authenticate users
-- The old 'users' table is NO LONGER used for authentication

-- Create ua_geninfo table if it doesn't exist (for admin general info)
CREATE TABLE IF NOT EXISTS ua_geninfo (
    empID VARCHAR(20) PRIMARY KEY,
    full_name VARCHAR(100),
    email VARCHAR(100),
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    is_active BOOLEAN DEFAULT TRUE
);

-- Create ui_geninfo table if it doesn't exist (for instructor general info)
CREATE TABLE IF NOT EXISTS ui_geninfo (
    empID VARCHAR(20) PRIMARY KEY,
    full_name VARCHAR(100),
    email VARCHAR(100),
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    is_active BOOLEAN DEFAULT TRUE
);

-- Create us_geninfo table if it doesn't exist (for student general info)
CREATE TABLE IF NOT EXISTS us_geninfo (
    studNo VARCHAR(20) PRIMARY KEY,
    full_name VARCHAR(100),
    email VARCHAR(100),
    program VARCHAR(100),
    year_level INT,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    is_active BOOLEAN DEFAULT TRUE
);

-- Create ua_credentials table for admin credentials
CREATE TABLE IF NOT EXISTS ua_credentials (
    id SERIAL PRIMARY KEY,
    empID VARCHAR(20) NOT NULL REFERENCES ua_geninfo(empID) ON DELETE CASCADE,
    username VARCHAR(50),
    password_hash VARCHAR(255) NOT NULL,
    is_temp_password BOOLEAN DEFAULT TRUE,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    role VARCHAR(50) DEFAULT 'ADMIN'
);

-- Create ui_credentials table for instructor credentials
CREATE TABLE IF NOT EXISTS ui_credentials (
    id SERIAL PRIMARY KEY,
    empID VARCHAR(20) NOT NULL REFERENCES ui_geninfo(empID) ON DELETE CASCADE,
    username VARCHAR(50),
    password_hash VARCHAR(255) NOT NULL,
    is_temp_password BOOLEAN DEFAULT TRUE,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    role VARCHAR(50) DEFAULT 'INSTRUCTOR'
);

-- Update attendance_logs table to new schema
DO $$
BEGIN
    -- Check if attendance_logs exists with old schema (user_id as integer)
    IF EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_name = 'attendance_logs' 
        AND column_name = 'user_id'
        AND data_type LIKE '%integer%'
    ) THEN
        -- Backup old data
        CREATE TEMP TABLE attendance_logs_backup AS SELECT * FROM attendance_logs;
        
        -- Drop old table
        DROP TABLE IF EXISTS attendance_logs CASCADE;
        
        -- Create new table with updated schema
        CREATE TABLE attendance_logs (
            id SERIAL PRIMARY KEY,
            studNo VARCHAR(20) NOT NULL REFERENCES us_geninfo(studNo),
            computer_id INT REFERENCES computers(id),
            login_time TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            logout_time TIMESTAMP,
            session_duration INTERVAL,
            status VARCHAR(20) DEFAULT 'Active'
        );
        
        -- Note: Cannot automatically migrate data from old user_id to new studNo
        -- Manual data migration required if needed
        
        -- Update sequence
        PERFORM setval('attendance_logs_id_seq', COALESCE((SELECT MAX(id) FROM attendance_logs), 1));
        
        DROP TABLE IF EXISTS attendance_logs_backup;
    END IF;
    
    -- Create table if it doesn't exist
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'attendance_logs') THEN
        CREATE TABLE attendance_logs (
            id SERIAL PRIMARY KEY,
            studNo VARCHAR(20) NOT NULL REFERENCES us_geninfo(studNo),
            computer_id INT REFERENCES computers(id),
            login_time TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            logout_time TIMESTAMP,
            session_duration INTERVAL,
            status VARCHAR(20) DEFAULT 'Active'
        );
    END IF;
END $$;

-- Update system_logs table to new schema (if needed)
DO $$
BEGIN
    -- Check if system_logs exists with old schema
    IF EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_name = 'system_logs' 
        AND column_name = 'user_id'
        AND data_type LIKE '%integer%'
    ) THEN
        -- Backup old data
        CREATE TEMP TABLE system_logs_backup AS SELECT * FROM system_logs;
        
        -- Drop old table
        DROP TABLE IF EXISTS system_logs CASCADE;
        
        -- Create new table with updated schema
        CREATE TABLE system_logs (
            id SERIAL PRIMARY KEY,
            log_level VARCHAR(10) NOT NULL,
            log_message TEXT NOT NULL,
            action_type VARCHAR(50),
            user_id VARCHAR(20),
            computer_id INT REFERENCES computers(id),
            log_time TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        );
        
        -- Migrate old data (user_id converted to VARCHAR)
        INSERT INTO system_logs (id, log_level, log_message, action_type, user_id, computer_id, log_time)
        SELECT id, log_level, log_message, action_type, user_id::VARCHAR(20), computer_id, log_time
        FROM system_logs_backup;
        
        -- Update sequence
        PERFORM setval('system_logs_id_seq', COALESCE((SELECT MAX(id) FROM system_logs), 1));
        
        DROP TABLE system_logs_backup;
    END IF;
    
    -- Create table if it doesn't exist
    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'system_logs') THEN
        CREATE TABLE system_logs (
            id SERIAL PRIMARY KEY,
            log_level VARCHAR(10) NOT NULL,
            log_message TEXT NOT NULL,
            action_type VARCHAR(50),
            user_id VARCHAR(20),
            computer_id INT REFERENCES computers(id),
            log_time TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        );
    END IF;
END $$;

-- Create indexes for better performance
CREATE INDEX IF NOT EXISTS idx_ua_credentials_username ON ua_credentials(username);
CREATE INDEX IF NOT EXISTS idx_ua_credentials_empid ON ua_credentials(empID);
CREATE INDEX IF NOT EXISTS idx_ui_credentials_username ON ui_credentials(username);
CREATE INDEX IF NOT EXISTS idx_ui_credentials_empid ON ui_credentials(empID);
CREATE INDEX IF NOT EXISTS idx_system_logs_time ON system_logs(log_time);
CREATE INDEX IF NOT EXISTS idx_system_logs_user ON system_logs(user_id);
CREATE INDEX IF NOT EXISTS idx_attendance_logs_studno ON attendance_logs(studNo);
CREATE INDEX IF NOT EXISTS idx_attendance_logs_time ON attendance_logs(login_time);

-- Migrate existing admin users from users table to new structure (RECOMMENDED)
-- Uncomment the following if you want to migrate existing data from the old 'users' table
/*
DO $$
DECLARE
    admin_record RECORD;
    new_empid VARCHAR(20);
BEGIN
    FOR admin_record IN 
        SELECT id, username, password, full_name, email, created_at, is_active
        FROM users 
        WHERE role = 'Admin'
    LOOP
        -- Generate empID (you may want to customize this)
        new_empid := 'EMP' || LPAD(admin_record.id::TEXT, 6, '0');
        
        -- Insert into ua_geninfo if not exists
        INSERT INTO ua_geninfo (empID, full_name, email, created_at, is_active)
        VALUES (new_empid, admin_record.full_name, admin_record.email, admin_record.created_at, admin_record.is_active)
        ON CONFLICT (empID) DO NOTHING;
        
        -- Insert into ua_credentials if not exists
        INSERT INTO ua_credentials (empID, username, password_hash, is_temp_password, created_at, role)
        VALUES (new_empid, admin_record.username, admin_record.password, FALSE, admin_record.created_at, 'ADMIN')
        ON CONFLICT DO NOTHING;
    END LOOP;
END $$;
*/

-- REQUIRED: Insert default admin for initial login
-- You can modify these values or add your own admins
-- Default credentials: username='admin', password='admin123'

-- Insert default admin general info
INSERT INTO ua_geninfo (empID, full_name, email)
VALUES ('ADMIN001', 'System Administrator', 'admin@labserver.local')
ON CONFLICT (empID) DO NOTHING;

-- Insert default admin credentials
-- Note: This is a BCrypt hash of 'admin123' - CHANGE THIS PASSWORD AFTER FIRST LOGIN!
INSERT INTO ua_credentials (empID, username, password_hash, is_temp_password, role)
VALUES (
    'ADMIN001', 
    'admin', 
    '$2a$10$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy', -- BCrypt hash of 'admin123'
    TRUE, 
    'ADMIN'
)
ON CONFLICT DO NOTHING;

-- Optional: Add sample instructor
-- Uncomment if needed
/*
INSERT INTO ui_geninfo (empID, full_name, email)
VALUES ('INST001', 'Sample Instructor', 'instructor@labserver.local')
ON CONFLICT (empID) DO NOTHING;

INSERT INTO ui_credentials (empID, username, password_hash, is_temp_password, role)
VALUES (
    'INST001', 
    'instructor', 
    '$2a$10$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy', -- BCrypt hash of 'admin123'
    TRUE, 
    'INSTRUCTOR'
);
*/

-- Optional: Add sample student
-- Uncomment if needed
/*
INSERT INTO us_geninfo (studNo, full_name, email, program, year_level)
VALUES ('2024-00001', 'Sample Student', 'student@labserver.local', 'Computer Science', 1)
ON CONFLICT (studNo) DO NOTHING;
*/

-- Verify the tables were created and have data
SELECT 'ua_geninfo' as table_name, COUNT(*) as record_count FROM ua_geninfo
UNION ALL
SELECT 'ui_geninfo', COUNT(*) FROM ui_geninfo
UNION ALL
SELECT 'us_geninfo', COUNT(*) FROM us_geninfo
UNION ALL
SELECT 'ua_credentials', COUNT(*) FROM ua_credentials
UNION ALL
SELECT 'ui_credentials', COUNT(*) FROM ui_credentials
UNION ALL
SELECT 'system_logs', COUNT(*) FROM system_logs
UNION ALL
SELECT 'attendance_logs', COUNT(*) FROM attendance_logs;

-- SUCCESS MESSAGE
DO $$
BEGIN
    RAISE NOTICE '============================================';
    RAISE NOTICE 'Migration completed successfully!';
    RAISE NOTICE 'Default admin credentials:';
    RAISE NOTICE '  Username: admin';
    RAISE NOTICE '  Password: admin123';
    RAISE NOTICE '  IMPORTANT: Change this password immediately!';
    RAISE NOTICE '============================================';
END $$;
