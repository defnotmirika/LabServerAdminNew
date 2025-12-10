-- SQL Queries to Export Data from Localhost to Supabase
-- Run these queries on your LOCALHOST database to generate INSERT statements
-- Then run the generated INSERT statements on your SUPABASE database

-- ============================================
-- EXPORT ADMINS TABLE
-- ============================================
SELECT 
    'INSERT INTO admins (username, password_hash, created_at) VALUES (' ||
    quote_literal(username) || ', ' ||
    quote_literal(password_hash) || ', ' ||
    quote_literal(created_at::text) || ');' AS insert_statement
FROM admins
ORDER BY id;

-- ============================================
-- EXPORT SYSTEM_LOGS TABLE
-- ============================================
SELECT 
    'INSERT INTO system_logs (timestamp, action, client_name, status, details) VALUES (' ||
    quote_literal(timestamp::text) || ', ' ||
    quote_literal(action) || ', ' ||
    COALESCE(quote_literal(client_name), 'NULL') || ', ' ||
    COALESCE(quote_literal(status), 'NULL') || ', ' ||
    COALESCE(quote_literal(details), 'NULL') || ');' AS insert_statement
FROM system_logs
ORDER BY id;

-- ============================================
-- EXPORT ATTENDANCE_LOGS TABLE
-- ============================================
SELECT 
    'INSERT INTO attendance_logs (student_name, pc_name, time_in, time_out, is_active) VALUES (' ||
    quote_literal(student_name) || ', ' ||
    quote_literal(pc_name) || ', ' ||
    quote_literal(time_in::text) || ', ' ||
    COALESCE(quote_literal(time_out::text), 'NULL') || ', ' ||
    is_active || ');' AS insert_statement
FROM attendance_logs
ORDER BY id;

-- ============================================
-- EXPORT CONNECTED_CLIENTS TABLE
-- ============================================
SELECT 
    'INSERT INTO connected_clients (name, ip_address, last_response, is_connected, status) VALUES (' ||
    quote_literal(name) || ', ' ||
    quote_literal(ip_address) || ', ' ||
    quote_literal(last_response::text) || ', ' ||
    is_connected || ', ' ||
    quote_literal(status) || ');' AS insert_statement
FROM connected_clients
ORDER BY id;

-- ============================================
-- EXPORT CLIENTS TABLE
-- ============================================
SELECT 
    'INSERT INTO clients (username, password_hash, pc_name, created_at) VALUES (' ||
    quote_literal(username) || ', ' ||
    quote_literal(password_hash) || ', ' ||
    COALESCE(quote_literal(pc_name), 'NULL') || ', ' ||
    quote_literal(created_at::text) || ');' AS insert_statement
FROM clients
ORDER BY id;

-- ============================================
-- EXPORT SYSTEM_SETTINGS TABLE (if exists)
-- ============================================
SELECT 
    'INSERT INTO system_settings (setting_key, setting_value, updated_at) VALUES (' ||
    quote_literal(setting_key) || ', ' ||
    COALESCE(quote_literal(setting_value), 'NULL') || ', ' ||
    quote_literal(updated_at::text) || ');' AS insert_statement
FROM system_settings
ORDER BY setting_key;

-- ============================================
-- ALTERNATIVE: Export to COPY format (faster for large datasets)
-- ============================================

-- Export admins to CSV format
-- COPY (SELECT * FROM admins) TO 'C:\temp\admins.csv' WITH CSV HEADER;

-- Export system_logs to CSV format
-- COPY (SELECT * FROM system_logs) TO 'C:\temp\system_logs.csv' WITH CSV HEADER;

-- Export attendance_logs to CSV format
-- COPY (SELECT * FROM attendance_logs) TO 'C:\temp\attendance_logs.csv' WITH CSV HEADER;

-- Export connected_clients to CSV format
-- COPY (SELECT * FROM connected_clients) TO 'C:\temp\connected_clients.csv' WITH CSV HEADER;

-- Export clients to CSV format
-- COPY (SELECT * FROM clients) TO 'C:\temp\clients.csv' WITH CSV HEADER;

-- ============================================
-- COMPLETE EXPORT SCRIPT (All tables in one go)
-- ============================================

-- Run this to get all INSERT statements for all tables:
SELECT 
    '-- ADMINS TABLE' AS comment
UNION ALL
SELECT 
    'INSERT INTO admins (username, password_hash, created_at) VALUES (' ||
    quote_literal(username) || ', ' ||
    quote_literal(password_hash) || ', ' ||
    quote_literal(created_at::text) || ');'
FROM admins
UNION ALL
SELECT '-- SYSTEM_LOGS TABLE'
UNION ALL
SELECT 
    'INSERT INTO system_logs (timestamp, action, client_name, status, details) VALUES (' ||
    quote_literal(timestamp::text) || ', ' ||
    quote_literal(action) || ', ' ||
    COALESCE(quote_literal(client_name), 'NULL') || ', ' ||
    COALESCE(quote_literal(status), 'NULL') || ', ' ||
    COALESCE(quote_literal(details), 'NULL') || ');'
FROM system_logs
UNION ALL
SELECT '-- ATTENDANCE_LOGS TABLE'
UNION ALL
SELECT 
    'INSERT INTO attendance_logs (student_name, pc_name, time_in, time_out, is_active) VALUES (' ||
    quote_literal(student_name) || ', ' ||
    quote_literal(pc_name) || ', ' ||
    quote_literal(time_in::text) || ', ' ||
    COALESCE(quote_literal(time_out::text), 'NULL') || ', ' ||
    is_active || ');'
FROM attendance_logs
UNION ALL
SELECT '-- CONNECTED_CLIENTS TABLE'
UNION ALL
SELECT 
    'INSERT INTO connected_clients (name, ip_address, last_response, is_connected, status) VALUES (' ||
    quote_literal(name) || ', ' ||
    quote_literal(ip_address) || ', ' ||
    quote_literal(last_response::text) || ', ' ||
    is_connected || ', ' ||
    quote_literal(status) || ');'
FROM connected_clients
UNION ALL
SELECT '-- CLIENTS TABLE'
UNION ALL
SELECT 
    'INSERT INTO clients (username, password_hash, pc_name, created_at) VALUES (' ||
    quote_literal(username) || ', ' ||
    quote_literal(password_hash) || ', ' ||
    COALESCE(quote_literal(pc_name), 'NULL') || ', ' ||
    quote_literal(created_at::text) || ');'
FROM clients;

-- ============================================
-- HOW TO USE:
-- ============================================
-- 1. Connect to your LOCALHOST database:
--    psql -h localhost -U postgres -d labserver
--
-- 2. Run the queries above to generate INSERT statements
--    You can redirect output to a file:
--    psql -h localhost -U postgres -d labserver -f export_localhost_to_supabase.sql -o output_inserts.sql
--
-- 3. Connect to your SUPABASE database
--    psql -h db.anccogjamaylwuydtuqd.supabase.co -U postgres -d postgres
--
-- 4. Run the generated INSERT statements from output_inserts.sql
--
-- OR use pg_dump for easier migration:
-- pg_dump -h localhost -U postgres -d labserver --data-only --inserts > localhost_data.sql

