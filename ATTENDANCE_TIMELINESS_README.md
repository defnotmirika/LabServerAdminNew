# Attendance Timeliness Tracking Implementation

## Overview
This implementation adds automatic tracking of student punctuality (on-time vs late) based on class schedules. Students are marked late if they log in more than 15 minutes after their scheduled class start time, unless the instructor started the server late.

## Features

### 1. **Automatic Timeliness Calculation**
- ? Students marked **"On Time"** if they login within 15 minutes of schedule start
- ?? Students marked **"Late"** if they login after the grace period
- ?? Students marked **"Excused"** if instructor started server late

### 2. **Grace Period**
- Default: **15 minutes** after scheduled start time
- Configurable via database settings table
- Fair to students who arrive slightly after schedule

### 3. **Instructor Protection**
- If instructor starts server AFTER the expected login time, students are marked "Excused"
- This prevents unfair late markings when instructor is delayed

## Database Schema

### New Tables

#### `server_sessions` - Tracks when instructor starts/stops server
```sql
CREATE TABLE server_sessions (
    id SERIAL PRIMARY KEY,
    instructor_id VARCHAR(20),
    server_start_time TIMESTAMP,
    server_stop_time TIMESTAMP,
    session_date DATE,
    lab_id INTEGER,
    is_active BOOLEAN,
    created_at TIMESTAMP
);
```

### Updated Tables

#### `attendance_logs` - New columns added
```sql
ALTER TABLE attendance_logs ADD COLUMN:
- schedule_start_time TIMESTAMP      -- When class is scheduled to start
- expected_login_time TIMESTAMP      -- Schedule start + grace period
- is_late BOOLEAN                    -- True if student is late
- minutes_late INTEGER               -- How many minutes late (0 if on time)
- timeliness_status VARCHAR(20)      -- "On Time", "Late", or "Excused"
- server_start_time TIMESTAMP        -- When instructor started server
```

### New Settings
```sql
INSERT INTO settings VALUES
('attendance_grace_period_minutes', '15', 'Grace period before marking late');
```

## How It Works

### Student Login Flow
```
1. Student clicks login in LoginWindow
   ?
2. RecordStudentLoginAsync() is called
   ?
3. System queries:
   - Student's section from us_geninfo
   - Today's schedule from course_schedules
   - Server start time from server_sessions
   ?
4. INSERT into attendance_logs with:
   - login_time = CURRENT_TIMESTAMP
   - schedule_start_time (from course_schedules)
   - server_start_time (from server_sessions)
   ?
5. Database trigger automatically calculates:
   - is_late (TRUE/FALSE)
   - minutes_late (0 or actual minutes)
   - timeliness_status ("On Time", "Late", "Excused")
```

### Calculation Logic
```sql
Expected Login Time = Schedule Start Time + 15 minutes

IF student has no schedule:
    Status = "On Time"
ELSE IF server_start_time > expected_login_time:
    Status = "Excused" (instructor was late)
ELSE IF login_time <= expected_login_time:
    Status = "On Time"
ELSE:
    Status = "Late"
    Minutes Late = (login_time - expected_login_time)
```

### Server Start Flow
```
1. Instructor clicks "Start Server"
   ?
2. RecordServerStartAsync() is called
   ?
3. INSERT into server_sessions:
   - instructor_id
   - server_start_time = CURRENT_TIMESTAMP
   - session_date = CURRENT_DATE
   - is_active = TRUE
```

## UI Changes

### Attendance Logs Tab
New column added: **"Timeliness"**

| Display | Status | Color | Meaning |
|---------|--------|-------|---------|
| ?? On Time | On Time | Green | Logged in within grace period |
| ?? Late (X min) | Late | Red | Logged in X minutes after grace period |
| ?? Excused | Excused | Orange | Server started late, student not at fault |

### Example Display
```
Date       | Student No | Name      | Time In  | Timeliness       | Status
-----------|------------|-----------|----------|------------------|----------
2024-01-15 | 2021-1234  | John Doe  | 08:05:00 | ?? On Time      | Completed
2024-01-15 | 2021-5678  | Jane Smith| 08:25:00 | ?? Late (10 min)| Active
2024-01-15 | 2021-9999  | Bob Lee   | 08:30:00 | ?? Excused      | Active
```

## Installation Steps

### 1. Run Database Migration
```bash
psql -U postgres -d labserver -f database_migration_attendance_timeliness.sql
```

### 2. Required Database Tables
Ensure you have these tables:
- `us_geninfo` (student information with section_id)
- `course_schedules` (class schedules with time_in, section_id, lab_id, day_of_week)
- `computers` (with lab_id)

