-- Lab Server Database Setup Script
-- Run this script in PostgreSQL to create the database and tables

-- Create database (run as superuser)
-- CREATE DATABASE labserver;

-- Connect to the labserver database
-- \c labserver;

-- Create tables
CREATE TABLE IF NOT EXISTS admins (
    id SERIAL PRIMARY KEY,
    username VARCHAR(50) NOT NULL UNIQUE,
    password_hash VARCHAR(255) NOT NULL,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS system_logs (
    id SERIAL PRIMARY KEY,
    timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    action VARCHAR(100) NOT NULL,
    client_name VARCHAR(100),
    status VARCHAR(50),
    details VARCHAR(500)
);

CREATE TABLE IF NOT EXISTS attendance_logs (
    id SERIAL PRIMARY KEY,
    student_name VARCHAR(100) NOT NULL,
    pc_name VARCHAR(100) NOT NULL,
    time_in TIMESTAMP NOT NULL,
    time_out TIMESTAMP,
    is_active BOOLEAN DEFAULT TRUE
);

CREATE TABLE IF NOT EXISTS connected_clients (
    id SERIAL PRIMARY KEY,
    name VARCHAR(100) NOT NULL UNIQUE,
    ip_address VARCHAR(45) NOT NULL,
    last_response TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    is_connected BOOLEAN DEFAULT TRUE,
    status VARCHAR(50) DEFAULT 'Online'
);

CREATE TABLE IF NOT EXISTS clients (
    id SERIAL PRIMARY KEY,
    username VARCHAR(50) NOT NULL UNIQUE,
    password_hash VARCHAR(255) NOT NULL,
    pc_name VARCHAR(100),
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Create indexes for better performance
CREATE INDEX IF NOT EXISTS idx_system_logs_timestamp ON system_logs(timestamp);
CREATE INDEX IF NOT EXISTS idx_system_logs_client_name ON system_logs(client_name);
CREATE INDEX IF NOT EXISTS idx_attendance_logs_time_in ON attendance_logs(time_in);
CREATE INDEX IF NOT EXISTS idx_attendance_logs_pc_name ON attendance_logs(pc_name);
CREATE INDEX IF NOT EXISTS idx_connected_clients_name ON connected_clients(name);
CREATE INDEX IF NOT EXISTS idx_clients_username ON clients(username);

-- Create a user for the application (optional)
-- CREATE USER labuser WITH PASSWORD 'your_secure_password';
-- GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO labuser;
-- GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO labuser;

COMMIT;
