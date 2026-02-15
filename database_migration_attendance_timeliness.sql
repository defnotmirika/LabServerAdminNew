-- Migration script for adding attendance timeliness tracking
-- This adds columns to track if students are on time or late based on schedule

-- Step 1: Add new columns to attendance_logs table
ALTER TABLE attendance_logs 
ADD COLUMN IF NOT EXISTS schedule_start_time TIMESTAMP,
ADD COLUMN IF NOT EXISTS expected_login_time TIMESTAMP,
ADD COLUMN IF NOT EXISTS is_late BOOLEAN DEFAULT FALSE,
ADD COLUMN IF NOT EXISTS minutes_late INTEGER DEFAULT 0,
ADD COLUMN IF NOT EXISTS timeliness_status VARCHAR(20) DEFAULT 'On Time',
ADD COLUMN IF NOT EXISTS server_start_time TIMESTAMP;

-- Step 2: Add comments to new columns
COMMENT ON COLUMN attendance_logs.schedule_start_time IS 'The scheduled class start time from course_schedules';
COMMENT ON COLUMN attendance_logs.expected_login_time IS 'Expected login time (schedule_start_time + grace period)';
COMMENT ON COLUMN attendance_logs.is_late IS 'True if student logged in after grace period';
COMMENT ON COLUMN attendance_logs.minutes_late IS 'Number of minutes late (0 if on time)';
COMMENT ON COLUMN attendance_logs.timeliness_status IS 'On Time, Late, or Excused (if server started late)';
COMMENT ON COLUMN attendance_logs.server_start_time IS 'Timestamp when instructor started the server for this session';

-- Step 3: Create index for timeliness queries
CREATE INDEX IF NOT EXISTS idx_attendance_logs_timeliness ON attendance_logs(timeliness_status);
CREATE INDEX IF NOT EXISTS idx_attendance_logs_is_late ON attendance_logs(is_late);

-- Step 4: Ensure lab_sessions table has necessary columns for server start tracking
-- (lab_sessions already exists from database_setup.sql)
-- Add start_time index if not exists
CREATE INDEX IF NOT EXISTS idx_lab_sessions_start_time ON lab_sessions(start_time);
CREATE INDEX IF NOT EXISTS idx_lab_sessions_active ON lab_sessions(is_active);

-- Step 5: Add grace period setting (15 minutes default)
INSERT INTO settings (setting_key, setting_value, description) VALUES
('attendance_grace_period_minutes', '15', 'Number of minutes after schedule start time before marking late')
ON CONFLICT (setting_key) DO UPDATE SET 
    setting_value = EXCLUDED.setting_value,
    description = EXCLUDED.description;

-- Step 6: Create function to calculate timeliness status
CREATE OR REPLACE FUNCTION calculate_timeliness_status(
    p_login_time TIMESTAMP,
    p_schedule_start_time TIMESTAMP,
    p_server_start_time TIMESTAMP,
    p_grace_period_minutes INTEGER DEFAULT 15
) RETURNS TABLE (
    is_late BOOLEAN,
    minutes_late INTEGER,
    timeliness_status VARCHAR(20)
) AS $$
DECLARE
    v_expected_login_time TIMESTAMP;
    v_minutes_diff INTEGER;