### 3. Sample course_schedules Structure
```sql
CREATE TABLE course_schedules (
    id SERIAL PRIMARY KEY,
    section_id INTEGER,
    lab_id INTEGER,
    day_of_week VARCHAR(10),  -- 'Monday', 'Tuesday', etc.
    time_in TIME,              -- e.g., '08:00:00'
    time_out TIME
);
```

### 4. Link Students to Sections
```sql
-- Ensure us_geninfo has section_id
ALTER TABLE us_geninfo ADD COLUMN section_id INTEGER;

-- Example: Assign student to section
UPDATE us_geninfo SET section_id = 1 WHERE studNo = '2021-1234';
```

## Configuration

### Adjust Grace Period
```sql
-- Change from 15 minutes to 10 minutes
UPDATE settings 
SET setting_value = '10' 
WHERE setting_key = 'attendance_grace_period_minutes';
```

### View Timeliness Statistics
```sql
-- Count students by timeliness status
SELECT 
    timeliness_status,
    COUNT(*) as count,
    AVG(minutes_late) as avg_minutes_late
FROM attendance_logs
WHERE DATE(login_time) = CURRENT_DATE
GROUP BY timeliness_status;
```

### Export Late Students
```sql
-- Find all late students today
SELECT 
    studNo,
    student_name,
    login_time,
    minutes_late,
    pc_name
FROM v_attendance_with_timeliness
WHERE DATE(login_time) = CURRENT_DATE
  AND is_late = TRUE
ORDER BY minutes_late DESC;
```

## Code Changes Summary

### Files Modified
1. **`LabServerAdmin/Models/DatabaseModels.cs`**
   - Added timeliness fields to `AttendanceLog` class
   - Added `TimelinessDisplay` computed property

2. **`LabServerAdmin/Services/DatabaseService.cs`**
   - Updated `GetAttendanceLogsAsync()` to include timeliness fields
   - Added `RecordServerStartAsync()`
   - Added `GetTodayServerStartTimeAsync()`
   - Added `RecordServerStopAsync()`

3. **`LabServerAdmin/MainWindow.xaml.cs`**
   - Updated `ServerToggleButton_Click()` to record server start/stop

4. **`LabServerAdmin/MainWindow.xaml`**
   - Added "Timeliness" column to AttendanceLogsDataGrid
   - Color-coded display (Green/Red/Orange)

5. **`LabServerClient/Services/DatabaseService.cs`**
   - Updated `RecordStudentLoginAsync()` to capture schedule and server start time

### Files Created
1. **`database_migration_attendance_timeliness.sql`**
   - Complete migration script
   - Creates new tables and columns
   - Adds triggers and functions
   - Creates reporting view

## Testing

### Test Case 1: On Time Student
```
Schedule Start: 08:00:00
Grace Period: 15 minutes
Expected Login: 08:15:00
Student Logs In: 08:10:00
Expected Result: ?? "On Time"
```

### Test Case 2: Late Student
```
Schedule Start: 08:00:00
Grace Period: 15 minutes
Expected Login: 08:15:00
Student Logs In: 08:25:00
Expected Result: ?? "Late (10 min)"
```

### Test Case 3: Excused (Server Started Late)
```
Schedule Start: 08:00:00
Grace Period: 15 minutes
Expected Login: 08:15:00
Server Started: 08:20:00
Student Logs In: 08:22:00
Expected Result: ?? "Excused"
```

### Test Case 4: No Schedule
```
Student has no schedule in course_schedules
Student Logs In: Any time
Expected Result: ?? "On Time"
```

## Troubleshooting

### Students always marked "On Time"
- Check if `course_schedules` table has data
- Verify student's `section_id` is set in `us_geninfo`
- Ensure `day_of_week` matches current day format

### All students marked "Excused"
- Server was likely started late
- Check `server_sessions.server_start_time`
- This is working as designed to protect students

### Minutes late calculation wrong
- Verify time zones are consistent
- Check database server time vs application time
- Ensure `login_time` uses CURRENT_TIMESTAMP

## Benefits

? **Automated Tracking** - No manual intervention needed
? **Fair to Students** - Excuses them if instructor is late
? **Configurable** - Grace period can be adjusted
? **Audit Trail** - All data stored for reports
? **Visual Feedback** - Color-coded display for quick review
? **Exportable** - Can export late students for records

## Future Enhancements

- [ ] Email notifications for late students
- [ ] Weekly/monthly punctuality reports
- [ ] Student-specific grace periods
- [ ] Integration with academic calendar
- [ ] SMS alerts for chronic lateness
- [ ] Dashboard widget showing late percentage

---

**Version:** 1.0  
**Last Updated:** 2024  
**Author:** Lab Server Development Team
