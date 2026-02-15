# ?? Schedule-Based Automatic Timer Implementation

## Overview

The system now implements **automatic timer calculation based on class schedules**. Students can only login when the instructor starts the server, and their session timer is automatically set based on the `course_schedules` table.

---

## ? Features Implemented

### 1. **Server Start Requirement**
- ? Students **CANNOT login** unless instructor has started the server
- ? Prevents students from accessing PCs outside class hours
- ? Ensures full instructor control

### 2. **Automatic Timer Based on Schedule**
- ? Timer automatically calculated from `course_schedules.time_out`
- ?? No manual timer setting required
- ?? Timer expires exactly when class schedule ends

### 3. **Late Arrival Handling**
- If student arrives late, timer still expires at schedule end time
- Student gets LESS time, not full class duration
- Fair and accurate tracking

---

## ?? How It Works

### **Login Flow**

```
1. Student opens LabServerClient
   ?
2. Enters username/password
   ?
3. System checks: Is server running? ?
   ?
   If NO:
   ? "The lab server has not been started yet.
       Please wait for your instructor."
   ? Login BLOCKED
   ?
   If YES:
   ? Continue to step 4
   ?
4. Validate credentials
   ?
5. Record attendance (with timeliness tracking)
   ?
6. Get student's schedule from course_schedules
   ?
7. Calculate time remaining:
      Remaining Time = schedule.time_out - NOW
   ?
8. Set automatic timer
   ?
9. Show SessionWindow
   ?
10. Timer counts down to schedule end time
   ?
11. When timer reaches 00:00:00 ? PC locks automatically
```

---

## ?? Example Scenarios

### **Scenario 1: On-Time Student**
```
Schedule: 08:00 - 10:00 (2 hours class)
Server Started: 07:55
Student Logs In: 08:05 (5 min late)

Timer Calculation:
- Current Time: 08:05
- Schedule Ends: 10:00
- Remaining: 10:00 - 08:05 = 1 hour 55 minutes

Timer Set To: 1:55:00

At 10:00 AM:
- Timer expires ?
- PC locks automatically ??
- Student marked "Late (5 min)" in attendance
```

### **Scenario 2: Very Late Student**
```
Schedule: 08:00 - 10:00 (2 hours class)
Server Started: 08:00
Student Logs In: 09:30 (1.5 hours late!)

Timer Calculation:
- Current Time: 09:30
- Schedule Ends: 10:00
- Remaining: 10:00 - 09:30 = 30 minutes

Timer Set To: 0:30:00 ??

At 10:00 AM:
- Timer expires ?
- PC locks automatically ??
- Student marked "Late (75 min)" in attendance
- Student only got 30 minutes of lab time
```

### **Scenario 3: Server Started Late**
```
Schedule: 08:00 - 10:00
Instructor Arrives Late: 08:25
Server Started: 08:25
Student Logs In: 08:30

Timer Calculation:
- Current Time: 08:30
- Schedule Ends: 10:00
- Remaining: 10:00 - 08:30 = 1 hour 30 minutes

Timer Set To: 1:30:00

Timeliness Status: ?? "Excused"
Why: Server started after expected login time

At 10:00 AM:
- Timer expires ?
- PC locks automatically ??
```

---

## ??? Database Requirements

### **course_schedules Table**
```sql
CREATE TABLE course_schedules (
    schedule_id SERIAL PRIMARY KEY,
    course_id INT REFERENCES courses(course_id),
    section_id INT REFERENCES section(section_id),
    empID VARCHAR(20) REFERENCES ui_geninfo(empID),
    lab_id INT REFERENCES laboratories(lab_id),
    day_of_week VARCHAR(15),    -- 'Monday', 'Tuesday', etc.
    time_in TIME,                -- e.g., '08:00:00'
    time_out TIME,               -- e.g., '10:00:00' ? REQUIRED
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);
```

### **server_sessions Table**
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

### **Sample Data**
```sql
-- Insert class schedule
INSERT INTO course_schedules 
(course_id, section_id, empID, lab_id, day_of_week, time_in, time_out)
VALUES
(1, 1, 'INSTR001', 1, 'Monday', '08:00:00', '10:00:00'),
(1, 1, 'INSTR001', 1, 'Wednesday', '08:00:00', '10:00:00'),
(1, 1, 'INSTR001', 1, 'Friday', '13:00:00', '15:00:00');

-- Link student to section
UPDATE us_geninfo 
SET section_id = 1 
WHERE studNo = '2021-1234';
```

---

## ?? Code Changes Summary

### **1. LabServerClient/Services/DatabaseService.cs**

#### Added Method: `GetStudentScheduleAsync`
```csharp
public async Task<(DateTime? scheduleStart, DateTime? scheduleEnd, DateTime? serverStart)?> 
GetStudentScheduleAsync(string studNo, string clientName)
```
- Retrieves student's schedule for today
- Returns start time, end time, and server start time
- Used to calculate automatic timer

#### Updated Method: `RecordStudentLoginAsync`
- Now uses `TRIM()` on day_of_week comparison
- Fixes whitespace issues in PostgreSQL `TO_CHAR()` result

### **2. LabServerClient/ViewModels/LoginViewModel.cs**