BEGIN
    -- If no schedule exists, mark as On Time
    IF p_schedule_start_time IS NULL THEN
        RETURN QUERY SELECT FALSE, 0, 'On Time'::VARCHAR(20);
        RETURN;
    END IF;
    
    -- Calculate expected login time (schedule start + grace period)
    v_expected_login_time := p_schedule_start_time + (p_grace_period_minutes || ' minutes')::INTERVAL;
    
    -- If server started after the expected login time, student is excused
    IF p_server_start_time IS NOT NULL AND p_server_start_time > v_expected_login_time THEN
        RETURN QUERY SELECT FALSE, 0, 'Excused'::VARCHAR(20);
        RETURN;
    END IF;
    
    -- Calculate minutes difference
    v_minutes_diff := EXTRACT(EPOCH FROM (p_login_time - v_expected_login_time)) / 60;
    
    -- Determine status
    IF v_minutes_diff <= 0 THEN
        -- Logged in before or during grace period
        RETURN QUERY SELECT FALSE, 0, 'On Time'::VARCHAR(20);
    ELSE
        -- Logged in after grace period
        RETURN QUERY SELECT TRUE, v_minutes_diff, 'Late'::VARCHAR(20);
    END IF;
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- Step 7: Create trigger to automatically calculate timeliness on insert/update
CREATE OR REPLACE FUNCTION update_attendance_timeliness()
RETURNS TRIGGER AS $$
DECLARE
    v_timeliness RECORD;
    v_grace_period INTEGER;
BEGIN
    -- Get grace period from settings (default 15 minutes)
    SELECT COALESCE(setting_value::INTEGER, 15) INTO v_grace_period
    FROM settings
    WHERE setting_key = 'attendance_grace_period_minutes';
    
    -- Calculate timeliness status
    SELECT * INTO v_timeliness
    FROM calculate_timeliness_status(
        NEW.login_time,
        NEW.schedule_start_time,
        NEW.server_start_time,
        v_grace_period
    );
    
    -- Update the record
    NEW.is_late := v_timeliness.is_late;
    NEW.minutes_late := v_timeliness.minutes_late;
    NEW.timeliness_status := v_timeliness.timeliness_status;
    
    -- Calculate expected login time
    IF NEW.schedule_start_time IS NOT NULL THEN
        NEW.expected_login_time := NEW.schedule_start_time + (v_grace_period || ' minutes')::INTERVAL;
    END IF;
    
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Create trigger
DROP TRIGGER IF EXISTS trg_update_attendance_timeliness ON attendance_logs;
CREATE TRIGGER trg_update_attendance_timeliness
    BEFORE INSERT OR UPDATE OF login_time, schedule_start_time, server_start_time
    ON attendance_logs
    FOR EACH ROW
    EXECUTE FUNCTION update_attendance_timeliness();

-- Step 8: Create view for easy attendance reporting with timeliness
CREATE OR REPLACE VIEW v_attendance_with_timeliness AS
SELECT 
    al.id,
    al.studNo,
    COALESCE(us.full_name, al.studNo) as student_name,
    COALESCE(c.client_name, '') as pc_name,
    al.login_time,
    al.logout_time,
    al.schedule_start_time,
    al.expected_login_time,
    al.server_start_time,
    al.is_late,
    al.minutes_late,
    al.timeliness_status,
    al.session_duration,
    al.status,
    CASE 
        WHEN al.is_late THEN '?? Late (' || al.minutes_late || ' min)'
        WHEN al.timeliness_status = 'Excused' THEN '?? Excused'
        ELSE '?? On Time'
    END as timeliness_display
FROM attendance_logs al
LEFT JOIN us_geninfo us ON al.studNo = us.studNo
LEFT JOIN computers c ON al.computer_id = c.id
ORDER BY al.login_time DESC;

-- Step 9: Update existing records (mark all as 'On Time' since we don't have historical data)
UPDATE attendance_logs
SET 
    is_late = FALSE,
    minutes_late = 0,
    timeliness_status = 'On Time'
WHERE timeliness_status IS NULL;

-- Step 10: Show migration results
DO $$
DECLARE
    v_count INTEGER;
BEGIN
    SELECT COUNT(*) INTO v_count FROM attendance_logs;
    RAISE NOTICE 'Migration completed successfully!';
    RAISE NOTICE 'Total attendance records: %', v_count;
    RAISE NOTICE 'New columns added: schedule_start_time, expected_login_time, is_late, minutes_late, timeliness_status, server_start_time';
    RAISE NOTICE 'Grace period setting: 15 minutes';
END $$;
