-- Supabase Database Setup Script
-- Lab Sessions, Session Participants, and Computers Tables
-- Run this script in Supabase SQL Editor

-- Enable UUID extension (if not already enabled)
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

-- Create computers table
-- Tracks computer/PC information in the lab
CREATE TABLE IF NOT EXISTS computers (
    id SERIAL PRIMARY KEY,
    name VARCHAR(100) NOT NULL UNIQUE,
    ip_address VARCHAR(45),
    mac_address VARCHAR(17),
    status VARCHAR(50) DEFAULT 'Offline',
    is_active BOOLEAN DEFAULT TRUE,
    last_seen TIMESTAMP,
    location VARCHAR(100),
    notes TEXT,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Create lab_sessions table
-- Tracks lab sessions with start/end times and session details
CREATE TABLE IF NOT EXISTS lab_sessions (
    id SERIAL PRIMARY KEY,
    session_name VARCHAR(200) NOT NULL,
    instructor_name VARCHAR(100),
    start_time TIMESTAMP NOT NULL,
    end_time TIMESTAMP,
    status VARCHAR(50) DEFAULT 'Active',
    description TEXT,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Create session_participants table
-- Tracks which computers/students are participating in which sessions
CREATE TABLE IF NOT EXISTS session_participants (
    id SERIAL PRIMARY KEY,
    session_id INTEGER NOT NULL REFERENCES lab_sessions(id) ON DELETE CASCADE,
    computer_id INTEGER REFERENCES computers(id) ON DELETE SET NULL,
    student_name VARCHAR(100),
    pc_name VARCHAR(100),
    joined_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    left_at TIMESTAMP,
    is_active BOOLEAN DEFAULT TRUE,
    notes TEXT,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Create settings table
-- Stores application configuration as key-value pairs
CREATE TABLE IF NOT EXISTS settings (
    id SERIAL PRIMARY KEY,
    setting_key VARCHAR(100) NOT NULL,
    setting_value VARCHAR(255),
    description TEXT,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Create indexes for better performance
CREATE INDEX IF NOT EXISTS idx_computers_name ON computers(name);
CREATE INDEX IF NOT EXISTS idx_computers_status ON computers(status);
CREATE INDEX IF NOT EXISTS idx_computers_is_active ON computers(is_active);
CREATE INDEX IF NOT EXISTS idx_computers_last_seen ON computers(last_seen);

CREATE INDEX IF NOT EXISTS idx_lab_sessions_start_time ON lab_sessions(start_time);
CREATE INDEX IF NOT EXISTS idx_lab_sessions_status ON lab_sessions(status);
CREATE INDEX IF NOT EXISTS idx_lab_sessions_instructor ON lab_sessions(instructor_name);

CREATE INDEX IF NOT EXISTS idx_session_participants_session_id ON session_participants(session_id);
CREATE INDEX IF NOT EXISTS idx_session_participants_computer_id ON session_participants(computer_id);
CREATE INDEX IF NOT EXISTS idx_session_participants_student_name ON session_participants(student_name);
CREATE INDEX IF NOT EXISTS idx_session_participants_is_active ON session_participants(is_active);
CREATE INDEX IF NOT EXISTS idx_session_participants_joined_at ON session_participants(joined_at);

CREATE INDEX IF NOT EXISTS idx_settings_setting_key ON settings(setting_key);

-- Create updated_at trigger function (if not exists)
CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Create triggers to automatically update updated_at timestamp
CREATE TRIGGER update_computers_updated_at
    BEFORE UPDATE ON computers
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();

CREATE TRIGGER update_lab_sessions_updated_at
    BEFORE UPDATE ON lab_sessions
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();

CREATE TRIGGER update_settings_updated_at
    BEFORE UPDATE ON settings
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();

-- Enable Row Level Security (RLS) for Supabase
-- Adjust policies based on your authentication requirements

ALTER TABLE computers ENABLE ROW LEVEL SECURITY;
ALTER TABLE lab_sessions ENABLE ROW LEVEL SECURITY;
ALTER TABLE session_participants ENABLE ROW LEVEL SECURITY;
ALTER TABLE settings ENABLE ROW LEVEL SECURITY;

-- Example RLS policies (adjust based on your needs)
-- Allow authenticated users to read all data
CREATE POLICY "Allow authenticated read access" ON computers
    FOR SELECT
    TO authenticated
    USING (true);

CREATE POLICY "Allow authenticated read access" ON lab_sessions
    FOR SELECT
    TO authenticated
    USING (true);

CREATE POLICY "Allow authenticated read access" ON session_participants
    FOR SELECT
    TO authenticated
    USING (true);

CREATE POLICY "Allow authenticated read access" ON settings
    FOR SELECT
    TO authenticated
    USING (true);

-- Allow authenticated users to insert/update/delete (adjust as needed)
CREATE POLICY "Allow authenticated write access" ON computers
    FOR ALL
    TO authenticated
    USING (true)
    WITH CHECK (true);

CREATE POLICY "Allow authenticated write access" ON lab_sessions
    FOR ALL
    TO authenticated
    USING (true)
    WITH CHECK (true);

CREATE POLICY "Allow authenticated write access" ON session_participants
    FOR ALL
    TO authenticated
    USING (true)
    WITH CHECK (true);

CREATE POLICY "Allow authenticated write access" ON settings
    FOR ALL
    TO authenticated
    USING (true)
    WITH CHECK (true);

-- Optional: Add comments for documentation
COMMENT ON TABLE computers IS 'Stores information about computers/PCs in the lab';
COMMENT ON TABLE lab_sessions IS 'Stores lab session information with start/end times';
COMMENT ON TABLE session_participants IS 'Tracks which computers/students participate in lab sessions';
COMMENT ON TABLE settings IS 'Stores application configuration as key-value pairs';

COMMENT ON COLUMN computers.status IS 'Current status: Online, Offline, Maintenance, etc.';
COMMENT ON COLUMN lab_sessions.status IS 'Session status: Active, Completed, Cancelled, etc.';
COMMENT ON COLUMN session_participants.is_active IS 'Whether the participant is currently active in the session';
COMMENT ON COLUMN settings.setting_key IS 'Unique identifier for the setting';
COMMENT ON COLUMN settings.setting_value IS 'The value of the setting';
COMMENT ON COLUMN settings.description IS 'Documentation explaining what this setting does';