#### Updated Method: `CompleteLogin`
- Added server running check
- Blocks login if server hasn't started
- Shows error message to student

### **3. LabServerClient/SessionWindow.xaml.cs**

#### Added Method: `InitializeScheduleBasedTimerAsync`
- Automatically called when SessionWindow is created
- Gets student's schedule from database
- Calculates remaining time until `time_out`
- Sets timer automatically
- Starts monitoring

---

## ?? Testing Checklist

### **Test 1: Server Not Started**
- [ ] Student tries to login
- [ ] Expected: Error message "lab server has not been started yet"
- [ ] Expected: Login blocked

### **Test 2: On-Time Login**
- [ ] Instructor starts server at 08:00
- [ ] Schedule: 08:00 - 10:00
- [ ] Student logs in at 08:05
- [ ] Expected: Timer shows ~1:55:00
- [ ] Expected: Timeliness "On Time" (within grace period)
- [ ] At 10:00: PC locks automatically

### **Test 3: Late Login**
- [ ] Instructor starts server at 08:00
- [ ] Schedule: 08:00 - 10:00
- [ ] Student logs in at 08:25
- [ ] Expected: Timer shows ~1:35:00
- [ ] Expected: Timeliness "Late (10 min)"
- [ ] At 10:00: PC locks automatically

### **Test 4: Very Late Login**
- [ ] Schedule: 08:00 - 10:00
- [ ] Student logs in at 09:45
- [ ] Expected: Timer shows ~0:15:00
- [ ] Expected: Timeliness "Late (30 min)"
- [ ] At 10:00: PC locks automatically

### **Test 5: Server Started Late**
- [ ] Schedule: 08:00 - 10:00
- [ ] Instructor starts server at 08:20
- [ ] Student logs in at 08:25
- [ ] Expected: Timeliness "Excused"
- [ ] Expected: Timer shows ~1:35:00

### **Test 6: No Schedule**
- [ ] Student has no schedule in `course_schedules`
- [ ] Expected: Timer shows 00:00:00 (no limit)
- [ ] Expected: Timeliness "On Time"

---

## ?? Benefits

### **For Instructors**
? Full control - students can only login when server is running  
? No manual timer setting required  
? Automatic PC lockout at class end time  
? Accurate attendance tracking with timeliness  

### **For Students**
? Clear feedback on class schedule  
? Timer shows exact time remaining  
? Fair treatment if instructor is late (Excused status)  
? No confusion about when class ends  

### **For Administration**
? Accurate records of class attendance  
? Tracks late arrivals automatically  
? Distinguishes between student lateness and instructor lateness  
? Complete audit trail  

---

## ?? Important Notes

### **Server Must Be Running**
- Students **CANNOT login** if instructor hasn't started the server
- This is intentional for proper classroom management
- Admin accounts bypass this check

### **Timer Based on Schedule End Time**
- Timer is **NOT** based on login time + X hours
- Timer is based on **schedule.time_out** - current time
- Late students get LESS time, not full class duration

### **Grace Period**
- Default: 15 minutes after `time_in`
- Configurable in `settings` table
- Used for timeliness tracking only, NOT timer calculation

### **Day Matching**
- PostgreSQL `TO_CHAR(CURRENT_DATE, 'Day')` returns padded day names (e.g., "Monday   ")
- Uses `TRIM()` to handle whitespace
- Ensure `day_of_week` in `course_schedules` matches format (e.g., "Monday")

---

## ?? Migration Steps

### **1. Run Database Migration**
```sh
psql -U postgres -d labserver -f database_migration_attendance_timeliness.sql
```

### **2. Populate Course Schedules**
```sql
-- Add your class schedules
INSERT INTO course_schedules 
(course_id, section_id, empID, lab_id, day_of_week, time_in, time_out)
VALUES
(1, 1, 'YOUR_INSTRUCTOR_ID', 1, 'Monday', '08:00:00', '10:00:00');

-- Link students to sections
UPDATE us_geninfo SET section_id = 1 WHERE studNo = '2021-1234';
```

### **3. Test the System**
1. Start LabServerAdmin
2. Login as instructor
3. Click "Start Server"
4. On student PC: Try to login
5. Verify timer shows correct time remaining
6. Wait for timer to expire and verify PC locks

---

## ?? Troubleshooting

### **"Server has not been started yet" error**
- Check if instructor has clicked "Start Server" in LabServerAdmin
- Verify `server_sessions` table has active session for today
- SQL: `SELECT * FROM server_sessions WHERE session_date = CURRENT_DATE AND is_active = TRUE;`

### **Timer shows 00:00:00**
- Student has no schedule in `course_schedules` for today
- Check `day_of_week` matches current day
- Check `section_id` is set in `us_geninfo`

### **Timer shows wrong time**
- Verify `time_out` in `course_schedules` is correct
- Check server and client are in same timezone
- Verify system clocks are synchronized

### **Day matching not working**
- PostgreSQL returns padded day names ("Monday   ")
- Ensure `TRIM()` is used in query
- Check `day_of_week` values in database

---

**Version:** 2.0  
**Last Updated:** 2024  
**Author:** Lab Server Development Team
